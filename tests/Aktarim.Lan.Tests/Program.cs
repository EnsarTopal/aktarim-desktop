using Aktarim.Desktop;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "aktarim-lan-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await using var receiver = new LanReceiver(root, () => { });
    await receiver.StartAsync(IPAddress.Loopback, null);
    HttpClient Client(string ip)
    {
        var handler = new SocketsHttpHandler { UseCookies = false, UseProxy = false, ConnectCallback = async (context, token) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Parse(ip), 0));
            await socket.ConnectAsync(context.DnsEndPoint, token);
            return new NetworkStream(socket, ownsSocket: true);
        }};
        var client = new HttpClient(handler) { BaseAddress = new Uri(receiver.Url) };
        client.DefaultRequestHeaders.Add("Origin", receiver.Url);
        return client;
    }
    using var attacker = Client("127.0.0.2");
    using var owner = Client("127.0.0.1");
    foreach (var path in new[] { "/", "/app.js", "/worker.js", "/sha256.min.js", "/style.css" })
        Check((await owner.GetAsync(path)).IsSuccessStatusCode, "embedded web asset " + path);
    for (int i = 0; i < 5; i++) Check((await attacker.PostAsJsonAsync("/v1/pair", new { code = "wrong" })).StatusCode == HttpStatusCode.Unauthorized, "bad code rejected");
    Check((int)(await attacker.PostAsJsonAsync("/v1/pair", new { code = "wrong" })).StatusCode == 429, "client rate limited");
    var code = receiver.PairUrl.Split('#')[1];
    var pairing = await owner.PostAsJsonAsync("/v1/pair", new { code });
    Check(pairing.IsSuccessStatusCode && receiver.HasPending, "other client can request approval");
    var ticket = (await pairing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString();
    var waiting = await owner.PostAsJsonAsync("/v1/pair/status", new { ticket });
    Check(!(await waiting.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("approved").GetBoolean(), "approval required");
    receiver.Decide(true);
    var approved = await owner.PostAsJsonAsync("/v1/pair/status", new { ticket });
    Check((await approved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("approved").GetBoolean(), "approval grants session");
    Check((await owner.PostAsJsonAsync("/v1/pair", new { code })).StatusCode == HttpStatusCode.Unauthorized, "QR single use");
    string cookie = approved.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    owner.DefaultRequestHeaders.Add("Cookie", cookie);
    var created = await owner.PostAsJsonAsync("/v1/transfers", new { name = "test.bin", length = 16777216, sha256 = new string('A', 64) });
    var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    var held = new List<TcpClient>();
    try
    {
        for (int i = 0; i < 4; i++)
        {
            var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, new Uri(receiver.Url).Port); held.Add(tcp);
            var header = $"PUT /v1/transfers/{id}/chunks/0 HTTP/1.1\r\nHost: {new Uri(receiver.Url).Authority}\r\nOrigin: {receiver.Url}\r\nCookie: {cookie}\r\nContent-Length: 16777216\r\nX-Chunk-Sha256: {new string('A', 64)}\r\n\r\n";
            await tcp.GetStream().WriteAsync(System.Text.Encoding.ASCII.GetBytes(header));
        }
        bool limited = false;
        for (int i = 0; i < 50 && !limited; i++)
        {
            await Task.Delay(100);
            limited = (int)(await owner.PostAsync("/v1/session", null)).StatusCode == 429;
        }
        Check(limited, "session concurrency rejected before another body");
    }
    finally { foreach (var tcp in held) tcp.Dispose(); }
}
finally { Directory.Delete(root, true); }

static void Check(bool value, string name)
{
    if (!value) throw new Exception("FAILED: " + name);
    Console.WriteLine("PASS " + name);
}
