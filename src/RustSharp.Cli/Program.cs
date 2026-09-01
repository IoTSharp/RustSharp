using System.Globalization;
using System.Runtime.InteropServices;
using RustSharp.Compiler;
using RustSharp.Syntax;

namespace RustSharp.Cli;

internal static class Program
{
    private const string Version = "0.1.0-dev";
    private const int MaximumDisplayedDiagnostics = 100;

    public static async Task<int> Main(string[] args)
    {
        var parseResult = CommandLineParser.Parse(args);
        if (!parseResult.Success)
        {
            Console.Error.WriteLine($"rsc: {parseResult.Error}");
            Console.Error.WriteLine("Run 'rsc --help' for usage.");
            return 2;
        }

        var options = parseResult.Options!;
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            return options.Command switch
            {
                CommandKind.Help => WriteHelp(),
                CommandKind.Version => WriteVersion(),
                CommandKind.Check => Check(options),
                CommandKind.Compile => Compile(options),
                CommandKind.Run => await RunAsync(options, cancellation.Token).ConfigureAwait(false),
                CommandKind.Publish => await PublishAsync(options, cancellation.Token).ConfigureAwait(false),
                _ => 2,
            };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("rsc: operation cancelled.");
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static int Check(CommandLineOptions options)
    {
        var result = CompilerDriver.CheckFile(options.SourcePath!);
        if (!result.Success)
        {
            WriteDiagnostics(options.SourcePath!, result.Diagnostics);
            return 1;
        }

        Console.WriteLine($"Checked {Path.GetFullPath(options.SourcePath!)}");
        return 0;
    }

    private static int Compile(CommandLineOptions options)
    {
        var sourcePath = Path.GetFullPath(options.SourcePath!);
        var assemblyName = GetAssemblyName(sourcePath);
        var outputPath = options.OutputPath is null
            ? Path.Combine(Environment.CurrentDirectory, assemblyName + ".dll")
            : Path.GetFullPath(options.OutputPath);

        var result = CompilerDriver.CompileFile(sourcePath, outputPath, assemblyName);
        if (!result.Success)
        {
            WriteDiagnostics(sourcePath, result.Diagnostics);
            return 1;
        }

        WriteCompilationOutput(result.Output!);
        return 0;
    }

    private static async Task<int> RunAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.GetFullPath(options.SourcePath!);
        var assemblyName = GetAssemblyName(sourcePath);
        var outputPath = options.OutputPath is null
            ? Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "run",
                assemblyName,
                assemblyName + ".dll")
            : Path.GetFullPath(options.OutputPath);

        var result = CompilerDriver.CompileFile(sourcePath, outputPath, assemblyName);
        if (!result.Success)
        {
            WriteDiagnostics(sourcePath, result.Diagnostics);
            return 1;
        }

        WriteCompilationOutput(result.Output!);
        var workingDirectory = Path.GetDirectoryName(result.Output!.AssemblyPath)!;
        var processResult = await new BoundedProcessRunner().RunAsync(
            new BoundedProcessRequest(
                "dotnet",
                [result.Output.AssemblyPath],
                workingDirectory,
                TimeSpan.FromSeconds(options.TimeoutSeconds),
                WriteStartedProcess),
            cancellationToken).ConfigureAwait(false);

