using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataFlow.Infrastructure.Persistence;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

// Stage 12 acceptance benchmark (plan.md "Провести 10 замеров на 1 000 строках").
//
// Starts isolated PostgreSQL/RabbitMQ/MinIO containers, the real API and Worker
// as separate processes, then imports the synthetic fixture several times while
// sampling Worker RSS. Requirements checked against DataFlow-Architecture.md
// §18: 1 000 rows within 30 seconds of capture and Worker RSS under 512 MB on
// a fixture up to 25 MB.
//
//   dotnet run --project tools/DataFlow.Benchmark -- [--runs 10] [--skip-big] [--rows 1000]
//
// Results are written to benchmarks/results-<timestamp>.json and printed as a
// markdown table. Child process logs land in benchmarks/logs/.
namespace DataFlow.Benchmark;

public static class Program
{
    private const string SourceSchema = """
        {"keyFields":["externalId"],"csvDelimiter":",","fields":[
        {"name":"externalId","type":"string","required":true,"maxLength":80},
        {"name":"name","type":"string","required":true,"maxLength":40},
        {"name":"email","type":"email","required":true,"maxLength":254},
        {"name":"registeredAt","type":"date","required":false,"format":"yyyy-MM-dd"},
        {"name":"isActive","type":"boolean","required":true}]}
        """;

