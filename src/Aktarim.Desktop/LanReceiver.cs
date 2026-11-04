using Aktarim.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using System.Net;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Aktarim.Desktop;

internal sealed class LanReceiver : IAsyncDisposable
{
    private const int ChunkSize = 16 * 1024 * 1024;
    private readonly object sync = new();
    private readonly ChunkTransfer store;
    private readonly HashSet<Guid> allowed = [];
    private readonly Dictionary<Guid, string> completed = [];
    private readonly SemaphoreSlim commitGate = new(1, 1);
    private readonly SemaphoreSlim requests = new(8, 8), sessionRequests = new(4, 4);
    private WebApplication? app;
    private string? pending, session;
    private DateTime expires = DateTime.UtcNow.AddMinutes(5);
    private readonly Dictionary<IPAddress, (int Count, DateTime Until)> attempts = [];
    private bool approved, denied, codeUsed;
    private DateTime sessionExpires;
    private readonly Action pendingNotice;
    private string Code { get; } = Token();
    public string Url { get; private set; } = "";
    public string PairUrl => Url + "/#" + Code;
    public LanReceiver(string destination, Action pendingNotice)
    {
        store = new(destination); this.pendingNotice = pendingNotice;
    }
    public bool HasPending { get { lock (sync) return pending != null && !approved && !denied; } }
    public void Decide(bool accept) { lock (sync) { if (pending != null) { approved = accept; denied = !accept; } } }
    private static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static bool Same(string? a, string? b) => a != null && b != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    public async Task StartAsync(IPAddress address, X509Certificate2? certificate)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = typeof(LanReceiver).Assembly.FullName, ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        if (Environment.GetEnvironmentVariable("AKTARIM_LAN_DIAGNOSTICS") == "1") builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Debug);
        builder.WebHost.ConfigureKestrel(k => { k.Limits.MaxRequestBodySize = ChunkSize + 1024; k.Listen(address, 0, x => { if (certificate != null) x.UseHttps(certificate); }); });
        app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; worker-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
            if (ctx.Connection.RemoteIpAddress is not { } remote || !PrivateAddress(remote)) { ctx.Response.StatusCode = 403; return; }
            if (!string.Equals(ctx.Request.Host.Value, new Uri(Url).Authority, StringComparison.OrdinalIgnoreCase)) { ctx.Response.StatusCode = 400; return; }
            if (ctx.Request.Path.StartsWithSegments("/v1") && !string.Equals(ctx.Request.Headers.Origin.ToString(), Url, StringComparison.OrdinalIgnoreCase)) { ctx.Response.StatusCode = 403; return; }
            if (!ctx.Request.Path.StartsWithSegments("/v1")) { await next(); return; }
            if (!requests.Wait(0)) { ctx.Response.StatusCode = 429; return; }
            bool sessionSlot = false;
            try
            {
                if (Authorized(ctx))
                {
                    if (!sessionRequests.Wait(0)) { ctx.Response.StatusCode = 429; return; }
                    sessionSlot = true;
                }
                await next();
            }
            finally { if (sessionSlot) sessionRequests.Release(); requests.Release(); }
        });
        static IResult WebFile(string name, string type) => Results.File(typeof(LanReceiver).Assembly.GetManifestResourceStream("Aktarim.Web." + name) ?? throw new InvalidOperationException("Missing web resource: " + name), type);
        app.MapGet("/", () => WebFile("index.html", "text/html; charset=utf-8"));
        app.MapGet("/app.js", () => WebFile("app.js", "text/javascript; charset=utf-8"));
        app.MapGet("/worker.js", () => WebFile("worker.js", "text/javascript; charset=utf-8"));
        app.MapGet("/sha256.min.js", () => WebFile("sha256.min.js", "text/javascript; charset=utf-8"));
        app.MapGet("/style.css", () => WebFile("style.css", "text/css; charset=utf-8"));
        app.MapPost("/v1/pair", (HttpContext ctx, PairRequest request) =>
        {
            lock (sync)
            {
                var now = DateTime.UtcNow;
                if (now > expires || codeUsed) return Results.Unauthorized();
                var ip = ctx.Connection.RemoteIpAddress!.MapToIPv6();
                foreach (var key in attempts.Where(x => x.Value.Until <= now).Select(x => x.Key).ToArray()) attempts.Remove(key);
                if (attempts.TryGetValue(ip, out var limit) && limit.Count >= 5) return Results.StatusCode(429);
                if (!Same(request.Code, Code))
                {
                    if (!attempts.ContainsKey(ip) && attempts.Count >= 1024) return Results.StatusCode(429);
                    attempts[ip] = (limit.Count + 1, limit.Until > now ? limit.Until : now.AddMinutes(1));
                    return Results.Unauthorized();
                }
                codeUsed = true; pending = Token(); pendingNotice(); return Results.Ok(new { ticket = pending });
            }
        });
        app.MapPost("/v1/pair/status", (HttpContext ctx, PairStatus request) =>
        {
            lock (sync)
            {
                if (DateTime.UtcNow > expires || !Same(pending, request.Ticket) || denied) return Results.Unauthorized();
                if (!approved) return Results.Ok(new { approved = false });
                session ??= Token(); sessionExpires = DateTime.UtcNow.AddHours(2);
                ctx.Response.Cookies.Append("aktarim", session, new CookieOptions { HttpOnly = true, Secure = certificate != null, SameSite = SameSiteMode.Strict, IsEssential = true, MaxAge = TimeSpan.FromHours(2), Path = "/" });
                pending = null;
                return Results.Ok(new { approved = true });
            }
        });
        bool Authorized(HttpContext ctx) { lock (sync) return DateTime.UtcNow < sessionExpires && Same(session, ctx.Request.Cookies["aktarim"]); }
        app.MapPost("/v1/session", (HttpContext ctx) => Authorized(ctx) ? Results.Ok() : Results.Unauthorized());
        app.MapPost("/v1/transfers", async (HttpContext ctx, TransferRequest request) =>
        {
            if (!Authorized(ctx)) return Results.Unauthorized();
            if (request.Length < 0 || request.Length > 100L * 1024 * 1024 * 1024 || request.Sha256 is not { Length: 64 } || !request.Sha256.All(Uri.IsHexDigit) || string.IsNullOrEmpty(request.Name)) return Results.BadRequest();
            try
            {
                var identity = SHA256.HashData(Encoding.UTF8.GetBytes(request.Name + "\n" + request.Length + "\n" + request.Sha256.ToUpperInvariant()));
                var id = new Guid(identity.AsSpan(0, 16));
                lock (sync) if (completed.TryGetValue(id, out var finished)) return Results.Ok(new { id, chunkSize = ChunkSize, chunks = Array.Empty<ChunkRecord>(), completed = true, name = Path.GetFileName(finished) });
                await store.CreateAsync(new Manifest(id, request.Name, request.Length, request.Sha256.ToUpperInvariant(), ChunkSize));
                lock (sync) allowed.Add(id);
                var chunks = await store.StatusAsync(id);
                return Results.Ok(new { id, chunkSize = ChunkSize, chunks, completed = false, name = request.Name });
            }
            catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException) { return Results.BadRequest(); }
        });
        bool Allowed(HttpContext ctx, Guid id) { lock (sync) return Authorized(ctx) && allowed.Contains(id); }
        app.MapPut("/v1/transfers/{id:guid}/chunks/{index:int}", async (HttpContext ctx, Guid id, int index) =>
        {
            if (!Allowed(ctx, id)) return Results.Unauthorized();
            if (ctx.Request.ContentLength is not > 0 or > ChunkSize) return Results.BadRequest();
            try
            {
                await store.PutAsync(id, index, ctx.Request.Body, ctx.Request.ContentLength.Value, ctx.Request.Headers["X-Chunk-Sha256"].ToString(), ctx.RequestAborted);
                return Results.Ok();
            }
            catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException) { return Results.BadRequest(); }
        });
        app.MapPost("/v1/transfers/{id:guid}/commit", async (HttpContext ctx, Guid id) =>
        {
            if (!Allowed(ctx, id)) return Results.Unauthorized();
            await commitGate.WaitAsync();
            try
            {
                if (completed.TryGetValue(id, out var existing)) return Results.Ok(new { name = Path.GetFileName(existing) });
                var path = await store.CommitAsync(id); lock (sync) completed[id] = path;
                return Results.Ok(new { name = Path.GetFileName(path) });
            }
            catch (Exception e) when (e is IOException or InvalidOperationException) { return Results.BadRequest(); }
            finally { commitGate.Release(); }
        });
        await app.StartAsync();
        Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/');
    }
    public async ValueTask DisposeAsync() { if (app != null) await app.DisposeAsync(); }
    public static bool PrivateAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes(); return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }
    private sealed record PairRequest(string Code);
    private sealed record PairStatus(string Ticket);
    private sealed record TransferRequest(string Name, long Length, string Sha256);
}
