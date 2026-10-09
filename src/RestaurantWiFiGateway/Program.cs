using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using RestaurantWiFiStorage;
using RestaurantWiFiGateway;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "Restaurant WiFi Gateway");
builder.Services.AddHostedService<GatewayWorker>();
builder.Services.AddHostedService<AdminPipeWorker>();
await builder.Build().RunAsync();

sealed class GatewayWorker : BackgroundService
{
    readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Restaurant WiFi Control");
    readonly StateStore store = new();
    readonly Dictionary<string, DateTimeOffset> lastAttempt = new(StringComparer.Ordinal);
    HttpListener? listener;
    readonly SemaphoreSlim activationGate = new(1, 1);
    const int MaxFormBytes = 4096;

    public GatewayWorker()
    {
        Directory.CreateDirectory(dataDir);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = ExpireSessionsLoop(stoppingToken);
        listener = new HttpListener();
        listener.Prefixes.Add("http://+:8088/");
        listener.Prefixes.Add("http://127.0.0.1:8765/");
        listener.Start();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ctx = await listener.GetContextAsync().WaitAsync(stoppingToken);
                _ = Task.Run(() => Handle(ctx), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(500, stoppingToken); }
        }
    }

    async Task ExpireSessionsLoop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
                store.Update(root => SessionLifecycle.Expire(root, DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Console.Error.WriteLine("Session maintenance stopped: " + ex); }
    }

    async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.LocalEndPoint?.Port == 8765)
            {
                await Write(ctx, "portal-ready", "text/plain; charset=utf-8");
                return;
            }
            if (ctx.Request.HttpMethod == "POST")
            {
                if (ctx.Request.Url?.AbsolutePath != "/activate") { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
                await activationGate.WaitAsync();
                try { await Activate(ctx); }
                finally { activationGate.Release(); }
                return;
            }
            if (ctx.Request.HttpMethod != "GET") { ctx.Response.StatusCode = 405; ctx.Response.Close(); return; }
            await Write(ctx, PortalHtml(""), "text/html; charset=utf-8");
        }
        catch
        {
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    sealed record ActivationResult(bool Success, string Message, int Minutes = 0);

    async Task Activate(HttpListenerContext ctx)
    {
        if (ctx.Request.ContentLength64 > MaxFormBytes ||
            !string.Equals(ctx.Request.ContentType?.Split(';')[0].Trim(), "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = 400; ctx.Response.Close(); return;
        }
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await ctx.Request.InputStream.ReadAsync(chunk)) != 0)
        {
            if (buffer.Length + read > MaxFormBytes) { ctx.Response.StatusCode = 413; ctx.Response.Close(); return; }
            buffer.Write(chunk, 0, read);
        }
        var values = ParseForm(Encoding.UTF8.GetString(buffer.ToArray()));
        var name = values.GetValueOrDefault("name", "").Trim();
        var phone = values.GetValueOrDefault("phone", "").Trim();
        var code = values.GetValueOrDefault("code", "").Trim().ToUpperInvariant();
        var ip = ctx.Request.RemoteEndPoint?.Address.ToString() ?? "";
        if (name.Length < 2 || name.Length > 100 || phone.Length < 5 || phone.Length > 30 || code.Length < 4 || code.Length > 32)
        {
            await Write(ctx, PortalHtml("راجع الاسم والهاتف والكود."), "text/html; charset=utf-8"); return;
        }
        var now = DateTimeOffset.UtcNow;
        foreach (var old in lastAttempt.Where(x => x.Value < now.AddMinutes(-2)).Select(x => x.Key).ToArray())
            lastAttempt.Remove(old);
        if (lastAttempt.TryGetValue(ip, out var previous) && now - previous < TimeSpan.FromSeconds(2))
        {
            ctx.Response.StatusCode = 429; ctx.Response.Close(); return;
        }
        lastAttempt[ip] = now;

        var result = store.Update(root =>
        {
            var codes = root["Codes"]?.AsArray() ?? new JsonArray();
            JsonObject? matched = null;
            foreach (var item in codes)
            {
                if (item is not JsonObject entry) continue;
                if (string.Equals(entry["Code"]?.GetValue<string>(), code, StringComparison.OrdinalIgnoreCase) &&
                    (entry["Enabled"]?.GetValue<bool>() ?? false) &&
                    (entry["Uses"]?.GetValue<int>() ?? 0) < (entry["MaxUses"]?.GetValue<int>() ?? 1))
                { matched = entry; break; }
            }
            if (matched is null) return new ActivationResult(false, "الكود غير صحيح أو انتهى استخدامه.");
            var groupId = matched["GroupId"]?.GetValue<string>() ?? "";
            var groups = root["Groups"]?.AsArray() ?? new JsonArray();
            JsonObject? group = null;
            foreach (var item in groups)
                if (item is JsonObject entry &&
                    string.Equals(entry["Id"]?.GetValue<string>(), groupId, StringComparison.OrdinalIgnoreCase))
                { group = entry; break; }
            if (group is null || !(group["Enabled"]?.GetValue<bool>() ?? false))
                return new ActivationResult(false, "الباقة غير متاحة.");
            var minutes = group["Minutes"]?.GetValue<int>() ?? 0;
            if (minutes <= 0) return new ActivationResult(false, "مدة الباقة غير صالحة.");
            SessionLifecycle.Expire(root, now);
            var clients = root["Clients"]?.AsArray() ?? new JsonArray();
            foreach (var item in clients)
            {
                if (item is not JsonObject client) continue;
                if ((client["Connected"]?.GetValue<bool>() ?? false) &&
                    DateTimeOffset.TryParse(client["SessionExpiresAt"]?.GetValue<string>(), out var expiry) &&
                    expiry > now &&
                    string.Equals(client["Phone"]?.GetValue<string>(), phone, StringComparison.OrdinalIgnoreCase))
                    return new ActivationResult(false, "هذا الهاتف لديه جلسة نشطة بالفعل.");
            }
            root["Clients"] = clients;
            var groupName = group["Name"]?.GetValue<string>() ?? "";
            clients.Add(new JsonObject
            {
                ["Name"] = name, ["Phone"] = phone, ["Device"] = ctx.Request.UserAgent ?? "",
                ["Ip"] = ip, ["Mac"] = "", ["Group"] = groupName, ["UsedMb"] = 0, ["Connected"] = true,
                ["SessionStartedAt"] = now.ToString("O"),
                ["SessionExpiresAt"] = now.AddMinutes(minutes).ToString("O"),
                ["AccessCode"] = code, ["SessionStatus"] = "pending-network-authorization"
            });
            matched["Uses"] = (matched["Uses"]?.GetValue<int>() ?? 0) + 1;
            return new ActivationResult(true, groupName, minutes);
        });
        await Write(ctx, result.Success ? SuccessHtml(result.Message, result.Minutes) : PortalHtml(result.Message),
            "text/html; charset=utf-8");
    }

    static Dictionary<string,string> ParseForm(string body)
    {
        var d = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Split('=', 2);
            d[WebUtility.UrlDecode(p[0])] = p.Length > 1 ? WebUtility.UrlDecode(p[1].Replace("+"," ")) : "";
        }
        return d;
    }

    static string PortalHtml(string error) => $@"<!doctype html><html dir='rtl' lang='ar'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Restaurant Wi-Fi</title>
