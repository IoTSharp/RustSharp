using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace RustSharp.Compiler;

public sealed record NativeAotPublishRequest(
    string GeneratedAssemblyPath,
    string AssemblyName,
    string RuntimeIdentifier,
    string OutputDirectory,
    TimeSpan Timeout,
    Action<BoundedProcessStarted>? OnProcessStarted = null);

public sealed record NativeAotPublishResult(
    string HostDirectory,
    string HostSourcePath,
    string HostProjectPath,
    string PublishDirectory,
    string ExpectedExecutablePath,
    string? ExecutablePath,
    BoundedProcessResult ProcessResult)
{
    public bool Succeeded =>
        ProcessResult.Succeeded &&
        ExecutablePath is not null &&
        !HostCleanupIncomplete;

    /// <summary>Gets a value indicating whether temporary host cleanup was attempted.</summary>
    public bool HostCleanupAttempted { get; init; }

    /// <summary>Gets a value indicating whether temporary host files could remain.</summary>
    public bool HostCleanupIncomplete { get; init; }

    /// <summary>Gets diagnostic details from temporary host cleanup, when available.</summary>
    public string? HostCleanupDiagnostic { get; init; }
}

public sealed class NativeAotPublisher
{
    private const string HostProjectFileName = "RustSharp.NativeAotHost.csproj";
    private const string HostSourceFileName = "Program.cs";
    private const int MaximumRuntimeIdentifierLength = 64;
    private const int MaximumCleanupAttempts = 8;

    private static readonly UTF8Encoding Utf8WithoutByteOrderMark = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan MaximumCleanupDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CleanupRetryBaseDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan CleanupRetryMaximumDelay = TimeSpan.FromMilliseconds(500);

    private readonly BoundedProcessRunner processRunner;

    public NativeAotPublisher()
        : this(new BoundedProcessRunner())
    {
    }

    public NativeAotPublisher(BoundedProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        this.processRunner = processRunner;
    }

