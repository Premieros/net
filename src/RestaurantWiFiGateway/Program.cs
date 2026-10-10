using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using RestaurantWiFiStorage;
using RestaurantWiFiGateway;
using RestaurantWiFiNetworking;

// Opt-in CI self-test: never starts the service or touches customer data.
// Synthetic adapter indices cannot match any real network interface.
if (args.Length == 1 && args[0] == "--wfp-smoke")
{
    WfpInteropSmoke.Run();
    return;
}

try { GatewayDataSecurity.Protect(); }
catch (Exception ex)
{
    // Keep a diagnostic for failures before Windows hosting/logging starts.
    // Avoid logging customer data. This file is under the service's data directory.
    try
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Restaurant WiFi Control");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "gateway-startup-error.txt"),
            DateTimeOffset.UtcNow.ToString("O") + " " + ex.GetType().Name + ": " + ex.Message);
    }
    catch { }
    throw;
}
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "RestaurantWiFiGateway");
builder.Services.AddSingleton<WfpTrialStatus>();
builder.Services.AddSingleton<TrialAdmissionController>();
// Start the admin channel before optional portal/network diagnostics.
builder.Services.AddHostedService<AdminPipeWorker>();
builder.Services.AddHostedService<ExperimentalWfpTrialWorker>();
builder.Services.AddHostedService<GatewayWorker>();
await builder.Build().RunAsync();

sealed class GatewayWorker : BackgroundService
{
    readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Restaurant WiFi Control");
    readonly StateStore store = new();
    readonly Dictionary<string, DateTimeOffset> lastAttempt = new(StringComparer.Ordinal);
    HttpListener? listener;
    readonly SemaphoreSlim activationGate = new(1, 1);
    readonly SemaphoreSlim requestSlots = new(32, 32);
    readonly INetworkAdmissionController admission;
    readonly TrialAdmissionController trialAdmission;
    readonly WfpTrialStatus trialStatus;
    readonly AccessRedemptionService redemption;
    readonly SessionRevocationService revoker;
    const int MaxFormBytes = 4096;

