using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using OfficeSignal;

public static class RelayClientTests
{
    private static int passed;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
    public static int Main(string[] args)
    {
        Log.FilePath = Path.Combine(args[0], ".build", "relay-client-test.log");
        try { Run(args[0], args[1], args.Length > 2 ? args[2] : null).GetAwaiter().GetResult(); Console.WriteLine("PASS " + passed + " desktop HTTP relay checks"); return 0; }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
    private static async Task Run(string projectRoot, string nodePath, string liveUrl)
    {
        int port;
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start(); port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        string url = liveUrl ?? "http://127.0.0.1:" + port + "/mail-reminder/";
        var start = new ProcessStartInfo(nodePath, "server/server.mjs") { WorkingDirectory = projectRoot, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.EnvironmentVariables["HOST"] = "127.0.0.1";
        start.EnvironmentVariables["PORT"] = port.ToString();
        start.EnvironmentVariables["MAIL_DATA_DIR"] = Path.Combine(projectRoot, ".build", "relay-fixture-" + Guid.NewGuid().ToString("N"));
        start.EnvironmentVariables["PUBLIC_URL"] = url;
        using (var process = liveUrl == null ? Process.Start(start) : null)
        using (var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) })
        {
            try
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("MailReminderAcceptance/1.3.0");
                await Wait(async () => { try { return (await http.GetAsync("api/health")).IsSuccessStatusCode; } catch (HttpRequestException) { return false; } }, "real Node relay starts");
                var key = new byte[32]; using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(key);
                var team = new TeamConfig { RoomName = "中转验收", RoomKey = Convert.ToBase64String(key), BroadcastAddress = "192.168.20.255", Port = 45820, RelayUrl = url };
                using (var a = Node("中文电脑甲", key))
                using (var b = Node("中文电脑乙", key))
                using (var ca = new RelayClient(a, team))
                using (var cb = new RelayClient(b, team))
                {
                    a.Start(); b.Start(); ca.Start(); cb.Start();
                    await Wait(() => Task.FromResult(a.GetPeers().Any(p => p.Id == b.NodeId) && b.GetPeers().Any(p => p.Id == a.NodeId)), "two C# desktop clients discover over real HTTP");
                    a.UpdateDisplayName("中文更名小张");
                    await Wait(() => Task.FromResult(b.GetPeers().Any(p => p.Id == a.NodeId && p.Name == "中文更名小张")), "Chinese rename crosses HTTP relay");
                    var code = await ca.RequestPairCodeAsync(CancellationToken.None);
                    var phone = await Post(http, "api/mobile/pair", new { code = code.Code, name = "中文手机小李" }, null);
                    string phoneToken = (string)phone["token"];
                    string phoneId = (string)phone["nodeId"];
                    long cursor = Convert.ToInt64(phone["cursor"]);
                    Check(code.Subnet == "192.168.20.0/24" && (string)phone["subnet"] == code.Subnet, "pair code binds the desktop subnet");
                    var alert = a.SendAlert(false);
                    await Wait(() => Task.FromResult(a.GetLastSent().ReceivedIds.Contains(b.NodeId)), "desktop alert obtains real HTTP delivery ACK");
                    b.MarkSeen(alert.Id);
                    await Wait(() => Task.FromResult(a.GetLastSent().SeenIds.Contains(b.NodeId)), "manual desktop confirmation crosses HTTP");
                    string phoneAlert = null;
                    a.AlertReceived += incoming => { if (incoming.SenderId == phoneId) phoneAlert = incoming.Id; };
                    var sent = await Post(http, "api/mobile/alert", new { isTest = false }, phoneToken);
                    string id = (string)sent["alertId"];
                    await Wait(() => Task.FromResult(phoneAlert == id), "phone API alert reaches the desktop LanNode entry");
                    a.MarkSeen(id);
                    bool found = false;
                    var until = DateTime.UtcNow.AddSeconds(8);
                    while (!found && DateTime.UtcNow < until)
                    {
                        var response = await Get(http, "api/mobile/poll?cursor=" + cursor, phoneToken);
                        cursor = Convert.ToInt64(response["cursor"]);
                        foreach (string raw in (IEnumerable)response["packets"])
                        {
                            var envelope = Json.Deserialize<Dictionary<string, object>>(raw);
                            var packet = Json.Deserialize<Dictionary<string, object>>((string)envelope["Payload"]);
                            if ((string)packet["Type"] == "seen" && (string)packet["Id"] == id && (string)packet["TargetId"] == phoneId) found = true;
                        }
                    }
                    Check(found, "phone polling receives the desktop human confirmation");
                    await Post(http, "api/mobile/unbind", new { }, phoneToken);
                }
            }
            finally { if (process != null && !process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
        }
    }
    private static LanNode Node(string name, byte[] key)
    {
        int port; using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = ((IPEndPoint)socket.Client.LocalEndPoint).Port;
        return new LanNode(new NetworkOptions { DisplayName = name, RoomKey = key, Port = port, ListenAddress = IPAddress.Loopback, BroadcastTargets = new IPEndPoint[0] });
    }
    private static async Task<Dictionary<string, object>> Post(HttpClient http, string path, object body, string token)
    {
        using (var request = new HttpRequestMessage(HttpMethod.Post, path))
        {
            request.Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json");
            return await Fetch(http, request, token);
        }
    }
    private static async Task<Dictionary<string, object>> Get(HttpClient http, string path, string token)
    {
        using (var request = new HttpRequestMessage(HttpMethod.Get, path)) return await Fetch(http, request, token);
    }
    private static async Task<Dictionary<string, object>> Fetch(HttpClient http, HttpRequestMessage request, string token)
    {
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using (var response = await http.SendAsync(request))
        {
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new Exception("HTTP " + response.StatusCode + " " + body);
            return Json.Deserialize<Dictionary<string, object>>(body);
        }
    }
    private static async Task Wait(Func<Task<bool>> condition, string message)
    {
        var end = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < end) { if (await condition()) { Check(true, message); return; } await Task.Delay(80); }
        throw new Exception("Timed out: " + message);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
}
