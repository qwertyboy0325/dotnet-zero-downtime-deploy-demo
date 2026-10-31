using System.Net;
using System.Reflection;

// 被部署的業務程式：每個實例跑在自己的 port，由 LB 分流。
var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// 收到停止訊號後，Kestrel 不再接新連線，最多等 30 秒讓進行中的請求做完
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

var app = builder.Build();

var version = Assembly.GetEntryAssembly()!
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
var pid = Environment.ProcessId;
var stopping = false;
app.Lifetime.ApplicationStopping.Register(() => stopping = true);

app.MapGet("/", async () =>
{
    await Task.Delay(Random.Shared.Next(50, 300)); // 模擬處理時間，讓「進行中請求」真的存在
    return $"{version} pid={pid}";
});

// LB 的主動健康檢查會打這裡；關機中回 503，LB 就不會再送新請求過來
#if SIMULATE_BROKEN
app.MapGet("/health", () => Results.StatusCode(StatusCodes.Status500InternalServerError)); // e2e 用來驗證自動回滾
#else
app.MapGet("/health", () => stopping
    ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
    : Results.Ok(new { status = "ok", version, pid }));
#endif

// 優雅關機：跨平台（Windows 上沒有 SIGTERM 可送給 console 程式）
app.MapPost("/admin/shutdown", (HttpContext ctx) =>
{
    if (!IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress!))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    app.Lifetime.StopApplication();
    return Results.Accepted();
});

app.Run();
