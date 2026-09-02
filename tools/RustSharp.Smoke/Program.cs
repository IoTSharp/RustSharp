using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RustSharp.Compiler;
using RustSharp.Runtime;

namespace RustSharp.Smoke;

internal static class Program
{
    private const int MaximumArguments = 16;
    private const int MaximumTimeoutSeconds = 300;
    private const int MaximumCases = 8;
    private const int MaximumCleanupAttempts = 40;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"smoke: {exception.Message}");
            return 2;
        }

        string root = FindRepositoryRoot();
        string reportPath = options.ReportPath is null
            ? Path.Combine(root, "artifacts", "smoke", "p0-io.json")
            : Path.GetFullPath(options.ReportPath, root);
        string runDirectory = Path.Combine(
            Path.GetDirectoryName(reportPath)!,
            $".run-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);
        var deadline = new CancellationTokenSource(options.Timeout);
        var cases = new List<ProbeResult>(MaximumCases);
        string? cleanupDiagnostic = null;
        try
        {
            cases.Add(await RunProbeSafelyAsync("file-roundtrip", () => RunFileProbeAsync(runDirectory, deadline.Token), deadline.Token).ConfigureAwait(false));
            cases.Add(await RunProbeSafelyAsync("loopback-tcp", () => RunTcpProbeAsync(deadline.Token), deadline.Token).ConfigureAwait(false));
            cases.Add(await RunProbeSafelyAsync("async-completion-cancellation", () => RunAsyncProbeAsync(deadline.Token), deadline.Token).ConfigureAwait(false));
            cases.Add(await RunProbeSafelyAsync("sqlite-transaction", () => RunSqliteProbeAsync(runDirectory, deadline.Token), deadline.Token).ConfigureAwait(false));
        }
        finally
        {
            deadline.Dispose();
            cleanupDiagnostic = TryDeleteDirectory(runDirectory);
        }

        int passed = cases.Count(static item => item.Status == "passed");
        int skipped = cases.Count(static item => item.Status == "skipped");
        int failed = cases.Count(static item => item.Status == "failed");
        string status = failed != 0 ? "failed" : skipped != 0 ? "blocked" : "passed";
        var report = new SmokeReport(
            1,
            DateTimeOffset.UtcNow,
            "p0-io",
            new Summary(cases.Count, passed, failed, skipped, status),
            cases)
        {
            RunDirectoryCleanupDiagnostic = cleanupDiagnostic,
        };
        await WriteReportAsync(reportPath, report).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(report, SmokeJsonContext.Default.SmokeReport));
        return status switch { "passed" => 0, "blocked" => 2, _ => 1 };
    }

    private static async Task<ProbeResult> RunFileProbeAsync(string runDirectory, CancellationToken token)
    {
        string path = Path.Combine(runDirectory, "roundtrip.txt");
        const string expected = "RustSharp managed-hybrid file probe\n";
        await File.WriteAllTextAsync(path, expected, new UTF8Encoding(false), token).ConfigureAwait(false);
        string actual = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
        return actual == expected
            ? new("file-roundtrip", "passed", null)
            : new("file-roundtrip", "failed", "file content differed after round-trip");
    }

    private static async Task<ProbeResult> RunProbeSafelyAsync(
        string name,
        Func<Task<ProbeResult>> probe,
        CancellationToken token)
    {
        try
        {
            return await probe().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new ProbeResult(name, "blocked", "overall timeout expired");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            string diagnostic = exception.Message.Length <= 512 ? exception.Message : exception.Message[..512] + "...";
            return new ProbeResult(name, "failed", diagnostic);
        }
    }

    private static async Task<ProbeResult> RunTcpProbeAsync(CancellationToken token)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = ServeOnceAsync(listener, token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
        await using NetworkStream stream = client.GetStream();
        byte[] request = Encoding.UTF8.GetBytes("ping");
        await stream.WriteAsync(request, token).ConfigureAwait(false);
        byte[] response = new byte[4];
        int read = await stream.ReadAsync(response, token).ConfigureAwait(false);
        await server.WaitAsync(token).ConfigureAwait(false);
        return read == 4 && Encoding.UTF8.GetString(response) == "pong"
            ? new("loopback-tcp", "passed", null)
            : new("loopback-tcp", "failed", "loopback response differed");
    }

    private static async Task ServeOnceAsync(TcpListener listener, CancellationToken token)
    {
        using TcpClient connection = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
        await using NetworkStream stream = connection.GetStream();
        byte[] request = new byte[4];
        int read = await stream.ReadAsync(request, token).ConfigureAwait(false);
        if (read == 4 && Encoding.UTF8.GetString(request) == "ping")
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("pong"), token).ConfigureAwait(false);
        }
    }

    private static async Task<ProbeResult> RunAsyncProbeAsync(CancellationToken token)
    {
        int completed = await Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), token).ConfigureAwait(false);
            return 7;
        }, token).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        bool cancelled = false;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            cancelled = true;
        }

        return completed == 7 && cancelled
            ? new("async-completion-cancellation", "passed", null)
            : new("async-completion-cancellation", "failed", "async completion or cancellation was not observed");
    }

    private static async Task<ProbeResult> RunSqliteProbeAsync(string runDirectory, CancellationToken token)
    {
        string scriptPath = Path.Combine(runDirectory, "transaction.sql");
        string databasePath = Path.Combine(runDirectory, "probe.db");
        const string script = ".parameter init\n.parameter set :value 41\nBEGIN; CREATE TABLE values_table(value INTEGER); INSERT INTO values_table VALUES(:value); SELECT value + 1 FROM values_table; COMMIT;\n";
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(false), token).ConfigureAwait(false);
        var runner = new BoundedProcessRunner(TimeSpan.FromSeconds(2));
        BoundedProcessResult version;
        try
        {
            version = await runner.RunAsync(new BoundedProcessRequest("sqlite3", ["--version"], runDirectory, TimeSpan.FromSeconds(5)), token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return new("sqlite-transaction", "skipped", $"sqlite3 unavailable: {exception.Message}");
        }

        if (!version.Succeeded)
        {
            return new("sqlite-transaction", "skipped", "sqlite3 unavailable on this runner");
        }

        BoundedProcessResult result = await runner.RunAsync(
            new BoundedProcessRequest("sqlite3", ["-batch", databasePath, $".read {scriptPath}"], runDirectory, TimeSpan.FromSeconds(10)),
            token).ConfigureAwait(false);
        return result.Succeeded && result.StandardOutput.Trim() == "42"
            ? new("sqlite-transaction", "passed", null)
            : new("sqlite-transaction", "failed", $"sqlite3 transaction failed: {result.StandardError.Trim()}");
    }

    private static Options Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length > MaximumArguments) throw new ArgumentException($"at most {MaximumArguments} arguments are accepted");
        string profile = "p0-io";
        string? report = null;
        int timeout = 120;
        for (var index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument is "--profile" or "--report" or "--timeout")
            {
                if (++index >= args.Length) throw new ArgumentException($"option '{argument}' requires a value");
                string value = args[index];
                if (argument == "--profile") profile = value;
                else if (argument == "--report") report = value;
                else if (!int.TryParse(value, out timeout) || timeout is < 1 or > MaximumTimeoutSeconds) throw new ArgumentException("--timeout must be 1..300 seconds");
            }
            else throw new ArgumentException($"unknown option '{argument}'");
        }

        if (profile != "p0-io") throw new ArgumentException("only profile 'p0-io' is supported");
        return new(profile, report, TimeSpan.FromSeconds(timeout));
    }

    private static string FindRepositoryRoot()
    {
        string current = Path.GetFullPath(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8; depth++)
        {
            if (File.Exists(Path.Combine(current, "RustSharp.slnx"))) return current;
            string? parent = Directory.GetParent(current)?.FullName;
            if (parent is null || parent == current) break;
            current = parent;
        }

        throw new DirectoryNotFoundException("Could not locate RustSharp.slnx.");
    }

    private static async Task WriteReportAsync(string path, SmokeReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + $".tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, report, SmokeJsonContext.Default.SmokeReport).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private sealed record Options(string Profile, string? ReportPath, TimeSpan Timeout);
    internal sealed record ProbeResult(string Name, string Status, string? Diagnostic);
    internal sealed record Summary(int Denominator, int Passed, int Failed, int Skipped, string Status);
    internal sealed record SmokeReport(int SchemaVersion, DateTimeOffset GeneratedAtUtc, string Profile, Summary Summary, IReadOnlyList<ProbeResult> Cases)
    {
        public string? RunDirectoryCleanupDiagnostic { get; init; }
    }

    private static string? TryDeleteDirectory(string path)
    {
        var clock = Stopwatch.StartNew();
        Exception? lastException = null;
        for (var attempt = 0; attempt < MaximumCleanupAttempts && clock.Elapsed < CleanupTimeout; attempt++)
        {
            if (!Directory.Exists(path))
            {
                return null;
            }

            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException exception)
            {
                lastException = exception;
            }
            catch (UnauthorizedAccessException exception)
            {
                lastException = exception;
            }
            catch (NotSupportedException exception)
            {
                lastException = exception;
            }
            catch (ArgumentException exception)
            {
                lastException = exception;
            }

            if (!Directory.Exists(path))
            {
                return null;
            }

            Thread.Sleep(50);
        }

        return $"Run directory cleanup failed after {MaximumCleanupAttempts} attempts or {CleanupTimeout.TotalSeconds:0.#} seconds: {lastException?.Message ?? "directory still exists"}";
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Program.SmokeReport))]
internal sealed partial class SmokeJsonContext : JsonSerializerContext
{
}
