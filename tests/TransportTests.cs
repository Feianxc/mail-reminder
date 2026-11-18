using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using OfficeSignal;

public static class TransportTests
{
    private static int passed;
    private static readonly byte[] RoomKey = Enumerable.Range(1, 32).Select(n => (byte)n).ToArray();
    private static readonly List<LanNode> Nodes = new List<LanNode>();

    public static int Main(string[] args)
    {
        try
        {
            bool broadcast = args.Length == 1 && args[0] == "--broadcast";
            if (broadcast) RunLocalBroadcast(); else Run();
            Console.WriteLine("RESULT pass " + passed + " checks. " + (broadcast ? "Local NIC broadcast" : "Loopback UDP") + "; cross-PC delivery and Windows 10 desktop not run.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("RESULT fail " + exception);
            return 1;
        }
        finally
        {
            foreach (LanNode node in Nodes) node.Dispose();
        }
    }

    private static void RunLocalBroadcast()
    {
        bool hasOfficeNetwork = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                a.Address.ToString().StartsWith("192.168.20.", StringComparison.Ordinal) && a.IPv4Mask.ToString() == "255.255.255.0");
        if (!hasOfficeNetwork) throw new InvalidOperationException("Local NIC broadcast check requires an active 192.168.20.0/24 adapter.");
        int[] ports = ReservePorts(2);
        using (var receiver = new LanNode(new NetworkOptions {
            NodeId = "broadcast-receiver", DisplayName = "broadcast-receiver", RoomKey = RoomKey,
            ListenAddress = IPAddress.Any, Port = ports[0], BroadcastTargets = new IPEndPoint[0]
        }))
        using (var sender = new LanNode(new NetworkOptions {
            NodeId = "broadcast-sender", DisplayName = "broadcast-sender", RoomKey = RoomKey,
            ListenAddress = IPAddress.Any, Port = ports[1],
            BroadcastTargets = new[] { new IPEndPoint(IPAddress.Parse("192.168.20.255"), ports[0]) }
        }))
        {
            receiver.Start(); sender.Start();
            Wait(() => receiver.GetPeers().Length == 1 && sender.GetPeers().Length == 1, 6000,
                "real NIC directed broadcast discovers peer before any known unicast address");
            SentAlert alert = sender.SendAlert(true);
            Wait(() => sender.GetLastSent().ReceivedIds.Length == 1, 5000, "real NIC discovered peer receives test alert and returns ACK");
            receiver.MarkSeen(alert.Id);
            Wait(() => sender.GetLastSent().SeenIds.Length == 1, 5000, "real NIC discovered peer returns human confirmation");
        }
    }