        WriteProcessOutput(processResult);
        return processResult.Succeeded ? 0 : 1;
    }

    private static async Task<int> PublishAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.GetFullPath(options.SourcePath!);
        var assemblyName = GetAssemblyName(sourcePath);
        var runtimeIdentifier = options.RuntimeIdentifier ?? RuntimeInformation.RuntimeIdentifier;
        var outputDirectory = options.OutputPath is null
            ? Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "publish",
                assemblyName,
                runtimeIdentifier)
            : Path.GetFullPath(options.OutputPath);
        var managedAssemblyPath = Path.Combine(
            outputDirectory,
            ".rsc",
            "managed",
            assemblyName + ".dll");

        var compilation = CompilerDriver.CompileFile(
            sourcePath,
            managedAssemblyPath,
            assemblyName);
        if (!compilation.Success)
        {
            WriteDiagnostics(sourcePath, compilation.Diagnostics);
            return 1;
        }

        WriteCompilationOutput(compilation.Output!);
        var request = new NativeAotPublishRequest(
            compilation.Output!.AssemblyPath,
            assemblyName,
            runtimeIdentifier,
            outputDirectory,
            TimeSpan.FromSeconds(options.TimeoutSeconds),
            WriteStartedProcess);
        var publishResult = await new NativeAotPublisher()
            .PublishAsync(request, cancellationToken)
            .ConfigureAwait(false);

        WriteProcessOutput(publishResult.ProcessResult);
        if (publishResult.HostCleanupDiagnostic is not null)
        {
            Console.Error.WriteLine($"Native AOT host cleanup: {publishResult.HostCleanupDiagnostic}");
        }

        if (!publishResult.Succeeded)
        {
            if (publishResult.HostCleanupIncomplete)
            {
                Console.Error.WriteLine(
                    "Native AOT publish did not pass the temporary-host cleanup gate; " +
                    "the output is not considered a successful publish.");
            }
            else
            {
                Console.Error.WriteLine($"Native AOT output was not produced at {publishResult.ExpectedExecutablePath}");
            }

            return 1;
        }

        Console.WriteLine($"Native AOT executable: {publishResult.ExecutablePath}");
        return 0;
    }

    private static int WriteHelp()
    {
        Console.WriteLine(
            """
            RustSharp compiler (rsc)

            Usage:
              rsc check <source.rs>
              rsc compile <source.rs> [--output <program.dll>]
              rsc run <source.rs> [--output <program.dll>] [--timeout <seconds>]
              rsc publish <source.rs> [--runtime <rid>] [--output <directory>] [--timeout <seconds>]
              rsc --version

            Current profile:
              Rust 1.98 / Edition 2024 vertical-slice-v1
              fn main() with zero or more println!(string-literal); statements
            """);
        return 0;
    }

    private static int WriteVersion()
    {
        Console.WriteLine($"rsc {Version} (Rust 1.98 / Edition 2024 vertical-slice-v1)");
        return 0;
    }

    private static void WriteDiagnostics(string sourcePath, IReadOnlyList<Diagnostic> diagnostics)
    {
        var displayed = Math.Min(diagnostics.Count, MaximumDisplayedDiagnostics);
        for (var index = 0; index < displayed; index++)
        {
            var diagnostic = diagnostics[index];
            Console.Error.WriteLine(
                $"{sourcePath}[{diagnostic.Span.Start}..{diagnostic.Span.End}]: error {diagnostic.Code}: {diagnostic.Message}");
        }

        if (diagnostics.Count > displayed)
        {
            Console.Error.WriteLine($"{diagnostics.Count - displayed} additional diagnostics were omitted.");
        }
    }

    private static void WriteCompilationOutput(CompilationOutput output)
    {
        Console.WriteLine($"Assembly: {output.AssemblyPath}");
        if (output.PdbPath is not null)
        {
            Console.WriteLine($"Portable PDB: {output.PdbPath}");
        }

        Console.WriteLine($"Runtime config: {output.RuntimeConfigPath}");
    }

    private static void WriteStartedProcess(BoundedProcessStarted process)
    {
        Console.Error.WriteLine(
            $"Started PID {process.ProcessId} at {process.StartedAt:O}; parent PID {process.ParentProcessId}; command: {process.CommandLine}");
    }

    private static void WriteProcessOutput(BoundedProcessResult process)
    {
        if (process.StandardOutput.Length != 0)
        {
            Console.Out.Write(process.StandardOutput);
        }

        if (process.StandardError.Length != 0)
        {
            Console.Error.Write(process.StandardError);
        }

        if (!process.Succeeded)
        {
            Console.Error.WriteLine(
                $"Process {process.StartedProcess.ProcessId} ended as {process.Termination} with exit code {process.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}.");
        }
    }

    private static string GetAssemblyName(string sourcePath)
    {
        var candidate = Path.GetFileNameWithoutExtension(sourcePath);
        Span<char> buffer = stackalloc char[Math.Min(candidate.Length, 128)];
        var length = 0;

        foreach (var character in candidate)
        {
            if (length == buffer.Length)
            {
                break;
            }

            buffer[length++] = char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'
                ? character
                : '_';
        }

        return length == 0 ? "program" : new string(buffer[..length]);
    }
}