    public static async Task<int> Main(string[] arguments)
    {
        var options = Options.Parse(arguments);
        var root = FindRepositoryRoot();
        var apiDll = options.ApiDll ?? Path.Combine(root, "src", "DataFlow.Api", "bin", "Debug", "net10.0", "DataFlow.Api.dll");
        var workerDll = options.WorkerDll ?? Path.Combine(root, "src", "DataFlow.Worker", "bin", "Debug", "net10.0", "DataFlow.Worker.dll");
        if (!File.Exists(apiDll) || !File.Exists(workerDll))
        {
            Console.Error.WriteLine("Built API/Worker DLLs not found. Run: dotnet build DataFlow.sln");
            return 2;
        }

        var logs = Path.Combine(root, "benchmarks", "logs");
        Directory.CreateDirectory(logs);
        var benchmarks = Path.Combine(root, "benchmarks");
        Directory.CreateDirectory(benchmarks);

        Console.WriteLine("Starting Testcontainers (PostgreSQL, RabbitMQ, MinIO)...");
        await using var postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
        await using var rabbit = new RabbitMqBuilder("rabbitmq:4.0.5-alpine")
            .WithUsername("dataflow").WithPassword("benchmark_password").Build();
        await using var minio = new ContainerBuilder("icr.io/fusion-open/minio/minio@sha256:14cea493d9a34af32f524e538b8346cf79f3321eff8e708c1e2960462bd8936e")
            .WithEnvironment("MINIO_ROOT_USER", "dataflow_bench")
            .WithEnvironment("MINIO_ROOT_PASSWORD", "benchmark_password")
            .WithCommand("server", "/data")
            .WithPortBinding(9000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9000).ForPath("/minio/health/ready")))
            .Build();
        await postgres.StartAsync();
        await rabbit.StartAsync();
        await minio.StartAsync();
        var policy = await rabbit.ExecAsync(["rabbitmqctl", "set_policy", "--apply-to", "quorum_queues",
            "dataflow-retry", "^imports\\.retry\\.", "{\"dead-letter-strategy\":\"at-least-once\",\"overflow\":\"reject-publish\"}"]);
        if (policy.ExitCode != 0) { throw new InvalidOperationException("RabbitMQ policy failed: " + policy.Stderr); }

        var bucket = "dataflow-benchmark";
        await CreateBucketAsync(minio, bucket);
        var connectionString = postgres.GetConnectionString();
        await using (var database = new DataFlowDbContext(new DbContextOptionsBuilder<DataFlowDbContext>().UseNpgsql(connectionString).Options))
        {
            await database.Database.MigrateAsync();
        }
        Console.WriteLine("Containers ready, migrations applied.");

        var environment = new Dictionary<string, string?>
        {
            ["ConnectionStrings__DataFlow"] = connectionString,
            ["Storage__Endpoint"] = $"http://{minio.Hostname}:{minio.GetMappedPublicPort(9000)}",
            ["Storage__Bucket"] = bucket,
            ["Storage__AccessKey"] = "dataflow_bench",
            ["Storage__SecretKey"] = "benchmark_password",
            ["Storage__Region"] = "us-east-1",
            ["Storage__UseSsl"] = "false",
            ["RabbitMq__Host"] = rabbit.Hostname,
            ["RabbitMq__Port"] = rabbit.GetMappedPublicPort(5672).ToString(),
            ["RabbitMq__User"] = "dataflow",
            ["RabbitMq__Password"] = "benchmark_password",
            ["RabbitMq__VirtualHost"] = "/",
            ["Outbox__PollIntervalSeconds"] = "1",
            // The 25 MB memory fixture exceeds the 50 000 default; the budget in
            // the architecture allows up to 500 000 data rows.
            ["Import__MaxRows"] = "500000",
        };

        var apiPort = FreePort();
        var api = StartProcess("API", "dotnet", [apiDll], environment, apiPort, Path.Combine(logs, "api.log"));
        var workerPort = FreePort();
        var workerEnvironment = new Dictionary<string, string?>(environment) { ["urls"] = $"http://127.0.0.1:{workerPort}" };
        var worker = StartProcess("Worker", "dotnet", [workerDll], workerEnvironment, null, Path.Combine(logs, "worker.log"));
        var sampler = new RssSampler(worker);
        var samplerTask = Task.Run(sampler.Run);
        try
        {
            await WaitHealthyAsync($"http://127.0.0.1:{apiPort}", api, "API");
            await WaitHealthyAsync($"http://127.0.0.1:{workerPort}", worker, "Worker");
            using var session = new ApiSession(apiPort);
            var user = await session.RegisterAsync();
            Console.WriteLine($"Authenticated as {user}.");

            var sourceId = await session.CreateSourceAsync();
            var seedBytes = Encoding.UTF8.GetBytes(SeedCsv());
            var seedJobId = await session.UploadAsync(sourceId, seedBytes, "seed");
            await session.WaitTerminalAsync(seedJobId, TimeSpan.FromMinutes(3));
            Console.WriteLine("Seed import committed.");

            var runs = new List<RunResult>();
            for (var index = 1; index <= options.Runs; index++)
            {
                var bytes = Encoding.UTF8.GetBytes(MixedCsv(index, options.Rows));
                var result = await RunOnceAsync(session, sampler, sourceId, bytes, index, options.Rows);
                runs.Add(result);
                Console.WriteLine($"Run {index,2}: upload {result.UploadMs,6} ms | after capture {result.ProcessingMs,6} ms | " +
                    $"total {result.TotalMs,6} ms | {result.RowsPerSecond,7:F0} rows/s | peak RSS {result.PeakRssMb:F0} MB");
            }

            BigRunResult? big = null;
            if (!options.SkipBig)
            {
                Console.WriteLine("Generating ~24 MiB fixture for the memory budget check...");
                var bigBytes = BigCsv();
                big = await RunBigAsync(session, sampler, sourceId, bigBytes);
                Console.WriteLine($"Big run: {big.Rows} rows in {big.ProcessingMs / 1000.0:F1} s, peak RSS {big.PeakRssMb:F0} MB");
            }

            var report = Report.Build(root, options, runs, big, sampler.GlobalPeakRssMb);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);
            var jsonPath = Path.Combine(benchmarks, $"results-{stamp}.json");
            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, Report.JsonOptions));
            Console.WriteLine();
            Console.WriteLine(report.ToMarkdown());
            Console.WriteLine($"JSON report: {jsonPath}");
            return report.WithinBudgets ? 0 : 1;
        }
        finally
        {
            sampler.Stop();
            await StopAsync(worker);
            await StopAsync(api);
            await samplerTask.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static async Task<RunResult> RunOnceAsync(ApiSession session, RssSampler sampler, Guid sourceId,
        byte[] bytes, int run, int rows)
    {
        var started = Stopwatch.StartNew();
        sampler.BeginWindow($"run-{run}");
        var jobId = await session.UploadAsync(sourceId, bytes, $"run-{run}");
        var acceptedAt = started.ElapsedMilliseconds;
        var (processingMs, totalMs, job) = await session.TimedWaitAsync(jobId, started, TimeSpan.FromMinutes(3));
        var window = sampler.EndWindow();
        AssertCounters(job, rows);
        return new RunResult(run, rows, acceptedAt, processingMs, totalMs, window.PeakRssMb);
    }

    private static async Task<BigRunResult> RunBigAsync(ApiSession session, RssSampler sampler, Guid sourceId, byte[] bytes)
    {
        var started = Stopwatch.StartNew();
        sampler.BeginWindow("big");
        var jobId = await session.UploadAsync(sourceId, bytes, "big");
        var (processingMs, _, job) = await session.TimedWaitAsync(jobId, started, TimeSpan.FromMinutes(15));
        var window = sampler.EndWindow();
        var rows = CountRows(bytes);
        if (job.ProcessedRows != rows)
        {
            throw new InvalidOperationException($"Big run processed {job.ProcessedRows} of {rows} rows (status {job.Status}).");
        }

        return new BigRunResult(rows, bytes.Length / (1024.0 * 1024.0), processingMs, window.PeakRssMb);
    }

    private static void AssertCounters(JobSnapshot job, int rows)
    {
        if (job.Status is not ("Completed" or "CompletedWithErrors"))
        {
            throw new InvalidOperationException($"Unexpected terminal status {job.Status}.");
        }

        if (job.ProcessedRows != rows
            || job.InsertedRows != rows * 600 / 1000
            || job.UpdatedRows != rows * 100 / 1000
            || job.DuplicateRows != rows * 100 / 1000
            || job.InvalidRows != rows * 200 / 1000)
        {
            throw new InvalidOperationException($"Counters disagree with the fixture: {JsonSerializer.Serialize(job)}.");
        }
    }

    // Mirrors the shape of tests/DataFlow.IntegrationTests/Fixtures/MixedCustomerCsv.cs:
    // per 10 data rows, 2 invalid, 1 duplicate, 1 updated, 6 fresh inserts.
    private static string SeedCsv() => Row("externalId,name,email,registeredAt,isActive") +
        string.Join('\n', Enumerable.Range(0, 1000).Select(index =>
            Row($"seed-{index:D4},Seed Customer {index:D4},seed-{index:D4}@example.test,2026-01-15,true")));

    private static string MixedCsv(int run, int rows)
    {
        var lines = new List<string>(rows + 1) { "externalId,name,email,registeredAt,isActive" };
        for (var index = 0; index < rows; index++)
        {
            lines.Add((index % 10) switch
            {
                0 => $"run-{run}-{index:D4},,run-{run}-{index:D4}@example.test,2026-01-15,true",
                1 => $"run-{run}-{index:D4},Bench Customer {index:D4},run-{run}-{index:D4}@example.test,2026-01-15,maybe",
                2 => $"seed-{index:D4},Seed Customer {index:D4},seed-{index:D4}@example.test,2026-01-15,true",
                3 => $"seed-{index:D4},Seed Customer {index:D4},upd-{run}-{index:D4}@example.test,2026-01-15,true",
                _ => $"run-{run}-{index:D4},Bench Customer {index:D4},run-{run}-{index:D4}@example.test,2026-01-15,true",
            });
        }

        return string.Join('\n', lines) + "\n";
    }

    // Grows rows until the fixture is just below 24 MiB (within the default
    // 25 MiB file limit), capped at the Import MaxRows range maximum.
    private static byte[] BigCsv()
    {
        var builder = new StringBuilder("externalId,name,email,registeredAt,isActive\n");
        var limit = 24 * 1024 * 1024;
        for (var index = 0; builder.Length < limit && index < 500_000; index++)
        {
            builder.Append($"big-{index:D7},Big Fixture Customer Number {index:D7},big-{index:D7}@example.test,2026-01-15,true\n");
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static int CountRows(byte[] bytes)
    {
        var count = 0;
        foreach (var symbol in bytes)
        {
            if (symbol == (byte)'\n') { count++; }
        }

        return count - 1; // header
    }

    private static string Row(string value) => value + "\n";

    private static Process StartProcess(string name, string fileName, string[] arguments,
        Dictionary<string, string?> environment, int? port, string logPath)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        foreach (var (key, value) in environment)
        {
            start.EnvironmentVariables[key] = value;
        }

        start.EnvironmentVariables.Remove("ASPNETCORE_URLS");
        if (port is { } actual)
        {
            start.EnvironmentVariables["ASPNETCORE_URLS"] = $"http://127.0.0.1:{actual}";
            start.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Production";
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException($"{name} process failed to start.");
        var log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        process.OutputDataReceived += (_, received) => { try { if (received.Data is not null) { log.WriteLine(received.Data); } } catch (ObjectDisposedException) { } };
        process.ErrorDataReceived += (_, received) => { try { if (received.Data is not null) { log.WriteLine(received.Data); } } catch (ObjectDisposedException) { } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task StopAsync(Process? process)
    {
        if (process is null || process.HasExited) { return; }
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }

    private static async Task WaitHealthyAsync(string baseAddress, Process process, string name)
    {
        using var client = new HttpClient();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) { throw new InvalidOperationException($"{name} exited during startup."); }
            try
            {
                using var response = await client.GetAsync($"{baseAddress}/health/live");
                if (response.StatusCode == HttpStatusCode.OK) { return; }
            }
            catch (HttpRequestException) { /* not listening yet */ }
            await Task.Delay(300);
        }

        throw new TimeoutException($"{name} did not become healthy.");
    }

    private static async Task CreateBucketAsync(IContainer minio, string bucket)
    {
        var endpoint = $"http://{minio.Hostname}:{minio.GetMappedPublicPort(9000)}";
        using var s3 = new Amazon.S3.AmazonS3Client("dataflow_bench", "benchmark_password", new Amazon.S3.AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        });
        await s3.PutBucketAsync(new Amazon.S3.Model.PutBucketRequest { BucketName = bucket });
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DataFlow.sln")))
        {
            directory = directory.Parent!;
        }

        return directory?.FullName ?? throw new InvalidOperationException("DataFlow.sln not found above the tool directory.");
    }

    private sealed record RunResult(
        int Run,
        int Rows,
        long UploadMs,
        long ProcessingMs,
        long TotalMs,
        double PeakRssMb)
    {
        [JsonIgnore]
        public double RowsPerSecond => ProcessingMs > 0 ? Rows * 1000.0 / ProcessingMs : 0;
    }

    private sealed record BigRunResult(int Rows, double SizeMb, long ProcessingMs, double PeakRssMb);

    private sealed record JobSnapshot(
        string Status,
        long ProcessedRows,
        long InsertedRows,
        long UpdatedRows,
        long DuplicateRows,
        long InvalidRows);

    private sealed record Report
    {
        public required EnvironmentInfo Environment { get; init; }
        public required string Configuration { get; init; }
        public required List<RunResult> Runs { get; init; }
        public required BigRunResult? BigRun { get; init; }
        public required SummaryData Summary { get; init; }
        public required bool WithinBudgets { get; init; }

        [JsonIgnore]
        public double PeakRssMb => Math.Max(Summary.PeakRssMb, BigRun?.PeakRssMb ?? 0);

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public static Report Build(string root, Options options, List<RunResult> runs, BigRunResult? big, double globalPeak)
        {
            var sortedCapture = runs.Select(x => x.ProcessingMs).Order().ToList();
            var sortedTotal = runs.Select(x => x.TotalMs).Order().ToList();
            var info = new EnvironmentInfo(
                System.Environment.MachineName,
                System.Environment.OSVersion.VersionString,
                System.Environment.ProcessPath is null ? "unknown" : Path.GetFileName(System.Environment.ProcessPath),
                System.Environment.ProcessorCount,
                Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024.0 / 1024.0, 1),
                DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture),
                options.Runs,
                options.Rows);
            var processingP95 = Percentile(sortedCapture, 0.95);
            var rssLimit = Math.Max(globalPeak, big?.PeakRssMb ?? 0);
            var summary = new SummaryData(
                Percentile(sortedCapture, 0.50),
                processingP95,
                Percentile(sortedTotal, 0.50),
                Percentile(sortedTotal, 0.95),
                runs.Average(x => x.RowsPerSecond),
                globalPeak);
            return new Report
            {
                Environment = info,
                Configuration = "real API and Worker processes (Production), Testcontainers PostgreSQL 18 / RabbitMQ 4.0.5 / MinIO; " +
                    $"Import:MaxRows=500000, Outbox:PollIntervalSeconds=1, poll interval 50 ms; timing 'after capture' starts at the first observed non-Queued status",
                Runs = runs,
                BigRun = big,
                Summary = summary,
                WithinBudgets = processingP95 <= 30_000 && rssLimit <= 512,
            };
        }

        private static double Percentile(List<long> sorted, double percentile) =>
            sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(percentile * sorted.Count) - 1)];

        public string ToMarkdown()
        {
            var builder = new StringBuilder();
            builder.AppendLine($"| Run | Rows | Upload ms | After capture ms | Total ms | rows/s | Peak RSS MB |");
            builder.AppendLine($"|---|---|---:|---:|---:|---:|---:|");
            foreach (var run in Runs)
            {
                builder.AppendLine($"| {run.Run} | {run.Rows} | {run.UploadMs} | {run.ProcessingMs} | {run.TotalMs} | {run.RowsPerSecond:F0} | {run.PeakRssMb:F0} |");
            }

            builder.AppendLine();
            builder.AppendLine($"- p50/p95 after capture: {Summary.CaptureP50Ms:F0} / {Summary.CaptureP95Ms:F0} ms");
            builder.AppendLine($"- p50/p95 total (upload -> terminal): {Summary.TotalP50Ms:F0} / {Summary.TotalP95Ms:F0} ms");
            builder.AppendLine($"- average throughput: {Summary.AverageRowsPerSecond:F0} rows/s");
            builder.AppendLine($"- Worker peak RSS: {PeakRssMb:F0} MB (budget 512 MB)");
            if (BigRun is { } big)
            {
                builder.AppendLine($"- Big fixture {big.SizeMb:F1} MiB / {big.Rows} rows: {big.ProcessingMs / 1000.0:F1} s, peak RSS {big.PeakRssMb:F0} MB");
            }

            builder.AppendLine();
            builder.AppendLine($"- Budget check (p95 after capture <= 30 s, RSS <= 512 MB): **{(WithinBudgets ? "passed" : "exceeded")}**");
            builder.AppendLine($"- Environment: {Environment.Os} ({Environment.Cores} cores, ~{Environment.MemoryGb} GiB), {Environment.Timestamp}");
            return builder.ToString();
        }

        public sealed record EnvironmentInfo(
            string Machine,
            string Os,
            string Runtime,
            int Cores,
            double MemoryGb,
            string Timestamp,
            int RunCount,
            int RowsPerRun);

        public sealed record SummaryData(
            double CaptureP50Ms,
            double CaptureP95Ms,
            double TotalP50Ms,
            double TotalP95Ms,
            double AverageRowsPerSecond,
            double PeakRssMb);
    }

    private sealed record Options
    {
        public int Runs { get; private init; } = 10;
        public int Rows { get; private init; } = 1000;
        public bool SkipBig { get; private init; }
        public string? ApiDll { get; private init; }
        public string? WorkerDll { get; private init; }

        public static Options Parse(string[] arguments)
        {
            var options = new Options();
            for (var index = 0; index < arguments.Length; index++)
            {
                options = arguments[index] switch
                {
                    "--runs" => options with { Runs = int.Parse(arguments[++index], CultureInfo.InvariantCulture) },
                    "--rows" => ParseRows(arguments[++index]),
                    "--skip-big" => options with { SkipBig = true },
                    "--api-dll" => options with { ApiDll = arguments[++index] },
                    "--worker-dll" => options with { WorkerDll = arguments[++index] },
                    _ => throw new ArgumentException($"Unknown argument {arguments[index]}."),
                };
            }

            return options;
        }

        private static Options ParseRows(string value)
        {
            var rows = int.Parse(value, CultureInfo.InvariantCulture);
            if (rows < 10 || rows % 10 != 0) { throw new ArgumentException("--rows must be a positive multiple of 10."); }
            return new Options { Rows = rows };
        }
    }

    /// <summary>Samples the worker process memory; per-run windows attribute peak usage.</summary>
    private sealed class RssSampler(Process? process)
    {
        private readonly Lock gate = new();
        private string? _window;
        private double _windowPeak;
        private double _globalPeak;
        private readonly Dictionary<string, double> _windows = new();
        private readonly CancellationTokenSource _cancellation = new();

        public double GlobalPeakRssMb => Volatile.Read(ref _globalPeak);

        public void BeginWindow(string name)
        {
            lock (gate)
            {
                _window = name;
                _windowPeak = 0;
            }

            SampleOnce();
        }

        public RssWindow EndWindow()
        {
            SampleOnce();
            lock (gate)
            {
                var peak = _windowPeak;
                if (_window is not null) { _windows[_window] = peak; }
                _window = null;
                return new RssWindow(peak);
            }
        }

        public readonly record struct RssWindow(double PeakRssMb);

        public void Run()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                SampleOnce();
                _cancellation.Token.WaitHandle.WaitOne(200);
            }
        }

        private void SampleOnce()
        {
            try
            {
                if (process is null || process.HasExited) { return; }
                process.Refresh();
                var megabytes = process.WorkingSet64 / 1024.0 / 1024.0;
                lock (gate)
                {
                    if (megabytes > _globalPeak) { _globalPeak = megabytes; }
                    if (_window is not null && megabytes > _windowPeak) { _windowPeak = megabytes; }
                }
            }
            catch (InvalidOperationException) { /* process exited between checks */ }
        }

        public void Stop() => _cancellation.Cancel();
    }

    /// <summary>Cookie/CSRF HTTP client for the real API with real Identity authentication.</summary>
    private sealed class ApiSession : IDisposable
    {
        private readonly HttpClient _client;
        private readonly CookieContainer _cookies = new();
        private string _csrf = "";

        public ApiSession(int port)
        {
            var handler = new HttpClientHandler { CookieContainer = _cookies, AllowAutoRedirect = false };
            _client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        }

        public void Dispose() => _client.Dispose();

        public async Task<string> RegisterAsync()
        {
            // Any mutating call needs the antiforgery pair: fetch the token first
            // (the GET also sets the matching cookie), authenticate, then refresh
            // the token after login in case the session rotated it.
            await FetchCsrfAsync();
            var email = $"benchmark-{DateTime.UtcNow:yyyyMMdd-HHmmss}@example.test";
            using var register = await PostAuthenticatedAsync("/api/v1/auth/register",
                new { email, password = "Benchmark-2026!run" });
            if (!register.IsSuccessStatusCode) { throw new InvalidOperationException("Register failed: " + await register.Content.ReadAsStringAsync()); }
            using var login = await PostAuthenticatedAsync("/api/v1/auth/login",
                new { email, password = "Benchmark-2026!run" });
            if (!login.IsSuccessStatusCode) { throw new InvalidOperationException("Login failed: " + await login.Content.ReadAsStringAsync()); }
            await FetchCsrfAsync();
            return email;
        }

        private async Task FetchCsrfAsync()
        {
            var token = await _client.GetFromJsonAsync<JsonElement>("/api/v1/auth/antiforgery");
            _csrf = token.GetProperty("token").GetString() ?? throw new InvalidOperationException("No CSRF token.");
        }

        private async Task<HttpResponseMessage> PostAuthenticatedAsync(string path, object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-CSRF-TOKEN", _csrf);
            return await _client.SendAsync(request);
        }

        public async Task<Guid> CreateSourceAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/data-sources")
            {
                Content = new StringContent(
                    $$"""{"name":"Benchmark source","acceptedFormats":["Csv"],"schema":{{SourceSchema}}}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-CSRF-TOKEN", _csrf);
            using var response = await _client.SendAsync(request);
            if (!response.IsSuccessStatusCode) { throw new InvalidOperationException("Create source failed: " + await response.Content.ReadAsStringAsync()); }
            var source = await response.Content.ReadFromJsonAsync<JsonElement>();
            return source.GetProperty("id").GetGuid();
        }

        public async Task<Guid> UploadAsync(Guid sourceId, byte[] bytes, string idempotencyKey)
        {
            using var content = new MultipartFormDataContent
            {
                { new ByteArrayContent(bytes), "file", $"{idempotencyKey}.csv" },
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/data-sources/{sourceId}/files") { Content = content };
            request.Headers.Add("Idempotency-Key", idempotencyKey);
            request.Headers.Add("X-CSRF-TOKEN", _csrf);
            var response = await _client.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.Accepted)
            {
                throw new InvalidOperationException($"Upload failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            }

            var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
            return accepted.GetProperty("jobId").GetGuid();
        }

        public async Task<JobSnapshot> WaitTerminalAsync(Guid jobId, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var job = await GetJobAsync(jobId);
                if (job.Status is "Completed" or "CompletedWithErrors" or "Failed" or "Cancelled") { return job; }
                await Task.Delay(50);
            }

            throw new TimeoutException($"Job {jobId} did not reach a terminal state within {timeout}.");
        }

        public async Task<(long ProcessingMs, long TotalMs, JobSnapshot Job)> TimedWaitAsync(
            Guid jobId, Stopwatch started, TimeSpan timeout)
        {
            long processingMs = 0;
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var job = await GetJobAsync(jobId);
                if (job.Status != "Queued" && processingMs == 0)
                {
                    processingMs = started.ElapsedMilliseconds;
                }

                if (job.Status is "Completed" or "CompletedWithErrors" or "Failed" or "Cancelled")
                {
                    return (processingMs == 0 ? 0 : started.ElapsedMilliseconds - processingMs, started.ElapsedMilliseconds, job);
                }

                await Task.Delay(50);
            }

            throw new TimeoutException($"Job {jobId} did not reach a terminal state within {timeout}.");
        }

        private async Task<JobSnapshot> GetJobAsync(Guid jobId) =>
            (await _client.GetFromJsonAsync<JobSnapshot>($"/api/v1/jobs/{jobId}"))!;
    }
}
