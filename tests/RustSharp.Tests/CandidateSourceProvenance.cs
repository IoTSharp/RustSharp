using System.Diagnostics;
using System.Text;

namespace RustSharp.Tests;

internal static class CandidateSourceProvenance
{
    private const int MaximumFiles = 4096;
    private const int MaximumOutputCharacters = 2 * 1024 * 1024;
    private const int MaximumOutputReadChunks = 8192;
    internal sealed record ProcessEvidence(int ProcessId, int ParentProcessId, DateTimeOffset StartedAtUtc,
        string FileName, IReadOnlyList<string> Arguments, int? ExitCode, bool CleanupComplete)
    {
        public string CommandLine => FileName + " " + string.Join(' ', Arguments.Select(static item => "\"" + item.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""));
    }
    internal sealed record Evidence(string CandidateSha, string? CandidateTreeSha, bool CandidateMatchesWorkingTree,
        int CheckedFileCount, IReadOnlyList<string> Errors, IReadOnlyList<ProcessEvidence> Processes);

    internal static async Task<Evidence> VerifyAsync(string sha, string repositoryRoot, CancellationToken token)
    {
        var errors = new List<string>();
        var processes = new List<ProcessEvidence>();
        string? tree = null;
        int checkedFiles = 0;
        try
        {
            if (!RegressionHarness.IsCommitSha(sha)) throw new ArgumentException("Candidate SHA must be a full commit identity.", nameof(sha));
            string commit = (await GitAsync(["rev-parse", sha + "^{commit}"], null, repositoryRoot, processes, token).ConfigureAwait(false)).Trim();
            if (!string.Equals(commit, sha, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Candidate identity must refer to a commit.");
            tree = (await GitAsync(["rev-parse", sha + "^{tree}"], null, repositoryRoot, processes, token).ConfigureAwait(false)).Trim();
            if (!RegressionHarness.IsCommitSha(tree)) throw new InvalidOperationException("Candidate tree is unavailable.");
            string inventory = await GitAsync(["ls-tree", "-rz", sha], null, repositoryRoot, processes, token).ConfigureAwait(false);
            string[] entries = inventory.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (entries.Length is 0 or > MaximumFiles) throw new InvalidOperationException("Candidate inventory exceeds its fixed file bound.");
            var expected = new List<string>(entries.Length);
            var candidatePaths = new HashSet<string>(StringComparer.Ordinal);
            var paths = new StringBuilder();
            foreach (string entry in entries)
            {
                token.ThrowIfCancellationRequested();
                int tab = entry.IndexOf('\t');
                if (tab < 0) throw new InvalidOperationException("Candidate tree entry is malformed.");
                string[] header = entry[..tab].Split(' ');
                string relative = entry[(tab + 1)..];
                if (header.Length != 3 || header[1] != "blob" || !RegressionHarness.IsCommitSha(header[2]) || relative.Length > 1024 ||
                    relative.IndexOfAny(['\r', '\n', '"']) >= 0 || Path.IsPathRooted(relative))
                    throw new InvalidOperationException("Candidate inventory contains an unsupported entry.");
                string file = Path.GetFullPath(relative, repositoryRoot);
                string relation = Path.GetRelativePath(repositoryRoot, file);
                if (relation.StartsWith("..", StringComparison.Ordinal) || !File.Exists(file))
                    throw new InvalidOperationException("Candidate source file is absent or outside the repository: " + relative);
                expected.Add(header[2]);
                candidatePaths.Add(relative);
                paths.Append('"').Append(file.Replace('\\', '/')).Append('"').Append('\n');
            }
            string[] workingPaths = (await GitAsync(["ls-files", "--cached", "--others", "--exclude-standard", "-z"], null,
                repositoryRoot, processes, token).ConfigureAwait(false)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (workingPaths.Length > MaximumFiles) throw new InvalidOperationException("Working source inventory exceeds its file bound.");
            foreach (string relative in workingPaths)
            {
                token.ThrowIfCancellationRequested();
                if (IsCompilerInput(relative) && !candidatePaths.Contains(relative))
                    errors.Add("Working compiler input is absent from candidate: " + relative);
            }
            string[] actual = (await GitAsync(["hash-object", "--stdin-paths"], paths.ToString(), repositoryRoot, processes, token).ConfigureAwait(false))
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (actual.Length != expected.Count) throw new InvalidOperationException("Git did not hash every candidate file.");
            for (int index = 0; index < actual.Length && index < MaximumFiles; index++)
            {
                token.ThrowIfCancellationRequested();
                checkedFiles++;
                if (!string.Equals(actual[index], expected[index], StringComparison.OrdinalIgnoreCase))
                    errors.Add("Working source differs from candidate: " + entries[index][(entries[index].IndexOf('\t') + 1)..]);
            }
        }
        catch (Exception exception) { errors.Add(exception.Message); }
        return new(sha, tree, errors.Count == 0 && checkedFiles > 0 && processes.All(static item => item.CleanupComplete), checkedFiles, errors, processes);
    }

    private static bool IsCompilerInput(string path) => path.StartsWith("src/", StringComparison.Ordinal) ||
        path.StartsWith("tests/", StringComparison.Ordinal) || path.StartsWith("tools/", StringComparison.Ordinal) ||
        path.StartsWith("eng/", StringComparison.Ordinal) || path.StartsWith(".github/", StringComparison.Ordinal) ||
        path.StartsWith(".config/", StringComparison.Ordinal) || path.StartsWith("samples/", StringComparison.Ordinal) ||
        path is ".gitattributes" or ".gitignore" || (!path.Contains('/') && Path.GetExtension(path) is ".props" or ".targets" or ".slnx" or ".json");

    private static async Task<string> GitAsync(string[] arguments, string? input, string root, List<ProcessEvidence> evidence, CancellationToken token)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process
        {
            StartInfo = new("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true }
        };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        bool didStart = false;
        try
        {
            bounded.Token.ThrowIfCancellationRequested();
            if (!process.Start()) throw new InvalidOperationException("Git failed to start.");
            didStart = true;
            Task<string> output = ReadBoundedAsync(process.StandardOutput, bounded.Token);
            Task<string> error = ReadBoundedAsync(process.StandardError, bounded.Token);
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), bounded.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
            string result = await output.ConfigureAwait(false);
            string diagnostic = await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("Git candidate verification failed: " + diagnostic);
            return result;
        }
        finally
        {
            if (didStart)
            {
                // This Process object was created and started above; it is the owned Git child.
                if (!process.HasExited) { process.Kill(entireProcessTree: true); _ = process.WaitForExit(5000); }
                evidence.Add(new(process.Id, Environment.ProcessId, started, process.StartInfo.FileName, arguments,
                    process.HasExited ? process.ExitCode : null, process.HasExited));
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var output = new StringBuilder();
        char[] buffer = new char[4096];
        for (int chunk = 0; chunk < MaximumOutputReadChunks; chunk++)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) return output.ToString();
            if (output.Length + count > MaximumOutputCharacters) throw new InvalidOperationException("Git output exceeded its byte budget.");
            output.Append(buffer, 0, count);
        }
        throw new InvalidOperationException("Git output exceeded its chunk budget.");
    }
}
