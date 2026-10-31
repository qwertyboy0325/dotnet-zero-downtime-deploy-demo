using System.Collections.Concurrent;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Model;

// 用 YARP 做的負載平衡器，多了幾個管理 API 讓部署腳本把實例摘除 / 加回。
// 以程式所在目錄當 content root，才讀得到旁邊的 appsettings.json
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Logging.SetMinimumLevel(LogLevel.Warning);

const string ClusterId = "app";
var backends = builder.Configuration.GetSection("Backends").Get<Dictionary<string, string>>()!;
var enabled = new ConcurrentDictionary<string, string>(backends);       // 正在接流量的實例
var draining = new ConcurrentDictionary<string, DestinationState>();    // 已摘除、還在收尾的實例

RouteConfig[] routes =
[
    new() { RouteId = "all", ClusterId = ClusterId, Order = 1000, Match = new() { Path = "{**catch-all}" } }
];

ClusterConfig BuildCluster() => new()
{
    ClusterId = ClusterId,
    LoadBalancingPolicy = "RoundRobin",
    Destinations = enabled.ToDictionary(kv => kv.Key, kv => new DestinationConfig { Address = kv.Value }),
    HealthCheck = new()
    {
        Active = new()
        {
            Enabled = true,
            Interval = TimeSpan.FromSeconds(2),
            Timeout = TimeSpan.FromSeconds(1),
            Policy = "ConsecutiveFailures",
            Path = "/health"
        }
    }
};

builder.Services.AddReverseProxy().LoadFromMemory(routes, [BuildCluster()]);
var app = builder.Build();

var config = app.Services.GetRequiredService<InMemoryConfigProvider>();
var lookup = app.Services.GetRequiredService<IProxyStateLookup>();
var gate = new object();

DestinationState? FindDestination(string id) =>
    lookup.TryGetCluster(ClusterId, out var cluster) && cluster.Destinations.TryGetValue(id, out var d) ? d : null;

// 摘除：之後的新請求不會再分到它；已經在處理中的請求照常完成
app.MapPost("/lb/drain/{id}", (string id) =>
{
    lock (gate)
    {
        if (!enabled.ContainsKey(id)) return Results.NotFound();
        if (FindDestination(id) is { } state) draining[id] = state;
        enabled.TryRemove(id, out _);
        config.Update(routes, [BuildCluster()]);
    }
    return Results.Ok();
});

app.MapPost("/lb/enable/{id}", (string id) =>
{
    lock (gate)
    {
        if (!backends.TryGetValue(id, out var address)) return Results.NotFound();
        draining.TryRemove(id, out _);
        enabled[id] = address;
        config.Update(routes, [BuildCluster()]);
    }
    return Results.Ok();
});

// 部署腳本輪詢這個值，等到 0 才去關掉實例（純文字，方便 shell 判斷）
app.MapGet("/lb/inflight/{id}", (string id) =>
    (draining.TryGetValue(id, out var d) ? d : FindDestination(id))?.ConcurrentRequestCount ?? 0);

app.MapGet("/lb/status", () => backends.Keys.Select(id => new
{
    id,
    address = backends[id],
    state = enabled.ContainsKey(id) ? "enabled" : "drained",
    health = FindDestination(id)?.Health.Active.ToString(),
    inflight = (draining.TryGetValue(id, out var d) ? d : FindDestination(id))?.ConcurrentRequestCount ?? 0
}));

app.MapReverseProxy();
app.Run();