    public Task<NativeAotPublishResult> PublishAsync(
        string generatedAssemblyPath,
        string assemblyName,
        string runtimeIdentifier,
        string outputDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        PublishAsync(
            new NativeAotPublishRequest(
                generatedAssemblyPath,
                assemblyName,
                runtimeIdentifier,
                outputDirectory,
                timeout),
            cancellationToken);

    public async Task<NativeAotPublishResult> PublishAsync(
        NativeAotPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSimpleName(request.AssemblyName, nameof(request.AssemblyName));
        ValidateRuntimeIdentifier(request.RuntimeIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.GeneratedAssemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);

        var generatedAssemblyPath = Path.GetFullPath(request.GeneratedAssemblyPath);
        if (!File.Exists(generatedAssemblyPath))
        {
            throw new FileNotFoundException(
                "The RustSharp-generated IL assembly was not found.",
                generatedAssemblyPath);
        }

        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        var hostDirectory = Path.Combine(
            outputDirectory,
            ".rsc",
            "nativeaot-host",
            $"{request.AssemblyName}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var hostSourcePath = Path.Combine(hostDirectory, HostSourceFileName);
        var hostProjectPath = Path.Combine(hostDirectory, HostProjectFileName);
        var hostAssemblyFileName = request.AssemblyName + ".dll";
        var hostAssemblyPath = Path.Combine(hostDirectory, hostAssemblyFileName);
        var hostAssemblyName = $"{request.AssemblyName}.NativeAotHost";
        string? hostParentDirectory = Path.GetDirectoryName(hostDirectory);
        bool hostParentExisted = hostParentDirectory is not null && Directory.Exists(hostParentDirectory);
        var ownsHostParentDirectory = false;

        NativeAotPublishResult? publishResult = null;
        BoundedProcessResult? processResultForCleanup = null;
        HostCleanupStatus cleanupStatus = HostCleanupStatus.None;

        try
        {
            Directory.CreateDirectory(hostDirectory);
            ownsHostParentDirectory = !hostParentExisted;

            File.Copy(generatedAssemblyPath, hostAssemblyPath, overwrite: false);

            var hostSource = CreateHostSource();
            var hostProject = CreateHostProject(
                request.AssemblyName,
                hostAssemblyName,
                hostAssemblyFileName);

            await File.WriteAllTextAsync(
                hostSourcePath,
                hostSource,
                Utf8WithoutByteOrderMark,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                hostProjectPath,
                hostProject,
                Utf8WithoutByteOrderMark,
                cancellationToken).ConfigureAwait(false);

            var processRequest = new BoundedProcessRequest(
                "dotnet",
                new[]
                {
                    "publish",
                    hostProjectPath,
                    "-c",
                    "Release",
                    "-r",
                    request.RuntimeIdentifier,
                    "--self-contained",
                    "true",
                    "-p:PublishAot=true",
                    "-warnaserror",
                    "-p:ImportDirectoryBuildProps=false",
                    "-p:ImportDirectoryBuildTargets=false",
                    "-o",
                    outputDirectory,
                },
                hostDirectory,
                request.Timeout,
                request.OnProcessStarted);

            var processResult = await processRunner.RunAsync(processRequest, cancellationToken).ConfigureAwait(false);
            processResultForCleanup = processResult;
            var executableExtension = IsWindowsRuntimeIdentifier(request.RuntimeIdentifier)
                ? ".exe"
                : string.Empty;
            var expectedExecutablePath = Path.Combine(
                outputDirectory,
                hostAssemblyName + executableExtension);
            var executablePath = processResult.Succeeded && File.Exists(expectedExecutablePath)
                ? expectedExecutablePath
                : null;

            publishResult = new NativeAotPublishResult(
                hostDirectory,
                hostSourcePath,
                hostProjectPath,
                outputDirectory,
                expectedExecutablePath,
                executablePath,
                processResult);
        }
        finally
        {
            cleanupStatus = await CleanupHostDirectoryAsync(
                hostDirectory,
                ownsHostParentDirectory ? hostParentDirectory : null,
                processResultForCleanup?.ProcessTreeCleanupIncomplete == true).ConfigureAwait(false);
        }

        return publishResult! with
        {
            HostCleanupAttempted = cleanupStatus.Attempted,
            HostCleanupIncomplete = cleanupStatus.Incomplete,
            HostCleanupDiagnostic = cleanupStatus.Diagnostic,
        };
    }

    private static string CreateHostSource() =>
        """
        namespace RustSharp.NativeAotHost;

        internal static class EntryPoint
        {
            private static void Main() => global::RustSharp.Generated.Program.Main();
        }
        """;

    private static string CreateHostProject(
        string generatedAssemblyName,
        string hostAssemblyName,
        string generatedAssemblyFileName)
    {
        var document = new XDocument(
            new XElement(
                "Project",
                new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement(
                    "PropertyGroup",
                    new XElement("OutputType", "Exe"),
                    new XElement("TargetFramework", "net10.0"),
                    new XElement("AssemblyName", hostAssemblyName),
                    new XElement("RootNamespace", "RustSharp.NativeAotHost"),
                    new XElement("ImplicitUsings", "disable"),
                    new XElement("Nullable", "enable"),
                    new XElement("EnableDefaultCompileItems", "false"),
                    new XElement("PublishAot", "true"),
                    new XElement("SelfContained", "true"),
                    new XElement("IsAotCompatible", "true"),
                    new XElement("TreatWarningsAsErrors", "true"),
                    new XElement("ImportDirectoryBuildProps", "false"),
                    new XElement("ImportDirectoryBuildTargets", "false")),
                new XElement(
                    "ItemGroup",
                    new XElement("Compile", new XAttribute("Include", HostSourceFileName)),
                    new XElement(
                        "Reference",
                        new XAttribute("Include", generatedAssemblyName),
                        new XElement("HintPath", generatedAssemblyFileName),
                        new XElement("Private", "true")))));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static void ValidateSimpleName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        if (value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains(Path.DirectorySeparatorChar) ||
            value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("The assembly name must be a valid simple file name.", parameterName);
        }
    }

    private static void ValidateRuntimeIdentifier(string runtimeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        if (runtimeIdentifier.Length > MaximumRuntimeIdentifierLength)
        {
            throw new ArgumentException(
                $"The runtime identifier must be no longer than {MaximumRuntimeIdentifierLength} characters.",
                nameof(runtimeIdentifier));
        }

        foreach (char character in runtimeIdentifier)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
            {
                throw new ArgumentException(
                    "The runtime identifier contains an invalid character.",
                    nameof(runtimeIdentifier));
            }
        }
    }

    private static async Task<HostCleanupStatus> CleanupHostDirectoryAsync(
        string hostDirectory,
        string? parentDirectory,
        bool processTreeCleanupIncomplete)
    {
        try
        {
            var diagnostics = new List<string>(capacity: 3);
            var cleanupClock = Stopwatch.StartNew();
            CleanupAttempt hostCleanup = await TryDeleteDirectoryAsync(
                hostDirectory,
                recursive: true,
                cleanupClock).ConfigureAwait(false);
            if (hostCleanup.Diagnostic is not null)
            {
                diagnostics.Add(hostCleanup.Diagnostic);
            }

            CleanupAttempt parentCleanup = await TryDeleteEmptyDirectoryAsync(
                parentDirectory,
                cleanupClock).ConfigureAwait(false);
            if (parentCleanup.Diagnostic is not null)
            {
                diagnostics.Add(parentCleanup.Diagnostic);
            }

            if (processTreeCleanupIncomplete)
            {
                diagnostics.Add(
                    "The publish process reported incomplete process-tree cleanup; temporary host files may have remained locked.");
            }

            var attempted = hostCleanup.Attempted || parentCleanup.Attempted || processTreeCleanupIncomplete;
            var incomplete = processTreeCleanupIncomplete ||
                             hostCleanup.Incomplete ||
                             parentCleanup.Incomplete;
            return new HostCleanupStatus(
                attempted,
                incomplete,
                diagnostics.Count == 0 ? null : string.Join(" ", diagnostics));
        }
        catch (Exception exception) when (IsCleanupException(exception))
        {
            return new HostCleanupStatus(
                Attempted: true,
                Incomplete: true,
                Diagnostic: $"Temporary Native AOT host cleanup failed with {exception.GetType().Name}: {TrimCleanupDiagnostic(exception.Message)}");
        }
    }

    private static async Task<CleanupAttempt> TryDeleteDirectoryAsync(
        string path,
        bool recursive,
        Stopwatch cleanupClock)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return CleanupAttempt.None;
        }

