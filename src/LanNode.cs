using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace OfficeSignal
{
    public sealed class NetworkOptions
    {
        public string NodeId { get; set; }
        public string DisplayName { get; set; }
        public byte[] RoomKey { get; set; }
        public int Port { get; set; }
        public IPAddress ListenAddress { get; set; }
        public IPEndPoint[] BroadcastTargets { get; set; }

        public NetworkOptions()
        {
            NodeId = Guid.NewGuid().ToString("N");
            DisplayName = Environment.MachineName;
            Port = 45820;
            ListenAddress = IPAddress.Any;
            BroadcastTargets = new[] { new IPEndPoint(IPAddress.Parse("192.168.20.255"), Port) };
        }
    }

    public sealed class PeerInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Address { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }

    public sealed class IncomingAlert
    {
        public string Id { get; set; }
        public string SenderId { get; set; }
        public string SenderName { get; set; }
        public DateTime SentUtc { get; set; }
        public bool IsTest { get; set; }
    }

    public sealed class SentAlert
    {
        public string Id { get; set; }
        public DateTime SentUtc { get; set; }
        public bool IsTest { get; set; }
        public PeerInfo[] ExpectedPeers { get; set; }
        public string[] ReceivedIds { get; set; }
        public string[] SeenIds { get; set; }

        public SentAlert()
        {
            ExpectedPeers = new PeerInfo[0];
            ReceivedIds = new string[0];
            SeenIds = new string[0];
        }
    }

    // Callbacks run on transport threads. A desktop client must marshal them to its UI thread.
    public sealed class LanNode : IDisposable
    {
        private const int MaximumDatagramBytes = 4096;
        private const int MaximumPeers = 128;
        private const int MaximumIncoming = 1024;
        private const int MaximumSent = 32;
        private const int MaximumRelayFingerprints = 2048;
        private const long PeerLifetimeMs = 12000;
        private const long RetryLifetimeMs = 12000;
        private const long CacheLifetimeMs = 300000;
        private const long ClockToleranceMs = 120000;
        private const long RelayFingerprintLifetimeMs = 150000;
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly UTF8Encoding WireEncoding = new UTF8Encoding(false, true);
        private readonly object stateLock = new object();
        private readonly object sendLock = new object();
        private readonly NetworkOptions options;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Dictionary<string, PeerState> peers = new Dictionary<string, PeerState>();
        private readonly Dictionary<string, IncomingState> incoming = new Dictionary<string, IncomingState>();
        private readonly Dictionary<string, SentState> sent = new Dictionary<string, SentState>();
        private readonly Dictionary<string, long> relayFingerprints = new Dictionary<string, long>();
        private UdpClient socket;
        private Thread receiver;
        private Timer timer;
        private volatile bool disposed;
        private bool started;
        private long lastHelloMs = -3000;
        private long lastErrorMs = -5000;
        private string lastSentId;
        private int ticking;

        public event Action<IncomingAlert> AlertReceived;
        public event Action Changed;
        public event Action<string> Error;
        public event Action<string> RelayPacketReady;

        public string NodeId { get { return options.NodeId; } }
        public string DisplayName { get { lock (stateLock) return options.DisplayName; } }

        public LanNode(NetworkOptions configuration)
        {
            if (configuration == null) throw new ArgumentNullException("configuration");
            if (!ValidIdentifier(configuration.NodeId)) throw new ArgumentException("电脑标识无效。");
            if (!ValidName(configuration.DisplayName)) throw new ArgumentException("显示名称需为 1 至 64 个字符。");
            if (configuration.RoomKey == null || configuration.RoomKey.Length < 16 || configuration.RoomKey.Length > 128)
                throw new ArgumentException("团队密钥需为 16 至 128 字节。");
            if (configuration.Port < 1 || configuration.Port > 65535) throw new ArgumentException("端口无效。");
            if (configuration.ListenAddress == null || configuration.ListenAddress.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("监听地址必须是 IPv4。");
            if (configuration.BroadcastTargets == null || configuration.BroadcastTargets.Length > 32 ||
                configuration.BroadcastTargets.Any(t => t == null || t.AddressFamily != AddressFamily.InterNetwork || t.Port < 1))
                throw new ArgumentException("发现地址无效。");
            options = new NetworkOptions
            {
                NodeId = configuration.NodeId,
                DisplayName = configuration.DisplayName,
                RoomKey = (byte[])configuration.RoomKey.Clone(),
                Port = configuration.Port,
                ListenAddress = new IPAddress(configuration.ListenAddress.GetAddressBytes()),
                BroadcastTargets = configuration.BroadcastTargets.Select(CloneEndpoint).ToArray()
            };
        }

        public void Start()
        {
            lock (stateLock)
            {
                if (disposed) throw new ObjectDisposedException("LanNode");
                if (started) throw new InvalidOperationException("局域网通信已经启动。");
                UdpClient candidate = new UdpClient(AddressFamily.InterNetwork);
                try
                {
                    candidate.ExclusiveAddressUse = true;
                    candidate.EnableBroadcast = true;
                    candidate.Client.ReceiveTimeout = 1000;
                    candidate.Client.Bind(new IPEndPoint(options.ListenAddress, options.Port));
                }
                catch
                {
                    candidate.Close();
                    throw;
                }
                socket = candidate;
                started = true;
                receiver = new Thread(ReceiveLoop) { IsBackground = true, Name = "OfficeSignal LAN receiver" };
                receiver.Start();
                timer = new Timer(Tick, null, 0, 500);
            }
        }

        public void UpdateDisplayName(string name)
        {
            if (!ValidName(name)) throw new ArgumentException("显示名称需为 1 至 64 个字符，且不能含控制字符。", "name");
            bool announce;
            lock (stateLock)
            {
                if (disposed) throw new ObjectDisposedException("LanNode");
                options.DisplayName = name;
                announce = started;
            }
            if (announce) SendToTeam(CreatePacket("hello", null, null, DateTime.UtcNow, false));
            RaiseChanged();
        }

        // The relay transports the original signed envelope. It never receives the room key here.
        public void ReceiveRelayPacket(string envelope)
        {
            if (disposed || String.IsNullOrEmpty(envelope)) return;
            lock (stateLock) EnsureRunning();
            byte[] bytes;
            try
            {
                if (WireEncoding.GetByteCount(envelope) > MaximumDatagramBytes) return;
                bytes = WireEncoding.GetBytes(envelope);
            }
            catch (EncoderFallbackException) { return; }
            WirePacket packet = Decode(bytes);
            if (packet == null || packet.SenderId == options.NodeId) return;
            bool forwardToLan = packet.Type != "hello" && RememberRelayPacket(bytes);
            Handle(packet, null);
            if (forwardToLan)
                foreach (IPEndPoint endpoint in GetLanEndpoints()) SendDatagram(bytes, endpoint);
        }

        public PeerInfo[] GetPeers()
        {
            lock (stateLock)
                return peers.Values.Where(p => clock.ElapsedMilliseconds - p.LastSeenMs < PeerLifetimeMs)
                    .Select(p => CopyPeer(p.Info)).OrderBy(p => p.Name, StringComparer.CurrentCulture).ToArray();
        }

        public SentAlert SendAlert(bool isTest)
        {
            SentState state;
            lock (stateLock)
            {
                EnsureRunning();
                if (sent.Count >= MaximumSent)
                    sent.Remove(sent.OrderBy(p => p.Value.CreatedMs).First().Key);
                long now = clock.ElapsedMilliseconds;
                state = new SentState
                {
                    Id = Guid.NewGuid().ToString("N"),
                    SentUtc = DateTime.UtcNow,
                    IsTest = isTest,
                    CreatedMs = now,
                    LastRetryMs = now
                };
                foreach (PeerState peer in peers.Values.Where(p => now - p.LastSeenMs < PeerLifetimeMs))
                    state.Expected[peer.Info.Id] = CopyPeer(peer.Info);
                sent.Add(state.Id, state);
                lastSentId = state.Id;
            }
            SendToTeam(CreatePacket("alert", state.Id, null, state.SentUtc, state.IsTest));
            RaiseChanged();
            lock (stateLock) return CopySent(state);
        }

        public SentAlert GetLastSent()
        {
            lock (stateLock)
            {
                SentState state;
                return lastSentId != null && sent.TryGetValue(lastSentId, out state) ? CopySent(state) : null;
            }
        }

        public void MarkSeen(string alertId)
        {
            IncomingState state;
            lock (stateLock)
            {
                EnsureRunning();
                if (alertId == null || !incoming.TryGetValue(alertId, out state)) return;
                state.SeenAtMs = clock.ElapsedMilliseconds;
                state.LastSeenRetryMs = state.SeenAtMs;
            }
            SendPacket(CreatePacket("seen", state.Alert.Id, state.Alert.SenderId, state.Alert.SentUtc, state.Alert.IsTest), state.Endpoint);
        }

        private void Tick(object unused)
        {
            if (disposed || Interlocked.Exchange(ref ticking, 1) != 0) return;
            try
            {
                long now = clock.ElapsedMilliseconds;
                bool changed = false;
                List<WirePacket> broadcasts = new List<WirePacket>();
                List<KeyValuePair<WirePacket, IPEndPoint>> unicasts = new List<KeyValuePair<WirePacket, IPEndPoint>>();
                lock (stateLock)
                {
                    if (disposed) return;
                    if (now - lastHelloMs >= 3000)
                    {
                        lastHelloMs = now;
                        broadcasts.Add(CreatePacket("hello", null, null, DateTime.UtcNow, false));
                    }
                    foreach (string id in peers.Where(p => now - p.Value.LastSeenMs >= PeerLifetimeMs).Select(p => p.Key).ToArray())
                    {
                        peers.Remove(id);
                        changed = true;
                    }
                    foreach (SentState state in sent.Values)
                    {
                        if (now - state.CreatedMs <= RetryLifetimeMs && now - state.LastRetryMs >= 1000)
                        {
                            state.LastRetryMs = now;
                            broadcasts.Add(CreatePacket("alert", state.Id, null, state.SentUtc, state.IsTest));
                        }
                    }
                    foreach (IncomingState state in incoming.Values)
                    {
                        if (state.SeenAtMs >= 0 && now - state.SeenAtMs <= RetryLifetimeMs && now - state.LastSeenRetryMs >= 1000)
                        {
                            state.LastSeenRetryMs = now;
                            unicasts.Add(new KeyValuePair<WirePacket, IPEndPoint>(
                                CreatePacket("seen", state.Alert.Id, state.Alert.SenderId, state.Alert.SentUtc, state.Alert.IsTest), state.Endpoint));
                        }
                    }
                    // An unread desktop reminder can stay open for hours. Keep its return route until
                    // acknowledged, subject to the bounded incoming cache, so a later click still works.
                    foreach (string id in incoming.Where(p => p.Value.SeenAtMs >= 0 && now - p.Value.SeenAtMs > CacheLifetimeMs).Select(p => p.Key).ToArray()) incoming.Remove(id);
                    foreach (string id in sent.Where(p => p.Key != lastSentId && now - p.Value.CreatedMs > CacheLifetimeMs).Select(p => p.Key).ToArray()) sent.Remove(id);
                    foreach (string fingerprint in relayFingerprints.Where(p => now - p.Value > RelayFingerprintLifetimeMs).Select(p => p.Key).ToArray()) relayFingerprints.Remove(fingerprint);
                }
                foreach (WirePacket packet in broadcasts) SendToTeam(packet);
                foreach (KeyValuePair<WirePacket, IPEndPoint> unicast in unicasts) SendPacket(unicast.Key, unicast.Value);
                if (changed) RaiseChanged();
            }
            catch (Exception exception)
            {
                if (!disposed) RaiseError("局域网通信处理失败。" + exception.Message);
            }
            finally { Interlocked.Exchange(ref ticking, 0); }
        }

        private void ReceiveLoop()
        {
            while (!disposed)
            {
                try
                {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] bytes = socket.Receive(ref remote);
                    if (bytes.Length == 0 || bytes.Length > MaximumDatagramBytes) continue;
                    WirePacket packet = Decode(bytes);
                    if (packet != null && packet.SenderId != options.NodeId)
                    {
                        bool relayEcho = IsKnownRelayPacket(bytes);
                        Handle(packet, relayEcho ? null : remote);
                        if (packet.Type != "hello" && !relayEcho) RaiseRelayPacket(WireEncoding.GetString(bytes));
                    }
                }
                catch (SocketException exception)
                {
                    if (disposed) return;
                    if (exception.SocketErrorCode != SocketError.TimedOut && exception.SocketErrorCode != SocketError.ConnectionReset)
                    {
                        RaiseError("接收局域网消息失败。" + exception.Message);
                        Thread.Sleep(100);
                    }
                }
                catch (ObjectDisposedException) { if (disposed) return; throw; }
                catch (Exception exception)
                {
                    if (!disposed) RaiseError("处理局域网消息失败。" + exception.Message);
                }
            }
        }

        private void Handle(WirePacket packet, IPEndPoint remote)
        {
            bool changed = false;
            bool replyHello = false;
            IncomingAlert newAlert = null;
            WirePacket reply = null;
            IPEndPoint replyEndpoint;
            lock (stateLock)
            {
                if (disposed) return;
                PeerState peer;
                if (!peers.TryGetValue(packet.SenderId, out peer))
                {
                    if (peers.Count >= MaximumPeers) return;
                    peer = new PeerState();
                    peers.Add(packet.SenderId, peer);
                    changed = true;
                    replyHello = true;
                }
                if (remote != null) peer.Endpoint = CloneEndpoint(remote);
                string address = peer.Endpoint == null ? "云端" : peer.Endpoint.Address.ToString();
                changed |= peer.Info == null || peer.Info.Name != packet.Name || peer.Info.Address != address;
                peer.Info = new PeerInfo { Id = packet.SenderId, Name = packet.Name, Address = address, LastSeenUtc = DateTime.UtcNow };
                peer.LastSeenMs = clock.ElapsedMilliseconds;
                replyEndpoint = CloneEndpoint(peer.Endpoint);
                foreach (SentState existing in sent.Values)
                    if (existing.Expected.ContainsKey(packet.SenderId)) existing.Expected[packet.SenderId] = CopyPeer(peer.Info);

                if (packet.Type == "alert")
                {
                    IncomingState state;
                    if (!incoming.TryGetValue(packet.Id, out state))
                    {
                        if (incoming.Count >= MaximumIncoming) incoming.Remove(incoming.OrderBy(p => p.Value.CreatedMs).First().Key);
                        IncomingAlert alert = new IncomingAlert
                        {
                            Id = packet.Id, SenderId = packet.SenderId, SenderName = packet.Name,
                            SentUtc = Epoch.AddMilliseconds(packet.AlertTime), IsTest = packet.IsTest
                        };
                        state = new IncomingState { Alert = alert, CreatedMs = clock.ElapsedMilliseconds, Endpoint = CloneEndpoint(peer.Endpoint) };
                        incoming.Add(packet.Id, state);
                        newAlert = CopyAlert(alert);
                    }
                    if (state.Alert.SenderId != packet.SenderId) return;
                    if (peer.Endpoint != null) state.Endpoint = CloneEndpoint(peer.Endpoint);
                    reply = CreatePacket(state.SeenAtMs >= 0 ? "seen" : "ack", packet.Id, packet.SenderId, state.Alert.SentUtc, state.Alert.IsTest);
                }
                else if ((packet.Type == "ack" || packet.Type == "seen") && packet.TargetId == options.NodeId)
                {
                    SentState state;
                    if (sent.TryGetValue(packet.Id, out state))
                    {
                        if (!state.Expected.ContainsKey(packet.SenderId)) state.Expected.Add(packet.SenderId, CopyPeer(peer.Info));
                        changed |= state.Received.Add(packet.SenderId);
                        if (packet.Type == "seen") changed |= state.Seen.Add(packet.SenderId);
                    }
                }
            }
            if (replyHello) SendPacket(CreatePacket("hello", null, null, DateTime.UtcNow, false), replyEndpoint);
            if (reply != null) SendPacket(reply, replyEndpoint);
            if (newAlert != null) RaiseAlert(newAlert);
            if (changed) RaiseChanged();
        }

        private WirePacket CreatePacket(string type, string id, string targetId, DateTime alertTime, bool isTest)
        {
            return new WirePacket
            {
                Version = 1, Type = type, SenderId = options.NodeId, Name = DisplayName,
                Id = id, TargetId = targetId, Time = UtcMilliseconds(),
                AlertTime = (long)(alertTime.ToUniversalTime() - Epoch).TotalMilliseconds, IsTest = isTest
            };
        }

        private void SendToTeam(WirePacket packet)
        {
            if (disposed) return;
            byte[] bytes = EncodePacket(packet);
            if (bytes == null) return;
            foreach (IPEndPoint endpoint in GetLanEndpoints()) SendDatagram(bytes, endpoint);
            RaiseRelayPacket(WireEncoding.GetString(bytes));
        }

        private IPEndPoint[] GetLanEndpoints()
        {
            lock (stateLock)
                return options.BroadcastTargets.Concat(peers.Values.Select(p => p.Endpoint)).Where(p => p != null)
                    .GroupBy(p => p.ToString()).Select(p => p.First()).ToArray();
        }

        private bool RememberRelayPacket(byte[] bytes)
        {
            string fingerprint = PacketFingerprint(bytes);
            lock (stateLock)
            {
                long now = clock.ElapsedMilliseconds;
                long receivedAt;
                if (relayFingerprints.TryGetValue(fingerprint, out receivedAt) && now - receivedAt <= RelayFingerprintLifetimeMs)
                    return false;
                if (relayFingerprints.Count >= MaximumRelayFingerprints)
                    relayFingerprints.Remove(relayFingerprints.OrderBy(p => p.Value).First().Key);
                relayFingerprints[fingerprint] = now;
                return true;
            }
        }

        private bool IsKnownRelayPacket(byte[] bytes)
        {
            string fingerprint = PacketFingerprint(bytes);
            lock (stateLock)
            {
                long receivedAt;
                return relayFingerprints.TryGetValue(fingerprint, out receivedAt) &&
                    clock.ElapsedMilliseconds - receivedAt <= RelayFingerprintLifetimeMs;
            }
        }

        private static string PacketFingerprint(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(bytes));
        }

        private void SendPacket(WirePacket packet, IPEndPoint target)
        {
            if (disposed) return;
            byte[] bytes = EncodePacket(packet);
            if (bytes == null) return;
            if (target != null) SendDatagram(bytes, target);
            RaiseRelayPacket(WireEncoding.GetString(bytes));
        }

        private byte[] EncodePacket(WirePacket packet)
        {
            try
            {
                JavaScriptSerializer serializer = NewSerializer();
                string payload = serializer.Serialize(packet);
                byte[] payloadBytes = WireEncoding.GetBytes(payload);
                string signature;
                using (HMACSHA256 hmac = new HMACSHA256(options.RoomKey)) signature = Convert.ToBase64String(hmac.ComputeHash(payloadBytes));
                byte[] bytes = WireEncoding.GetBytes(serializer.Serialize(new WireEnvelope { Payload = payload, Signature = signature }));
                if (bytes.Length > MaximumDatagramBytes) throw new InvalidOperationException("消息超过 4096 字节上限。");
                return bytes;
            }
            catch (Exception exception)
            {
                if (!disposed) RaiseError("生成局域网消息失败。" + exception.Message);
                return null;
            }
        }

        private void SendDatagram(byte[] bytes, IPEndPoint target)
        {
            try
            {
                lock (sendLock)
                {
                    if (disposed || socket == null) return;
                    socket.Send(bytes, bytes.Length, target);
                }
            }
            catch (Exception exception)
            {
                if (!disposed) RaiseError("发送局域网消息失败。" + exception.Message);
            }
        }

        private WirePacket Decode(byte[] bytes)
        {
            try
            {
                JavaScriptSerializer serializer = NewSerializer();
                WireEnvelope envelope = serializer.Deserialize<WireEnvelope>(WireEncoding.GetString(bytes));
                if (envelope == null || String.IsNullOrEmpty(envelope.Payload) || envelope.Payload.Length > 3000 ||
                    envelope.Signature == null || envelope.Signature.Length != 44) return null;
                byte[] provided = Convert.FromBase64String(envelope.Signature);
                byte[] expected;
                using (HMACSHA256 hmac = new HMACSHA256(options.RoomKey)) expected = hmac.ComputeHash(WireEncoding.GetBytes(envelope.Payload));
                if (provided.Length != expected.Length) return null;
                int difference = 0;
                for (int i = 0; i < provided.Length; i++) difference |= provided[i] ^ expected[i];
                if (difference != 0) return null;
                WirePacket packet = serializer.Deserialize<WirePacket>(envelope.Payload);
                long now = UtcMilliseconds();
                if (packet == null || packet.Version != 1 || !ValidIdentifier(packet.SenderId) || !ValidName(packet.Name) ||
                    packet.Time < now - ClockToleranceMs || packet.Time > now + ClockToleranceMs) return null;
                if (packet.Type == "hello") return packet;
                if (packet.Type != "alert" && packet.Type != "ack" && packet.Type != "seen") return null;
                Guid alertId;
                if (!Guid.TryParseExact(packet.Id, "N", out alertId)) return null;
                if (packet.Type == "alert" &&
                    (packet.AlertTime < now - ClockToleranceMs || packet.AlertTime > now + ClockToleranceMs)) return null;
                if (packet.Type != "alert" && !ValidIdentifier(packet.TargetId)) return null;
                return packet;
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (FormatException) { return null; }
            catch (OverflowException) { return null; }
        }

        private static JavaScriptSerializer NewSerializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = MaximumDatagramBytes, RecursionLimit = 8 };
        }

        private static bool ValidIdentifier(string value)
        {
            return !String.IsNullOrEmpty(value) && value.Length <= 64 &&
                value.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_');
        }

        private static bool ValidName(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(Char.IsControl)) return false;
            try { WireEncoding.GetByteCount(value); return true; }
            catch (EncoderFallbackException) { return false; }
        }

        private static long UtcMilliseconds() { return (long)(DateTime.UtcNow - Epoch).TotalMilliseconds; }
        private static IPEndPoint CloneEndpoint(IPEndPoint value) { return value == null ? null : new IPEndPoint(new IPAddress(value.Address.GetAddressBytes()), value.Port); }
        private static PeerInfo CopyPeer(PeerInfo peer) { return new PeerInfo { Id = peer.Id, Name = peer.Name, Address = peer.Address, LastSeenUtc = peer.LastSeenUtc }; }
        private static IncomingAlert CopyAlert(IncomingAlert alert) { return new IncomingAlert { Id = alert.Id, SenderId = alert.SenderId, SenderName = alert.SenderName, SentUtc = alert.SentUtc, IsTest = alert.IsTest }; }
        private static SentAlert CopySent(SentState state)
        {
            return new SentAlert
            {
                Id = state.Id, SentUtc = state.SentUtc, IsTest = state.IsTest,
                ExpectedPeers = state.Expected.Values.Select(CopyPeer).OrderBy(p => p.Name, StringComparer.CurrentCulture).ToArray(),
                ReceivedIds = state.Received.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                SeenIds = state.Seen.OrderBy(id => id, StringComparer.Ordinal).ToArray()
            };
        }

        private void EnsureRunning()
        {
            if (disposed) throw new ObjectDisposedException("LanNode");
            if (!started) throw new InvalidOperationException("局域网通信尚未启动。");
        }

        private void RaiseChanged()
        {
            Action handlers = Changed;
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList())
                try { handler(); } catch (Exception exception) { RaiseError("刷新连接状态失败。" + exception.Message); }
        }

        private void RaiseAlert(IncomingAlert alert)
        {
            Action<IncomingAlert> handlers = AlertReceived;
            if (handlers == null) return;
            foreach (Action<IncomingAlert> handler in handlers.GetInvocationList())
                try { handler(CopyAlert(alert)); } catch (Exception exception) { RaiseError("显示提醒失败。" + exception.Message); }
        }

        private void RaiseRelayPacket(string envelope)
        {
            if (disposed) return;
            Action<string> handlers = RelayPacketReady;
            if (handlers == null) return;
            foreach (Action<string> handler in handlers.GetInvocationList())
                try { handler(envelope); } catch (Exception exception) { RaiseError("手机中转消息发送失败。" + exception.Message); }
        }

        private void RaiseError(string message)
        {
            lock (stateLock)
            {
                if (clock.ElapsedMilliseconds - lastErrorMs < 5000) return;
                lastErrorMs = clock.ElapsedMilliseconds;
            }
            Action<string> handlers = Error;
            if (handlers == null) { Trace.WriteLine(message); return; }
            foreach (Action<string> handler in handlers.GetInvocationList())
                try { handler(message); } catch (Exception exception) { Trace.WriteLine(exception); }
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                if (disposed) return;
                disposed = true;
            }
            if (timer != null) timer.Dispose();
            lock (sendLock) { if (socket != null) socket.Close(); }
            if (receiver != null && Thread.CurrentThread != receiver) receiver.Join(1500);
        }

        private sealed class PeerState
        {
            public PeerInfo Info;
            public IPEndPoint Endpoint;
            public long LastSeenMs;
        }

        private sealed class IncomingState
        {
            public IncomingAlert Alert;
            public IPEndPoint Endpoint;
            public long CreatedMs;
            public long SeenAtMs = -1;
            public long LastSeenRetryMs;
        }

        private sealed class SentState
        {
            public string Id;
            public DateTime SentUtc;
            public bool IsTest;
            public long CreatedMs;
            public long LastRetryMs;
            public readonly Dictionary<string, PeerInfo> Expected = new Dictionary<string, PeerInfo>();
            public readonly HashSet<string> Received = new HashSet<string>();
            public readonly HashSet<string> Seen = new HashSet<string>();
        }

        public sealed class WireEnvelope
        {
            public string Payload { get; set; }
            public string Signature { get; set; }
        }

        public sealed class WirePacket
        {
            public int Version { get; set; }
            public string Type { get; set; }
            public string SenderId { get; set; }
            public string Name { get; set; }
            public string Id { get; set; }
            public string TargetId { get; set; }
            public long Time { get; set; }
            public long AlertTime { get; set; }
            public bool IsTest { get; set; }
        }
    }
}
