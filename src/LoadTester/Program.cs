using System.Collections.Concurrent;

// 壓測工具：持續打 LB，每秒印一次成功數、失敗數、各版本回應數。
// 用法：dotnet run --project src/LoadTester -- [url] [秒數，0 = 直到 Ctrl+C] [併發數]
var url = args.ElementAtOrDefault(0) ?? "http://127.0.0.1:8080/";
var seconds = int.Parse(args.ElementAtOrDefault(1) ?? "0");
var workers = int.Parse(args.ElementAtOrDefault(2) ?? "20");

using var cts = seconds > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(seconds)) : new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
long ok = 0, failed = 0;
var versions = new ConcurrentDictionary<string, long>();
var errors = new ConcurrentDictionary<string, long>();

async Task Worker()
{
    while (!cts.IsCancellationRequested)
    {
        try
        {
            using var resp = await http.GetAsync(url, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref ok);
                versions.AddOrUpdate(body.Split(' ')[0], 1, (_, n) => n + 1);
            }
            else
            {
                Interlocked.Increment(ref failed);
                errors.AddOrUpdate($"HTTP {(int)resp.StatusCode}", 1, (_, n) => n + 1);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
        catch (Exception ex)
        {
            Interlocked.Increment(ref failed);
            errors.AddOrUpdate(ex.GetType().Name, 1, (_, n) => n + 1);
        }
    }
}

var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(Worker)).ToArray();

string Versions() => string.Join(", ", versions.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));

var started = DateTime.Now;
long lastOk = 0;
while (!cts.IsCancellationRequested)
{
    try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { break; }
    var nowOk = Interlocked.Read(ref ok);
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] +{nowOk - lastOk,4} req/s  失敗累計={Interlocked.Read(ref failed)}  版本: {Versions()}");
    lastOk = nowOk;
}
await Task.WhenAll(tasks);

Console.WriteLine();
Console.WriteLine($"== 結束：{(DateTime.Now - started).TotalSeconds:F0} 秒，成功 {ok}，失敗 {failed} ==");
Console.WriteLine($"   版本分佈: {Versions()}");
foreach (var (kind, n) in errors) Console.WriteLine($"   錯誤 {kind}: {n}");
return failed == 0 ? 0 : 1;