        Exception? lastException = null;
        var attempted = false;
        for (var attempt = 1; attempt <= MaximumCleanupAttempts; attempt++)
        {
            if (cleanupClock.Elapsed >= MaximumCleanupDuration)
            {
                break;
            }

            if (!Directory.Exists(path))
            {
                return new CleanupAttempt(attempted, false, null);
            }

            attempted = true;
            try
            {
                Directory.Delete(path, recursive);
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
                return new CleanupAttempt(attempted, false, null);
            }

            if (!await WaitForCleanupRetryAsync(attempt, cleanupClock).ConfigureAwait(false))
            {
                break;
            }
        }

        return new CleanupAttempt(
            attempted,
            Directory.Exists(path),
            FormatCleanupDiagnostic(path, recursive, lastException));
    }

    private static async Task<CleanupAttempt> TryDeleteEmptyDirectoryAsync(
        string? path,
        Stopwatch cleanupClock)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return CleanupAttempt.None;
        }

        Exception? lastException = null;
        var attempted = false;
        for (var attempt = 1; attempt <= MaximumCleanupAttempts; attempt++)
        {
            if (cleanupClock.Elapsed >= MaximumCleanupDuration)
            {
                break;
            }

            if (!Directory.Exists(path))
            {
                return new CleanupAttempt(attempted, false, null);
            }

            Exception? inspectionException = null;
            var isEmpty = false;
            try
            {
                using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                isEmpty = !entries.MoveNext();
            }
            catch (IOException exception)
            {
                inspectionException = exception;
            }
            catch (UnauthorizedAccessException exception)
            {
                inspectionException = exception;
            }
            catch (NotSupportedException exception)
            {
                inspectionException = exception;
            }
            catch (ArgumentException exception)
            {
                inspectionException = exception;
            }

            attempted = true;
            if (inspectionException is null && !isEmpty)
            {
                // A sibling publish owns this parent directory; leaving it in
                // place is expected and is not an incomplete cleanup.
                return new CleanupAttempt(attempted, false, null);
            }

            if (inspectionException is null)
            {
                try
                {
                    Directory.Delete(path);
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
                    return new CleanupAttempt(attempted, false, null);
                }
            }
            else
            {
                lastException = inspectionException;
            }

            if (!await WaitForCleanupRetryAsync(attempt, cleanupClock).ConfigureAwait(false))
            {
                break;
            }
        }

        if (!Directory.Exists(path))
        {
            return new CleanupAttempt(attempted, false, null);
        }

        try
        {
            using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            if (entries.MoveNext())
            {
                return new CleanupAttempt(attempted, false, null);
            }
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

        return new CleanupAttempt(
            attempted,
            true,
            FormatCleanupDiagnostic(path, recursive: false, lastException));
    }

    private static async Task<bool> WaitForCleanupRetryAsync(int attempt, Stopwatch cleanupClock)
    {
        TimeSpan remaining = MaximumCleanupDuration - cleanupClock.Elapsed;
        if (remaining <= TimeSpan.Zero || attempt >= MaximumCleanupAttempts)
        {
            return false;
        }

        double delayMilliseconds = Math.Min(
            CleanupRetryMaximumDelay.TotalMilliseconds,
            CleanupRetryBaseDelay.TotalMilliseconds * attempt);
        TimeSpan delay = TimeSpan.FromMilliseconds(delayMilliseconds);
        if (delay > remaining)
        {
            delay = remaining;
        }

        if (delay <= TimeSpan.Zero)
        {
            return false;
        }

        await Task.Delay(delay).ConfigureAwait(false);
        return cleanupClock.Elapsed < MaximumCleanupDuration;
    }

    private static string FormatCleanupDiagnostic(
        string path,
        bool recursive,
        Exception? lastException)
    {
        var operation = recursive ? "temporary host directory" : "temporary host parent directory";
        var detail = lastException is null
            ? "The path remained present after the cleanup budget."
            : $"The last error was {lastException.GetType().Name}: {TrimCleanupDiagnostic(lastException.Message)}";
        return $"Could not remove {operation} '{path}' after {MaximumCleanupAttempts} attempts or {MaximumCleanupDuration.TotalSeconds:0.#} seconds. {detail}";
    }

    private static bool IsCleanupException(Exception exception) =>
        exception is IOException or
        UnauthorizedAccessException or
        NotSupportedException or
        ArgumentException or
        System.Security.SecurityException;

    private static string TrimCleanupDiagnostic(string value) =>
        value.Length <= 512 ? value : value[..512] + "...";

    private static bool IsWindowsRuntimeIdentifier(string runtimeIdentifier) =>
        runtimeIdentifier.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ||
        (runtimeIdentifier.StartsWith("win", StringComparison.OrdinalIgnoreCase) &&
         runtimeIdentifier.Length > 3 &&
         char.IsAsciiDigit(runtimeIdentifier[3]));

    private readonly record struct CleanupAttempt(bool Attempted, bool Incomplete, string? Diagnostic)
    {
        public static CleanupAttempt None => new(false, false, null);
    }

    private readonly record struct HostCleanupStatus(bool Attempted, bool Incomplete, string? Diagnostic)
    {
        public static HostCleanupStatus None => new(false, false, null);
    }
}