    private static void Run()
    {
        int[] ports = ReservePorts(6);
        LanNode solo = CreateNode("solo", ports[0], new int[0], RoomKey);
        solo.Start();
        Check(solo.GetPeers().Length == 0, "empty peer snapshot is an array");
        SentAlert empty = solo.SendAlert(true);
        Check(empty.ExpectedPeers.Length == 0 && empty.ReceivedIds.Length == 0 && empty.SeenIds.Length == 0,
            "zero recipients remain zero and have no delivery claim");
        CheckCollectionShapes(empty, 0);
        solo.Dispose();

        LanNode a = CreateNode("sender", ports[1], new[] { ports[2], ports[3], ports[4], ports[5] }, RoomKey);
        LanNode b = CreateNode("receiver-b", ports[2], new[] { ports[1] }, RoomKey);
        LanNode c = CreateNode("receiver-c", ports[3], new[] { ports[1] }, RoomKey);
        LanNode wrongRoom = CreateNode("other-room", ports[4], new[] { ports[1] }, Enumerable.Repeat((byte)99, 32).ToArray());
        LanNode late = CreateNode("late-receiver", ports[5], new[] { ports[1] }, RoomKey);
        AlertCounter bAlerts = new AlertCounter();
        AlertCounter cAlerts = new AlertCounter();
        AlertCounter aAlerts = new AlertCounter();
        AlertCounter lateAlerts = new AlertCounter();
        b.AlertReceived += bAlerts.Record;
        c.AlertReceived += cAlerts.Record;
        a.AlertReceived += aAlerts.Record;
        late.AlertReceived += lateAlerts.Record;
        EnvelopeLog aOutbound = new EnvelopeLog();
        EnvelopeLog bOutbound = new EnvelopeLog();
        a.RelayPacketReady += aOutbound.Record;
        b.RelayPacketReady += bOutbound.Record;
        List<string> errors = new List<string>();
        foreach (LanNode node in new[] { a, b, c, wrongRoom, late })
            node.Error += error => { lock (errors) errors.Add(error); };
        a.Start();
        b.Start();
        wrongRoom.Start();
        Wait(() => a.GetPeers().Length == 1 && b.GetPeers().Length == 1, 5000, "two-way discovery");
        Check(a.GetPeers().Single().Name == "receiver-b", "wrong team key cannot join presence");
        Check(a.NodeId == "sender" && a.DisplayName == "sender", "read-only identity and current display name");
        a.UpdateDisplayName("行政协作组·张三");
        Wait(() => b.GetPeers().Single(p => p.Id == a.NodeId).Name == "行政协作组·张三", 1000,
            "Chinese display-name update reaches real UDP peer immediately without restart");
        bool invalidNameRejected = false;
        try { a.UpdateDisplayName("张三\n新名字"); } catch (ArgumentException) { invalidNameRejected = true; }
        Check(invalidNameRejected && a.DisplayName == "行政协作组·张三", "control characters are rejected without changing current name");

        SentAlert one = a.SendAlert(true);
        Wait(() => a.GetLastSent().ReceivedIds.Length == 1, 4000, "one receiver ACK");
        Check(one.ExpectedPeers.Length == 1 && one.IsTest, "single-recipient test alert snapshot");
        CheckCollectionShapes(a.GetLastSent(), 1);
        Wait(() => bAlerts.Get(one.Id) == 1 && bAlerts.WasTest(one.Id), 4000,
            "test flag and one incoming callback");
        Check(bAlerts.SenderName(one.Id) == "行政协作组·张三", "Chinese sender name survives signed UTF-8 alert transport");
        Check(aOutbound.Count("alert", one.Id) == 1, "team send emits one relay envelope despite multiple UDP targets");
        Wait(() => bOutbound.Count("alert", one.Id) == 1, 1000, "received LAN alert is exposed once as the original relay envelope");
        string originalEnvelope = aOutbound.Last("alert", one.Id);
        Check(bOutbound.Last("alert", one.Id) == originalEnvelope, "LAN-to-relay bridge preserves original signed JSON");
        b.ReceiveRelayPacket(originalEnvelope);
        Check(bAlerts.Get(one.Id) == 1 && bOutbound.Count("alert", one.Id) == 1,
            "same alert through LAN and relay notifies once and relay input is not echoed");
        Check(b.GetPeers().Single(p => p.Id == a.NodeId).Address == "127.0.0.1", "cloud echo preserves known LAN route");
        b.MarkSeen(one.Id);
        Wait(() => a.GetLastSent().SeenIds.Length == 1, 4000, "human acknowledgement");

        c.Start();
        Wait(() => a.GetPeers().Length == 2, 5000, "third machine discovery");
        SentAlert many = a.SendAlert(false);
        Wait(() => a.GetLastSent().ReceivedIds.Length == 2 && bAlerts.Get(many.Id) == 1 && cAlerts.Get(many.Id) == 1,
            5000, "one-to-many delivery");
        Check(!bAlerts.WasTest(many.Id) && !cAlerts.WasTest(many.Id), "real alert flag survives transport");
        b.MarkSeen(many.Id);
        c.MarkSeen(many.Id);
        Wait(() => a.GetLastSent().SeenIds.Length == 2, 4000, "multiple human acknowledgements");
        SentAlert manySnapshot = a.GetLastSent();
        CheckCollectionShapes(manySnapshot, 2);
        manySnapshot.ExpectedPeers[0].Name = "modified outside node";
        manySnapshot.ReceivedIds[0] = "modified outside node";
        Check(a.GetLastSent().ExpectedPeers.All(p => p.Name != "modified outside node") &&
            a.GetLastSent().ReceivedIds.All(id => id != "modified outside node"), "snapshots do not expose internal state");
        Thread.Sleep(2300);
        Check(bAlerts.Get(many.Id) == 1 && cAlerts.Get(many.Id) == 1, "automatic retransmissions are deduplicated");

        int beforeNoise = bAlerts.Total;
        int peersBeforeNoise = b.GetPeers().Length;
        InjectMalformedPackets(ports[2]);
        wrongRoom.SendAlert(false);
        Thread.Sleep(600);
        Check(aAlerts.Total == 0, "wrong-key alert is rejected");
        Check(bAlerts.Total == beforeNoise && b.GetPeers().Length == peersBeforeNoise,
            "malformed, stale, future and invalid-field packets are rejected before presence or alerts");

        SentAlert retry = a.SendAlert(true);
        Thread.Sleep(1300);
        late.Start();
        Wait(() => lateAlerts.Get(retry.Id) == 1 && a.GetLastSent().ReceivedIds.Length == 3, 6000,
            "retry reaches a machine that missed initial send");
        Check(a.GetLastSent().ExpectedPeers.Length == 3, "new confirmed receiver joins delivery denominator");
        late.MarkSeen(retry.Id);
        Wait(() => a.GetLastSent().SeenIds.Length == 1, 4000, "late receiver sends human acknowledgement");

        SentAlert aged = a.SendAlert(true);
        Wait(() => bAlerts.Get(aged.Id) == 1 && a.GetLastSent().ReceivedIds.Length == 3, 5000,
            "long-lived reminder reaches receivers");
        AgeUnreadReminder(b, aged.Id);
        b.MarkSeen(aged.Id);
        Wait(() => a.GetLastSent().SeenIds.Contains("receiver-b"), 4000,
            "unread reminder can be acknowledged after five-minute cleanup boundary");

        b.Dispose();
        Wait(() => a.GetPeers().All(p => p.Name != "receiver-b"), 15000, "offline peer expires");
        Check(a.GetPeers().Length == 2, "remaining peers stay online after one exits");
        lock (errors) Check(errors.Count == 0, "normal socket operation has no transport errors: " + String.Join(" | ", errors));

        using (UdpClient occupied = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            LanNode collision = CreateNode("collision", ((IPEndPoint)occupied.Client.LocalEndPoint).Port, new int[0], RoomKey);
            bool failedClearly = false;
            try { collision.Start(); } catch (SocketException) { failedClearly = true; }
            Check(failedClearly, "occupied port fails synchronously at Start");
        }
        bool rejectedAfterDispose = false;
        try { b.SendAlert(false); } catch (ObjectDisposedException) { rejectedAfterDispose = true; }
        Check(rejectedAfterDispose, "disposed transport rejects sending");

        RunRelay();
        RunLegacyBridge();
    }