    public GatewayWorker(WfpTrialStatus trialStatus, TrialAdmissionController trialAdmission)
    {
        this.trialStatus = trialStatus;
        admission = trialAdmission;
        this.trialAdmission = trialAdmission;
        Directory.CreateDirectory(dataDir);
        redemption = new AccessRedemptionService(store, admission);
        revoker = new SessionRevocationService(store, admission);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = ExpireSessionsLoop(stoppingToken);
        // Port 8088 can be occupied by another application. Do not tear down the
        // administrator pipe/SQLite service when the HTTP portal cannot bind.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add("http://+:8088/");
                listener.Prefixes.Add("http://127.0.0.1:8765/");
                listener.Start();
                Console.WriteLine("Gateway HTTP listeners started.");
                while (!stoppingToken.IsCancellationRequested)
                {
                    var ctx = await listener.GetContextAsync().WaitAsync(stoppingToken);
                    if (!await requestSlots.WaitAsync(0, stoppingToken))
                    {
                        ctx.Response.StatusCode = 503;
                        ctx.Response.Close();
                        continue;
                    }
                    _ = Task.Run(async () =>
                    {
                        try { await Handle(ctx); }
                        finally { requestSlots.Release(); }
                    });
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Gateway HTTP portal unavailable (admin pipe remains active): " +
                    ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { listener?.Close(); } catch { }
                listener = null;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    async Task ExpireSessionsLoop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                // A transient SQLite/IPC/WFP error must not permanently end
                // voucher-expiry maintenance. Retry on the next interval.
                try
                {
                    store.Update(root => SessionLifecycle.Expire(root, DateTimeOffset.UtcNow));
                    await revoker.ReconcileAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Session maintenance will retry: " + ex);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.LocalEndPoint?.Port == 8765)
            {
                if (ctx.Request.Url?.AbsolutePath == "/status/")
                {
                    await Write(ctx, JsonSerializer.Serialize(new
                    {
                        portal = "portal-ready",
                        admissionReady = false, // Production admission remains unimplemented.
                        productionSafety = new
                        {
                            readyForPayingGuests = false,
                            permanentFailClosedDeny = false,
                            ipv6Enforced = false,
                            antiIpSpoofing = false,
                            deviceIdentityVerified = false,
                            byteQuotasEnforced = false,
                            speedLimitsEnforced = false,
                            realPhonePermitTrafficVerified = false
                        },
                        ipv4CodeTrialReady = admission.IsEnforcementReady,
                        ipv4TrialPolicy = trialAdmission.Snapshot(),
                        trialSessionAccounting = ExperimentalTrialSessionRecovery.Summary(
                            JsonNode.Parse(store.Read())?.AsObject()),
                        experimentalWfp = trialStatus.Current
                    }), "application/json; charset=utf-8");
                    return;
                }
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
            await Write(ctx, PortalHtml(admission.IsEnforcementReady ? "" :
                "وضع تجريبي فقط: لا تعمل الأكواد إلا بعد حجب IPv4 مؤقتًا وتأكيده يدويًا من المدير."),
                "text/html; charset=utf-8");
        }
        catch
        {
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    async Task Activate(HttpListenerContext ctx)
    {
        // No production-grade admission policy exists. This test can only
        // install temporary IPv4 permits, which must be checked on the device.
        // A valid code must not be consumed outside an active WFP trial.
        if (!admission.IsEnforcementReady)
        {
            ctx.Response.StatusCode = 503;
            await Write(ctx, PortalHtml("تفعيل الأكواد متوقف. شغّل اختبار حجب IPv4 وتأكد من انقطاع الإنترنت على هاتف الاختبار ثم أكد الحجب من برنامج الإدارة. لم يُستخدم الكود."),
                "text/html; charset=utf-8");
            return;
        }
        if (ctx.Request.ContentLength64 > MaxFormBytes ||
            !string.Equals(ctx.Request.ContentType?.Split(';')[0].Trim(),
                "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await ctx.Request.InputStream.ReadAsync(chunk)) != 0)
        {
            if (buffer.Length + read > MaxFormBytes)
            {
                ctx.Response.StatusCode = 413;
                ctx.Response.Close();
                return;
            }
            buffer.Write(chunk, 0, read);
        }

        var values = ParseForm(Encoding.UTF8.GetString(buffer.ToArray()));
        // HttpListener can report IPv4 peers as IPv4-mapped IPv6. Always
        // authorize the canonical source IPv4 seen by the gateway, never an
        // IP supplied in a form field or proxy header.
        ClientIpv4Source.TryNormalize(ctx.Request.RemoteEndPoint?.Address, out var ip);
        var now = DateTimeOffset.UtcNow;
        foreach (var old in lastAttempt.Where(x => x.Value < now.AddMinutes(-2))
            .Select(x => x.Key).ToArray()) lastAttempt.Remove(old);
        if (lastAttempt.TryGetValue(ip, out var previous) &&
            now - previous < TimeSpan.FromSeconds(2))
        {
            ctx.Response.StatusCode = 429;
            ctx.Response.Close();
            return;
        }
        lastAttempt[ip] = now;

        var request = new RedemptionRequest(
            values.GetValueOrDefault("name", "").Trim(),
            values.GetValueOrDefault("phone", "").Trim(),
            values.GetValueOrDefault("code", "").Trim().ToUpperInvariant(),
            ip, (ctx.Request.UserAgent ?? "")[..Math.Min(512, (ctx.Request.UserAgent ?? "").Length)]);
        var result = await redemption.RedeemAsync(request);
        await Write(ctx, result.Success ? SuccessHtml(result.Message, result.Minutes) :
            PortalHtml(result.Message), "text/html; charset=utf-8");
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
<body><div class='box'><h1>Restaurant Wi-Fi</h1><p>اختبار أكواد IPv4 مؤقت: استخدم كود اختبار فقط. خارج فترة الاختبار يمكن لجميع أجهزة Hotspot تصفح الإنترنت.</p>{(string.IsNullOrWhiteSpace(error) ? "" : $"<div class='err'>{WebUtility.HtmlEncode(error)}</div>")}
<form method='post' action='/activate'><input name='name' placeholder='الاسم' required><input name='phone' placeholder='رقم الهاتف' inputmode='tel' required><input name='code' placeholder='كود الدخول' required><button type='submit'>تفعيل الإنترنت</button></form></div></body></html>";

    static string SuccessHtml(string group, int minutes) => $@"<!doctype html><html dir='rtl' lang='ar'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><style>body{{font-family:Segoe UI,Tahoma;background:#0f172a;color:white;text-align:center;padding:12vh 20px}}.ok{{font-size:56px}}h1{{font-size:30px}}p{{color:#cbd5e1}}</style></head><body><div class='ok'>✓</div><h1>تم تسجيل السماح التجريبي</h1><p>الباقة: {WebUtility.HtmlEncode(group)} — المدة: {minutes} دقيقة</p><p>تم تثبيت قاعدة سماح IPv4 مؤقتة، لكن اتصال الهاتف الخارجي لم يُتحقق منه. افتح موقعًا خارجيًا من الهاتف الآن. هذه ليست حماية تجارية أو تحكمًا بالسرعات والحصص.</p></body></html>";

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