<style>body{{font-family:Segoe UI,Tahoma;background:#0f172a;margin:0;color:#111827}}.box{{max-width:420px;margin:8vh auto;background:white;border-radius:18px;padding:28px;box-shadow:0 20px 60px #0005}}h1{{margin:0 0 6px}}p{{color:#64748b}}input{{box-sizing:border-box;width:100%;padding:13px;margin:7px 0;border:1px solid #cbd5e1;border-radius:10px;font-size:16px}}button{{width:100%;padding:14px;margin-top:12px;border:0;border-radius:10px;background:#2563eb;color:white;font-size:17px;font-weight:700}}.err{{color:#b91c1c;background:#fee2e2;padding:10px;border-radius:8px}}</style></head>
<body><div class='box'><h1>Restaurant Wi-Fi</h1><p>أدخل بياناتك وكود الدخول لتفعيل الإنترنت.</p>{(string.IsNullOrWhiteSpace(error) ? "" : $"<div class='err'>{WebUtility.HtmlEncode(error)}</div>")}
<form method='post' action='/activate'><input name='name' placeholder='الاسم' required><input name='phone' placeholder='رقم الهاتف' inputmode='tel' required><input name='code' placeholder='كود الدخول' required><button type='submit'>تفعيل الإنترنت</button></form></div></body></html>";

    static string SuccessHtml(string group, int minutes) => $@"<!doctype html><html dir='rtl' lang='ar'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><style>body{{font-family:Segoe UI,Tahoma;background:#0f172a;color:white;text-align:center;padding:12vh 20px}}.ok{{font-size:56px}}h1{{font-size:30px}}p{{color:#cbd5e1}}</style></head><body><div class='ok'>✓</div><h1>تم تسجيل الكود</h1><p>الباقة: {WebUtility.HtmlEncode(group)} — المدة: {minutes} دقيقة</p><p>تم تسجيل الجلسة؛ السماح الفعلي بالإنترنت يتطلب تهيئة التحكم بالشبكة.</p></body></html>";

    static async Task Write(HttpListenerContext ctx, string text, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.ContentType = contentType; ctx.Response.ContentEncoding = Encoding.UTF8; ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
    }

    public override void Dispose()
    {
        try { listener?.Close(); } catch { }
        base.Dispose();
    }
}