    private static void RunRelay()
    {
        int[] ports = ReservePorts(2);
        LanNode left = CreateNode("relay-left", ports[0], new int[0], RoomKey);
        LanNode right = CreateNode("relay-right", ports[1], new int[0], RoomKey);
        AlertCounter rightAlerts = new AlertCounter();
        EnvelopeLog leftPackets = new EnvelopeLog();
        EnvelopeLog rightPackets = new EnvelopeLog();
        List<string> errors = new List<string>();
        left.Error += error => { lock (errors) errors.Add(error); };
        right.Error += error => { lock (errors) errors.Add(error); };
        right.AlertReceived += rightAlerts.Record;
        bool relayActive = false;
        bool failRelayHandler = false;
        left.RelayPacketReady += envelope => { if (failRelayHandler) throw new InvalidOperationException("simulated relay outage"); };
        left.RelayPacketReady += envelope => {
            leftPackets.Record(envelope);
            if (relayActive) right.ReceiveRelayPacket(envelope);
        };
        right.RelayPacketReady += envelope => {
            rightPackets.Record(envelope);
            if (relayActive) left.ReceiveRelayPacket(envelope);
        };
        left.Start(); right.Start(); relayActive = true;
        left.UpdateDisplayName("手机协作·李四");
        right.UpdateDisplayName("电脑接收·王五");
        Wait(() => left.GetPeers().Length == 1 && right.GetPeers().Length == 1, 1000,
            "relay-only peers discover each other with no UDP discovery target");
        Check(left.GetPeers().Single().Address == "云端" && right.GetPeers().Single().Address == "云端",
            "relay-only peer routes are identified as cloud");
        Check(right.GetPeers().Single().Name == "手机协作·李四", "Chinese name propagates through relay hello");
        SentAlert alert = left.SendAlert(false);
        Wait(() => rightAlerts.Get(alert.Id) == 1 && left.GetLastSent().ReceivedIds.Length == 1, 1000,
            "relay-only alert and ACK complete end to end");
        Check(leftPackets.Count("alert", alert.Id) == 1, "relay-only team send emits a single signed envelope");
        Check(rightAlerts.SenderName(alert.Id) == "手机协作·李四", "relay alert preserves Chinese sender name");
        CheckCollectionShapes(left.GetLastSent(), 1);
        right.MarkSeen(alert.Id);
        Wait(() => left.GetLastSent().SeenIds.Length == 1, 1000, "relay-only human confirmation returns to sender");
        right.UpdateDisplayName("电脑接收·赵六");
        Check(left.GetPeers().Single().Name == "电脑接收·赵六" && left.GetLastSent().ExpectedPeers.Single().Name == "电脑接收·赵六",
            "relay rename updates online peer and existing receipt snapshot immediately");
        int alertForwardCount = rightPackets.Count("alert", alert.Id);
        right.ReceiveRelayPacket(leftPackets.Last("alert", alert.Id));
        Check(rightAlerts.Get(alert.Id) == 1 && rightPackets.Count("alert", alert.Id) == alertForwardCount,
            "duplicate relay alert receives reply without being re-emitted");
        int incomingBeforeNoise = rightAlerts.Total;
        right.ReceiveRelayPacket("not-json"); right.ReceiveRelayPacket(new string('中', 2000));
        right.ReceiveRelayPacket(leftPackets.Last("alert", alert.Id).Replace("手机协作", "非法篡改"));
        Check(rightAlerts.Total == incomingBeforeNoise && right.GetPeers().Length == 1, "invalid and oversized relay envelopes are rejected");
        Check(leftPackets.AllWithinLimit && rightPackets.AllWithinLimit, "all relay envelopes fit the 4096-byte UTF-8 datagram limit");
        lock (errors) Check(errors.Count == 0, "relay-only transport has no unexpected errors");
        failRelayHandler = true;
        SentAlert afterFailure = left.SendAlert(true);
        failRelayHandler = false;
        Check(rightAlerts.Get(afterFailure.Id) == 1 && left.GetLastSent().ReceivedIds.Length == 1,
            "failing relay subscriber does not prevent later subscribers or normal delivery");
        lock (errors) Check(errors.Any(e => e.Contains("simulated relay outage")), "relay callback failure is reported clearly");
        relayActive = false; left.Dispose(); right.Dispose();
    }

