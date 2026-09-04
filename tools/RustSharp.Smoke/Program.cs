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
        bool deadlineExpired = false;
        try
        {
            cases.Add(await RunProbeSafelyAsync("file-roundtrip", () => RunFileProbeAsync(runDirectory, deadline.Token), deadline.Token).ConfigureAwait(false));
            cases.Add(await RunProbeSafelyAsync("loopback-tcp", () => RunTcpProbeAsync(deadline.Token), deadline.Token).ConfigureAwait(false));
            cases.Add(await RunProbeSafelyAsync("async-completion-cancellation", () => RunAsyncProbeAsync(deadline.Token), deadline.Token).ConfigureAwait(false));
            cases.Add(await RunProbeSafelyAsync("sqlite-transaction", () => RunSqliteProbeAsync(runDirectory, deadline.Token), deadline.Token).ConfigureAwait(false));
        }
        finally
        {
            cleanupDiagnostic = TryDeleteDirectory(runDirectory);
            deadlineExpired = deadline.IsCancellationRequested;
            deadline.Dispose();
        }

        int passed = cases.Count(static item => item.Status == "passed");
        int skipped = cases.Count(static item => item.Status == "skipped");
        int failed = cases.Count(static item => item.Status == "failed");
        string status = failed != 0 || cleanupDiagnostic is not null || deadlineExpired
            ? "failed"
            : skipped != 0
                ? "blocked"
                : "passed";
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
            // An execution deadline is a failed probe. The only intentionally
            // blocked case is an optional environment dependency such as the
            // sqlite3 executable being unavailable before it can start.
            return new ProbeResult(name, "failed", "overall timeout expired");
        }
        catch (Exception exception) when (exception is IOException or SocketException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException or TimeoutException)
        {
            string diagnostic = exception.Message.Length <= 512 ? exception.Message : exception.Message[..512] + "...";
            return new ProbeResult(name, "failed", diagnostic);
        }
    }

    private static async Task<ProbeResult> RunTcpProbeAsync(CancellationToken token)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? server = null;
        listener.Start(1);
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            server = ServeOnceAsync(listener, serverCancellation.Token);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
            await using NetworkStream stream = client.GetStream();
            byte[] request = Encoding.UTF8.GetBytes("ping");
            await stream.WriteAsync(request, token).ConfigureAwait(false);
            byte[] response = new byte[4];
            await stream.ReadExactlyAsync(response, token).ConfigureAwait(false);
            await server.WaitAsync(token).ConfigureAwait(false);
            return Encoding.UTF8.GetString(response) == "pong"
                ? new("loopback-tcp", "passed", null)
                : new("loopback-tcp", "failed", "loopback response differed");
        }
        finally
        {
            // A client-side failure can otherwise leave ServeOnceAsync waiting on
            // AcceptTcpClientAsync with its exception never observed. Cancel and
            // stop the listener, then observe the task within a fixed bound.
            serverCancellation.Cancel();
            listener.Stop();
            if (server is not null)
            {
                try
                {
                    await server.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The primary probe exception, if any, is reported by the
                    // caller; this await only prevents an unobserved task fault.
                }
            }
        }
    }

    private static async Task ServeOnceAsync(TcpListener listener, CancellationToken token)
    {
        using TcpClient connection = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
        await using NetworkStream stream = connection.GetStream();
        byte[] request = new byte[4];
        await stream.ReadExactlyAsync(request, token).ConfigureAwait(false);
        if (Encoding.UTF8.GetString(request) == "ping")
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
        string resolvedRunDirectory = Path.GetFullPath(runDirectory);
        const string scriptFileName = "transaction.sql";
        const string databaseFileName = "probe.db";
        string scriptPath = Path.Combine(resolvedRunDirectory, scriptFileName);
        string databasePath = Path.Combine(resolvedRunDirectory, databaseFileName);
        const string script = ".bail on\n.parameter init\n.parameter set :value 41\nBEGIN IMMEDIATE;\nCREATE TABLE values_table(value INTEGER);\nINSERT INTO values_table VALUES(:value);\nSELECT value + 1 FROM values_table;\nCOMMIT;\n";
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(false), token).ConfigureAwait(false);
        var runner = new BoundedProcessRunner(TimeSpan.FromSeconds(2));
        BoundedProcessResult version;
        try
        {
            version = await runner.RunAsync(new BoundedProcessRequest("sqlite3", ["--version"], resolvedRunDirectory, TimeSpan.FromSeconds(5)), token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return new("sqlite-transaction", "skipped", $"sqlite3 unavailable: {exception.Message}");
        }

        if (!IsBoundedSuccess(version))
        {
            return new("sqlite-transaction", "failed", DescribeSqliteFailure(version));
        }

        BoundedProcessResult result;
        try
        {
            // Keep paths as individual argv values. The -init option lets sqlite3 open the
            // script directly, so spaces, quotes, and platform-specific separators are not
            // re-parsed as part of a .read dot-command string. The final no-op SQL argument
            // prevents the CLI from waiting on inherited stdin after the init script finishes.
            result = await runner.RunAsync(
                new BoundedProcessRequest("sqlite3", ["-batch", "-init", scriptPath, databasePath, "SELECT 1 WHERE 0;"], resolvedRunDirectory, TimeSpan.FromSeconds(10)),
                token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return new("sqlite-transaction", "failed", $"sqlite3 became unavailable after its version probe: {exception.Message}");
        }

        return IsBoundedSuccess(result) && result.StandardOutput.Trim() == "42"
            ? new("sqlite-transaction", "passed", null)
            : new("sqlite-transaction", "failed", DescribeSqliteFailure(result));
    }

    private static bool IsBoundedSuccess(BoundedProcessResult result) =>
        result.Succeeded &&
        !result.OutputTruncated &&
        !result.OutputReadTimedOut &&
        !result.OutputDrainTimedOut &&
        !result.OutputReadLimitReached &&
        !result.ProcessTreeCleanupIncomplete;

    private static string DescribeSqliteFailure(BoundedProcessResult result)
    {
        string detail = FirstSqliteDiagnostic(result);
        return string.IsNullOrWhiteSpace(detail)
            ? "sqlite3 transaction failed"
            : $"sqlite3 transaction failed: {detail}";
    }

    private static string FirstSqliteDiagnostic(BoundedProcessResult result)
    {
        string? line = FirstNonEmptyLine(result.StandardError);
        line ??= FirstNonEmptyLine(result.StandardOutput);
        line ??= FirstNonEmptyLine(result.OutputDiagnostic);
        if (line is null && result.ProcessTreeCleanupDiagnostic is not null)
        {
            line = FirstNonEmptyLine(result.ProcessTreeCleanupDiagnostic);
        }

        return line is null
            ? string.Empty
            : line.Length <= 512 ? line : line[..512] + "...";
    }

    private static string? FirstNonEmptyLine(string? text) =>
        text?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .FirstOrDefault(static line => line.Length > 0);

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
