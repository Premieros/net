using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using RestaurantWiFiGateway;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "Restaurant WiFi Gateway");
builder.Services.AddHostedService<GatewayWorker>();
await builder.Build().RunAsync();

sealed class GatewayWorker : BackgroundService
{
    readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Restaurant WiFi Control");
    readonly string dataFile;
    readonly SemaphoreSlim dataLock = new(1, 1);
    readonly WfpForwardGate gate = new();
    readonly object topologyLock = new();
    NetworkTopologySnapshot topology = new();
    HttpListener? listener;

    public GatewayWorker()
    {
        dataFile = Path.Combine(dataDir, "v9-data.json");
        Directory.CreateDirectory(dataDir);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        listener = new HttpListener();
        listener.Prefixes.Add("http://+:8088/");
        listener.Prefixes.Add("http://127.0.0.1:8765/");
        listener.Start();

        var networkLoop = MaintainNetworkGate(stoppingToken);
        try
        {
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
        finally
        {
            try { listener.Close(); } catch { }
            try { await networkLoop; } catch { }
        }
    }

    async Task MaintainNetworkGate(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var detected = NetworkDiscovery.Discover();
                var authorized = await LoadAuthorizedAddresses();
                gate.Apply(detected, authorized);
                lock (topologyLock) topology = detected;
            }
            catch (Exception ex)
            {
                gate.ReportFailure(ex);
            }

            try { await Task.Delay(2000, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    async Task<List<IPAddress>> LoadAuthorizedAddresses()
    {
        var result = new List<IPAddress>();
        await dataLock.WaitAsync();
        try
        {
            if (!File.Exists(dataFile)) return result;
            var root = JsonNode.Parse(await File.ReadAllTextAsync(dataFile))?.AsObject();
            var clients = root?["Clients"]?.AsArray();
            if (root is null || clients is null) return result;

            var changed = false;
            var now = DateTimeOffset.UtcNow;
            foreach (var n in clients)
            {
                var o = n?.AsObject();
                if (o is null || !(o["Connected"]?.GetValue<bool>() ?? false)) continue;

                if (DateTimeOffset.TryParse(o["SessionExpiresAt"]?.GetValue<string>(), out var expires) && expires <= now)
                {
                    o["Connected"] = false;
                    changed = true;
                    continue;
                }

                if (IPAddress.TryParse(o["Ip"]?.GetValue<string>(), out var ip) &&
                    ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    result.Add(ip);
            }

            if (changed)
                await File.WriteAllTextAsync(dataFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }
        catch { }
        finally { dataLock.Release(); }
        return result;
    }

    async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.LocalEndPoint?.Port == 8765)
            {
                if (ctx.Request.Url?.AbsolutePath.Equals("/status/", StringComparison.OrdinalIgnoreCase) == true)
                {
                    await Write(ctx, BuildStatusJson(), "application/json; charset=utf-8");
                    return;
                }
                await Write(ctx, "portal-ready", "text/plain; charset=utf-8");
                return;
            }

            if (ctx.Request.HttpMethod == "POST") { await Activate(ctx); return; }
            await Write(ctx, PortalHtml(""), "text/html; charset=utf-8");
        }
        catch
        {
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    string BuildStatusJson()
    {
        NetworkTopologySnapshot snapshot;
        lock (topologyLock) snapshot = topology;

        return JsonSerializer.Serialize(new
        {
            portal = "ready",
            gate = new { active = gate.Active, state = gate.State, error = gate.LastError },
            wan = snapshot.Wan,
            accessPoints = snapshot.AccessPoints,
            detectedAt = snapshot.DetectedAt
        });
    }

    async Task Activate(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var values = ParseForm(await reader.ReadToEndAsync());
        var name = values.GetValueOrDefault("name", "").Trim();
        var phone = values.GetValueOrDefault("phone", "").Trim();
        var code = values.GetValueOrDefault("code", "").Trim().ToUpperInvariant();
        var ipText = ctx.Request.RemoteEndPoint?.Address.ToString() ?? "";

        if (name.Length < 2 || phone.Length < 5 || code.Length < 4 || !IPAddress.TryParse(ipText, out var clientIp))
        {
            await Write(ctx, PortalHtml("راجع الاسم والهاتف والكود."), "text/html; charset=utf-8"); return;
        }

        NetworkTopologySnapshot snapshot;
        lock (topologyLock) snapshot = topology;
        if (!snapshot.AccessPoints.Any(x => x.Contains(clientIp)))
        {
            await Write(ctx, PortalHtml("يجب فتح بوابة الدخول من شبكة الـ Access Point المحمية."), "text/html; charset=utf-8"); return;
        }

        await dataLock.WaitAsync();
        string groupName;
        int minutes;
        try
        {
            JsonObject root;
            try { root = JsonNode.Parse(File.Exists(dataFile) ? await File.ReadAllTextAsync(dataFile) : "{}")?.AsObject() ?? new JsonObject(); }
            catch { await Write(ctx, PortalHtml("تعذر قراءة إعدادات النظام."), "text/html; charset=utf-8"); return; }

            var codes = root["Codes"]?.AsArray() ?? new JsonArray();
            JsonObject? matched = null;
            foreach (var n in codes)
            {
                var o = n?.AsObject();
                if (o is null) continue;
                if (string.Equals(o["Code"]?.GetValue<string>(), code, StringComparison.OrdinalIgnoreCase) &&
                    (o["Enabled"]?.GetValue<bool>() ?? false) &&
                    (o["Uses"]?.GetValue<int>() ?? 0) < (o["MaxUses"]?.GetValue<int>() ?? 1))
                { matched = o; break; }
            }
            if (matched is null) { await Write(ctx, PortalHtml("الكود غير صحيح أو انتهى استخدامه."), "text/html; charset=utf-8"); return; }

            var clients = root["Clients"]?.AsArray() ?? new JsonArray();
            root["Clients"] = clients;
            foreach (var n in clients)
            {
                var o = n?.AsObject();
                if (o is null) continue;
                if ((o["Connected"]?.GetValue<bool>() ?? false) &&
                    string.Equals(o["Phone"]?.GetValue<string>(), phone, StringComparison.OrdinalIgnoreCase))
                { await Write(ctx, PortalHtml("هذا الهاتف لديه جلسة نشطة بالفعل."), "text/html; charset=utf-8"); return; }
            }

            var groupId = matched["GroupId"]?.GetValue<string>() ?? "";
            var groups = root["Groups"]?.AsArray() ?? new JsonArray();
            JsonObject? group = null;
            foreach (var n in groups)
            {
                var o = n?.AsObject();
                if (o is not null && string.Equals(o["Id"]?.GetValue<string>(), groupId, StringComparison.OrdinalIgnoreCase))
                { group = o; break; }
            }
            groupName = group?["Name"]?.GetValue<string>() ?? "";
            minutes = group?["Minutes"]?.GetValue<int>() ?? 60;

            clients.Add(new JsonObject
            {
                ["Name"] = name, ["Phone"] = phone, ["Device"] = ctx.Request.UserAgent ?? "",
                ["Ip"] = ipText, ["Mac"] = "", ["Group"] = groupName, ["UsedMb"] = 0, ["Connected"] = true,
                ["SessionStartedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["SessionExpiresAt"] = DateTimeOffset.UtcNow.AddMinutes(minutes).ToString("O"),
                ["AccessCode"] = code
            });
            matched["Uses"] = (matched["Uses"]?.GetValue<int>() ?? 0) + 1;
            await File.WriteAllTextAsync(dataFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }
        finally { dataLock.Release(); }

        try
        {
            gate.Apply(snapshot, await LoadAuthorizedAddresses());
        }
        catch (Exception ex)
        {
            gate.ReportFailure(ex);
            await Write(ctx, PortalHtml("تم قبول الكود لكن تعذر فتح الإنترنت. راجع حالة Gateway في برنامج الإدارة."), "text/html; charset=utf-8");
            return;
        }

        await Write(ctx, SuccessHtml(groupName, minutes), "text/html; charset=utf-8");
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
<body><div class='box'><h1>Restaurant Wi-Fi</h1><p>الإنترنت محجوب حتى يتم التحقق من الكود.</p>{(string.IsNullOrWhiteSpace(error) ? "" : $"<div class='err'>{WebUtility.HtmlEncode(error)}</div>")}
<form method='post'><input name='name' placeholder='الاسم' required><input name='phone' placeholder='رقم الهاتف' inputmode='tel' required><input name='code' placeholder='كود الدخول' required><button type='submit'>تفعيل الإنترنت</button></form></div></body></html>";

    static string SuccessHtml(string group, int minutes) => $@"<!doctype html><html dir='rtl' lang='ar'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><style>body{{font-family:Segoe UI,Tahoma;background:#0f172a;color:white;text-align:center;padding:12vh 20px}}.ok{{font-size:56px}}h1{{font-size:30px}}p{{color:#cbd5e1}}</style></head><body><div class='ok'>✓</div><h1>تم فتح الإنترنت</h1><p>الباقة: {WebUtility.HtmlEncode(group)} — المدة: {minutes} دقيقة</p><p>يمكنك إغلاق هذه الصفحة.</p></body></html>";

    static async Task Write(HttpListenerContext ctx, string text, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.ContentType = contentType; ctx.Response.ContentEncoding = Encoding.UTF8; ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
    }

    public override void Dispose()
    {
        try { listener?.Close(); } catch { }
        gate.Dispose();
        dataLock.Dispose();
        base.Dispose();
    }
}