    private static void RunLegacyBridge()
    {
        int[] ports = ReservePorts(2);
        LanNode bridge = CreateNode("new-bridge", ports[0], new[] { ports[1] }, RoomKey);
        LanNode legacy = CreateNode("legacy-lan", ports[1], new[] { ports[0] }, RoomKey);
        AlertCounter legacyAlerts = new AlertCounter();
        AlertCounter bridgeAlerts = new AlertCounter();
        EnvelopeLog bridgePackets = new EnvelopeLog();
        legacy.AlertReceived += legacyAlerts.Record;
        bridge.AlertReceived += bridgeAlerts.Record;
        bridge.RelayPacketReady += bridgePackets.Record;
        bridge.Start(); legacy.Start();
        Wait(() => bridge.GetPeers().Length == 1 && legacy.GetPeers().Length == 1, 4000, "bridge discovers legacy LAN node");
        long now = CurrentUtcMilliseconds();
        string id = Guid.NewGuid().ToString("N");
        LanNode.WirePacket phoneAlert = new LanNode.WirePacket {
            Version = 1, Type = "alert", SenderId = "phone-mobile", Name = "手机同事·小林",
            Time = now, AlertTime = now, Id = id, IsTest = false
        };
        string raw = Encoding.UTF8.GetString(Encode(phoneAlert));
        bridge.ReceiveRelayPacket(raw);
        Wait(() => legacyAlerts.Get(id) == 1 && bridgeAlerts.Get(id) == 1, 4000,
            "single new bridge forwards signed phone alert to legacy LAN recipient");
        Wait(() => bridgePackets.Has("ack", id, "legacy-lan"), 4000, "legacy LAN acknowledgement is returned to relay unchanged");
        Check(legacyAlerts.SenderName(id) == "手机同事·小林", "mobile-origin Chinese name survives cloud-to-LAN bridge");
        legacy.MarkSeen(id);
        Wait(() => bridgePackets.Has("seen", id, "legacy-lan"), 4000, "legacy human confirmation is returned to relay");
        bridge.ReceiveRelayPacket(raw);
        Check(legacyAlerts.Get(id) == 1 && bridgeAlerts.Get(id) == 1 && bridgePackets.Count("alert", id) == 0,
            "cloud envelope deduplication avoids repeated notification and relay echo");

        using (UdpClient echo = new UdpClient(AddressFamily.InterNetwork))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(raw);
            echo.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, ports[0]));
        }
        Thread.Sleep(150);
        Check(bridgePackets.Count("alert", id) == 0 && bridgeAlerts.Get(id) == 1,
            "LAN copy of cloud fingerprint is processed without returning the original to relay");
        Check(bridge.GetPeers().Single(p => p.Id == "phone-mobile").Address == "云端", "cloud echo cannot replace phone cloud route with bridge address");

        SentAlert legacyOrigin = legacy.SendAlert(true);
        Wait(() => bridgePackets.Has("alert", legacyOrigin.Id, "legacy-lan"), 4000, "legacy-origin alert is forwarded to cloud");
        LanNode.WirePacket phoneSeen = new LanNode.WirePacket {
            Version = 1, Type = "seen", SenderId = "phone-mobile", Name = "手机同事·小林",
            Time = CurrentUtcMilliseconds(), AlertTime = (long)(legacyOrigin.SentUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
            Id = legacyOrigin.Id, TargetId = "legacy-lan", IsTest = true
        };
        bridge.ReceiveRelayPacket(Encoding.UTF8.GetString(Encode(phoneSeen)));
        Wait(() => legacy.GetLastSent().SeenIds.Contains("phone-mobile"), 4000,
            "cloud human acknowledgement reaches original legacy LAN sender");
        bridge.Dispose(); legacy.Dispose();
    }

    private static LanNode CreateNode(string name, int port, int[] targets, byte[] key)
    {
        LanNode node = new LanNode(new NetworkOptions
        {
            NodeId = name,
            DisplayName = name,
            RoomKey = key,
            Port = port,
            ListenAddress = IPAddress.Loopback,
            BroadcastTargets = targets.Select(p => new IPEndPoint(IPAddress.Loopback, p)).ToArray()
        });
        Nodes.Add(node);
        return node;
    }

    private static int[] ReservePorts(int count)
    {
        List<UdpClient> reservations = new List<UdpClient>();
        try
        {
            for (int i = 0; i < count; i++) reservations.Add(new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)));
            return reservations.Select(c => ((IPEndPoint)c.Client.LocalEndPoint).Port).ToArray();
        }
        finally { foreach (UdpClient reservation in reservations) reservation.Close(); }
    }

    private static void CheckCollectionShapes(SentAlert snapshot, int expectedCount)
    {
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        string json = serializer.Serialize(snapshot);
        Dictionary<string, object> decoded = serializer.Deserialize<Dictionary<string, object>>(json);
        Check(decoded["ExpectedPeers"] is System.Collections.ArrayList &&
            ((System.Collections.ArrayList)decoded["ExpectedPeers"]).Count == expectedCount &&
            decoded["ReceivedIds"] is System.Collections.ArrayList && decoded["SeenIds"] is System.Collections.ArrayList,
            "JSON collection shape for " + expectedCount + " recipients");
    }

    private static void AgeUnreadReminder(LanNode receiver, string id)
    {
        // Advance only this record's age so the regression exercises cleanup without a five-minute wait.
        // The final assertion still requires a real UDP Seen packet arriving at the original sender.
        Type nodeType = typeof(LanNode);
        var cache = (System.Collections.IDictionary)nodeType.GetField("incoming", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(receiver);
        object record = cache[id];
        Type recordType = record.GetType();
        recordType.GetField("CreatedMs").SetValue(record, -600000L);
        var alert = (IncomingAlert)recordType.GetField("Alert").GetValue(record);
        alert.SentUtc = DateTime.UtcNow.AddMinutes(-6);
        MethodInfo tick = nodeType.GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic);
        for (int i = 0; i < 3; i++)
        {
            tick.Invoke(receiver, new object[] { null });
            Thread.Sleep(50);
        }
    }

    private static void InjectMalformedPackets(int port)
    {
        using (UdpClient source = new UdpClient(AddressFamily.InterNetwork))
        {
            IPEndPoint endpoint = new IPEndPoint(IPAddress.Loopback, port);
            foreach (string noise in new[] { "not-json", "null", "{}", "[]", "{\"Payload\":\"x\",\"Signature\":\"invalid\"}" })
            {
                byte[] bytes = Encoding.UTF8.GetBytes(noise);
                source.Send(bytes, bytes.Length, endpoint);
            }
            byte[] oversize = new byte[5000];
            source.Send(oversize, oversize.Length, endpoint);
            long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            foreach (LanNode.WirePacket packet in new[]
            {
                Packet(now - 300000, "alert", Guid.NewGuid().ToString("N")),
                Packet(now + 300000, "alert", Guid.NewGuid().ToString("N")),
                Packet(now, "unknown-command", Guid.NewGuid().ToString("N")),
                Packet(now, "alert", "invalid-id"),
                Packet(now, "alert", Guid.NewGuid().ToString("N"), "invalid\nname")
            })
            {
                byte[] bytes = Encode(packet);
                source.Send(bytes, bytes.Length, endpoint);
            }
            LanNode.WirePacket invalidSignature = Packet(now, "alert", Guid.NewGuid().ToString("N"));
            byte[] tampered = Encode(invalidSignature);
            string changed = Encoding.UTF8.GetString(tampered).Replace("noise-node", "other-node");
            byte[] changedBytes = Encoding.UTF8.GetBytes(changed);
            source.Send(changedBytes, changedBytes.Length, endpoint);
        }
    }

    private static LanNode.WirePacket Packet(long time, string type, string id, string name = "noise-node")
    {
        return new LanNode.WirePacket { Version = 1, Type = type, SenderId = "noise-node", Name = name, Time = time, AlertTime = time, Id = id };
    }

    private static long CurrentUtcMilliseconds()
    {
        return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
    }

    private static byte[] Encode(LanNode.WirePacket packet)
    {
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        string payload = serializer.Serialize(packet);
        string signature;
        using (HMACSHA256 hmac = new HMACSHA256(RoomKey)) signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
        return Encoding.UTF8.GetBytes(serializer.Serialize(new LanNode.WireEnvelope { Payload = payload, Signature = signature }));
    }

    private static void Wait(Func<bool> predicate, int timeoutMs, string name)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate()) { Check(true, name); return; }
            Thread.Sleep(50);
        }
        throw new Exception(name + " timed out after " + timeoutMs + "ms");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        passed++;
        Console.WriteLine("pass " + name);
    }

    private sealed class AlertCounter
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, int> counts = new Dictionary<string, int>();
        private readonly Dictionary<string, bool> tests = new Dictionary<string, bool>();
        private readonly Dictionary<string, string> names = new Dictionary<string, string>();
        public void Record(IncomingAlert alert)
        {
            lock (gate)
            {
                if (!counts.ContainsKey(alert.Id)) counts[alert.Id] = 0;
                counts[alert.Id]++;
                tests[alert.Id] = alert.IsTest;
                names[alert.Id] = alert.SenderName;
            }
        }
        public int Get(string id) { lock (gate) return counts.ContainsKey(id) ? counts[id] : 0; }
        public bool WasTest(string id) { lock (gate) return tests.ContainsKey(id) && tests[id]; }
        public string SenderName(string id) { lock (gate) return names.ContainsKey(id) ? names[id] : null; }
        public int Total { get { lock (gate) return counts.Values.Sum(); } }
    }

    private sealed class EnvelopeLog
    {
        private readonly object gate = new object();
        private readonly List<KeyValuePair<LanNode.WirePacket, string>> entries = new List<KeyValuePair<LanNode.WirePacket, string>>();
        private bool allWithinLimit = true;

        public void Record(string envelope)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            LanNode.WireEnvelope wire = serializer.Deserialize<LanNode.WireEnvelope>(envelope);
            LanNode.WirePacket packet = serializer.Deserialize<LanNode.WirePacket>(wire.Payload);
            lock (gate)
            {
                allWithinLimit &= Encoding.UTF8.GetByteCount(envelope) <= 4096;
                entries.Add(new KeyValuePair<LanNode.WirePacket, string>(packet, envelope));
            }
        }

        public int Count(string type, string id)
        {
            lock (gate) return entries.Count(e => e.Key.Type == type && e.Key.Id == id);
        }

        public string Last(string type, string id)
        {
            lock (gate) return entries.Last(e => e.Key.Type == type && e.Key.Id == id).Value;
        }

        public bool Has(string type, string id, string senderId)
        {
            lock (gate) return entries.Any(e => e.Key.Type == type && e.Key.Id == id && e.Key.SenderId == senderId);
        }

        public bool AllWithinLimit { get { lock (gate) return allWithinLimit; } }
    }
}
