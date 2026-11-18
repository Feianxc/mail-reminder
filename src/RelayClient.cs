using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace OfficeSignal
{
    internal sealed class PairCodeInfo
    {
        public string Code { get; set; }
        public string Subnet { get; set; }
        public int ExpiresIn { get; set; }
    }

    internal sealed class RelayClient : IDisposable
    {
        private sealed class Outgoing
        {
            public string Packet;
            public DateTime QueuedUtc;
        }

        private readonly LanNode node;
        private readonly TeamConfig team;
        private readonly Uri endpoint;
        private readonly HttpClient http;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly SemaphoreSlim registrationGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim available = new SemaphoreSlim(0, 1);
        private readonly ConcurrentQueue<Outgoing> outbox = new ConcurrentQueue<Outgoing>();
        private readonly object sessionGate = new object();
        private string token;
        private long cursor;
        private bool started;
        private bool disposed;
        public string Subnet { get; private set; }
        public string PublicUrl { get { return endpoint.AbsoluteUri; } }
        public string Status { get; private set; }
        public event Action Changed;

        public RelayClient(LanNode node, TeamConfig team)
        {
            this.node = node;
            this.team = team;
            endpoint = new Uri((team.RelayUrl ?? "").TrimEnd('/') + "/", UriKind.Absolute);
            if (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && endpoint.IsLoopback))
                throw new ArgumentException("手机同步服务需要 HTTPS 地址。");
            Subnet = ResolveSubnet(team.BroadcastAddress);
            http = new HttpClient(new HttpClientHandler { UseProxy = !endpoint.IsLoopback, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(40), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MailReminder/1.3.0");
            Status = "准备连接 FEIAN";
        }

        public void Start()
        {
            if (disposed) throw new ObjectDisposedException("RelayClient");
            if (started) return;
            started = true;
            node.RelayPacketReady += QueuePacket;
            Task.Run(() => PollLoop(lifetime.Token));
            Task.Run(() => SendLoop(lifetime.Token));
        }

        public async Task<PairCodeInfo> RequestPairCodeAsync(CancellationToken cancellationToken)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token))
            {
                string credential = await EnsureSession(linked.Token).ConfigureAwait(false);
                var result = await Request("api/desktop/pair-code", new { }, credential, linked.Token).ConfigureAwait(false);
                return new PairCodeInfo {
                    Code = Convert.ToString(result["code"]),
                    Subnet = Convert.ToString(result["subnet"]),
                    ExpiresIn = Convert.ToInt32(result["expiresIn"])
                };
            }
        }

        private void QueuePacket(string packet)
        {
            if (disposed) return;
            while (outbox.Count >= 256) { Outgoing old; outbox.TryDequeue(out old); }
            outbox.Enqueue(new Outgoing { Packet = packet, QueuedUtc = DateTime.UtcNow });
            try { available.Release(); } catch (SemaphoreFullException) { }
        }

        private async Task<string> EnsureSession(CancellationToken cancellationToken)
        {
            lock (sessionGate) { if (token != null) return token; }
            await registrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (sessionGate) { if (token != null) return token; }
                var response = await Request("api/desktop/register", new {
                    roomKey = team.RoomKey, subnet = Subnet, nodeId = node.NodeId,
                    name = node.DisplayName, roomName = team.RoomName
                }, null, cancellationToken).ConfigureAwait(false);
                string received = Convert.ToString(response["token"]);
                if (string.IsNullOrWhiteSpace(received)) throw new InvalidOperationException("FEIAN 注册响应缺少设备凭据。");
                lock (sessionGate)
                {
                    cursor = Convert.ToInt64(response["cursor"]);
                    token = received;
                }
                SetStatus("已连接 FEIAN，手机同步可用");
                return received;
            }
            finally { registrationGate.Release(); }
        }

        private async Task PollLoop(CancellationToken cancellationToken)
        {
            int retry = 1;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    string credential = await EnsureSession(cancellationToken).ConfigureAwait(false);
                    long since;
                    lock (sessionGate) since = cursor;
                    var response = await Request("api/desktop/poll?cursor=" + since, null, credential, cancellationToken).ConfigureAwait(false);
                    var packets = response["packets"] as IEnumerable;
                    if (packets == null || response["packets"] is string) throw new InvalidOperationException("FEIAN 中转响应格式不正确。");
                    foreach (var packet in packets)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!(packet is string)) throw new InvalidOperationException("FEIAN 中转报文格式不正确。");
                        node.ReceiveRelayPacket((string)packet);
                    }
                    lock (sessionGate)
                    {
                        if (token == credential) cursor = Convert.ToInt64(response["cursor"]);
                    }
                    retry = 1;
                    SetStatus("已连接 FEIAN，手机同步可用");
                }
                catch (Exception ex)
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    Log.Write("手机同步收取失败 · " + ex.Message);
                    SetStatus("手机同步暂未连接，正在重试");
                    await RetryDelay(retry, cancellationToken).ConfigureAwait(false);
                    retry = Math.Min(30, retry * 2);
                }
            }
        }

        private async Task SendLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await available.WaitAsync(cancellationToken).ConfigureAwait(false);
                    Outgoing outgoing;
                    while (outbox.TryDequeue(out outgoing))
                    {
                        if (DateTime.UtcNow - outgoing.QueuedUtc > TimeSpan.FromSeconds(90)) continue;
                        string credential = await EnsureSession(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            await Request("api/desktop/packet", new { packet = outgoing.Packet }, credential, cancellationToken).ConfigureAwait(false);
                        }
                        catch
                        {
                            if (DateTime.UtcNow - outgoing.QueuedUtc < TimeSpan.FromSeconds(80)) outbox.Enqueue(outgoing);
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    Log.Write("手机同步发送失败 · " + ex.Message);
                    SetStatus("手机同步暂未连接，正在重试");
                    await RetryDelay(3, cancellationToken).ConfigureAwait(false);
                    if (!outbox.IsEmpty) { try { available.Release(); } catch (SemaphoreFullException) { } }
                }
            }
        }

        private async Task<Dictionary<string, object>> Request(string path, object body, string credential, CancellationToken cancellationToken)
        {
            using (var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, new Uri(endpoint, path)))
            {
                if (credential != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                if (body != null) request.Content = new StringContent(new JavaScriptSerializer().Serialize(body), Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        lock (sessionGate) { if (token == credential) token = null; }
                    }
                    response.EnsureSuccessStatusCode();
                    string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(json);
                }
            }
        }

        private static async Task RetryDelay(int seconds, CancellationToken cancellationToken)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        private void SetStatus(string value)
        {
            if (disposed || Status == value) return;
            Status = value;
            var changed = Changed;
            if (changed == null) return;
            foreach (Action handler in changed.GetInvocationList())
                try { handler(); } catch (Exception ex) { Log.Write("手机同步状态刷新失败 · " + ex.Message); }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            node.RelayPacketReady -= QueuePacket;
            lifetime.Cancel();
            http.Dispose();
        }

        private static string ResolveSubnet(string broadcast)
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            {
                foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || address.IPv4Mask == null) continue;
                    byte[] ip = address.Address.GetAddressBytes();
                    byte[] mask = address.IPv4Mask.GetAddressBytes();
                    byte[] end = ip.Select((b, i) => (byte)(b | ~mask[i])).ToArray();
                    if (new IPAddress(end).ToString() != broadcast) continue;
                    byte[] start = ip.Select((b, i) => (byte)(b & mask[i])).ToArray();
                    int prefix = mask.Sum(b => Convert.ToString(b, 2).Count(c => c == '1'));
                    return new IPAddress(start) + "/" + prefix;
                }
            }
            byte[] fallback = IPAddress.Parse(broadcast).GetAddressBytes();
            fallback[3] = 0;
            return new IPAddress(fallback) + "/24";
        }
    }
}
