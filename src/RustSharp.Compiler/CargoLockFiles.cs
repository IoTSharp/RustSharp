using System.Diagnostics;
using System.Text;

namespace RustSharp.Compiler;

internal sealed class CargoLockCleanupException(string message, Exception? inner = null) : IOException(message, inner);

internal static class CargoLockFiles
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Decode(byte[] bytes, string path)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new CargoLoadException(CargoLockResolver.IncompatibleLockDiagnostic, "Cargo lock input is not strict UTF-8.", path, default); }
    }

    internal static byte[] Read(string path, CargoWorkspaceOptions limits, CargoLoadBudget budget)
    {
        CheckPath(path, budget);
        if (!File.Exists(path)) throw new CargoLoadException(CargoLockResolver.IncompatibleLockDiagnostic, "Locked mode requires both Cargo.lock and Cargo.lock.rustsharp.json.", path, default);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = stream.Length;
        if (length > limits.MaximumLockBytes) throw new CargoLoadException(CargoWorkspace.LimitDiagnostic, "Cargo lock input exceeded its byte limit before decoding or parsing.", path, default);
        byte[] bytes = new byte[(int)length]; int total = 0;
        for (int read = 0; read <= bytes.Length && total < bytes.Length; read++)
        {
            budget.Step(path, default);
            int count = stream.Read(bytes, total, Math.Min(4096, bytes.Length - total));
            if (count == 0) throw new CargoLoadException(CargoLockResolver.IncompatibleLockDiagnostic, "Cargo lock changed while being read.", path, default);
            total += count;
        }
        budget.Check(path, default);
        if (stream.ReadByte() != -1 || stream.Length != length) throw new CargoLoadException(CargoLockResolver.IncompatibleLockDiagnostic, "Cargo lock changed while being read.", path, default);
        return bytes;
    }

    internal static void ReplacePair(string lockPath, string lockText, string resolutionPath, string resolutionText,
        CargoWorkspaceOptions limits, CargoLoadBudget budget)
    {
        CheckPath(lockPath, budget); CheckPath(resolutionPath, budget);
        string directory = Path.GetDirectoryName(lockPath)!;
        string guardPath = Path.Combine(directory, ".rustsharp-lock.write.guard");
        string nonce = Guid.NewGuid().ToString("N");
        string[] staging = [Path.Combine(directory, ".rustsharp-lock-" + nonce + ".toml.tmp"), Path.Combine(directory, ".rustsharp-lock-" + nonce + ".json.tmp")];
        string[] targets = [lockPath, resolutionPath]; string[] texts = [lockText, resolutionText];
        byte[]?[] originals = new byte[]?[2]; bool[] changed = new bool[2];
        bool guardOwned = false; bool[] stagingOwned = new bool[2]; Exception? cleanupError = null;
        Exception? operationError = null;
        try
        {
            using var guard = new FileStream(guardPath, FileMode.CreateNew, FileAccess.Write, FileShare.None); guardOwned = true;
            for (int index = 0; index < 2; index++)
            {
                budget.Step(targets[index], default);
                originals[index] = File.Exists(targets[index]) ? Read(targets[index], limits, budget) : null;
                WriteStaged(staging[index], Encoding.UTF8.GetBytes(texts[index]), ref stagingOwned[index]);
            }
            budget.Check(lockPath, default); // Cancellation/deadline is honored before the fixed two-file commit.
            Stopwatch commitClock = Stopwatch.StartNew();
            try
            {
                for (int index = 0; index < 2; index++)
                {
                    if (commitClock.Elapsed >= TimeSpan.FromSeconds(10)) throw new IOException("Cargo lock commit deadline exceeded.");
                    File.Move(staging[index], targets[index], overwrite: true); changed[index] = true;
                }
            }
            catch (Exception commitError) when (commitError is IOException or UnauthorizedAccessException)
            {
                Stopwatch rollbackClock = Stopwatch.StartNew();
                for (int index = 1; index >= 0; index--)
                {
                    if (!changed[index]) continue;
                    try
                    {
                        if (rollbackClock.Elapsed >= TimeSpan.FromSeconds(10)) throw new CargoLockCleanupException("Cargo lock rollback deadline exceeded.");
                        if (originals[index] is null) File.Delete(targets[index]);
                        else { WriteStaged(staging[index], originals[index]!, ref stagingOwned[index]); File.Move(staging[index], targets[index], overwrite: true); }
                    }
                    catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                    { cleanupError = new CargoLockCleanupException("Cargo lock commit failed and rollback could not restore the prior pair.", new AggregateException(commitError, rollbackError)); }
                }
                if (cleanupError is not null) throw cleanupError;
                throw;
            }
        }
        catch (Exception exception)
        {
            operationError = exception;
        }
        finally
        {
            cleanupError = CleanupOwned(directory, guardPath, guardOwned, staging, stagingOwned);
        }
        if (cleanupError is not null)
            throw new CargoLockCleanupException("Cargo lock staging cleanup failed; retained paths require review.",
                operationError is null ? cleanupError : new AggregateException(operationError, cleanupError));
        if (operationError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(operationError).Throw();
    }

    private static CargoLockCleanupException? CleanupOwned(string directory, string guardPath, bool guardOwned,
        string[] staging, bool[] stagingOwned)
    {
        // Independent cleanup is fixed at three explicitly owned names and a ten-second wall deadline.
        Stopwatch cleanupClock = Stopwatch.StartNew();
        var failures = new List<Exception>();
        for (int index = 0; index < 3; index++)
        {
            string owned = index < 2 ? staging[index] : guardPath;
            if (index == 2 && !guardOwned) continue;
            if (index < 2 && !stagingOwned[index]) continue;
            try
            {
                if (cleanupClock.Elapsed >= TimeSpan.FromSeconds(10) || Path.GetDirectoryName(Path.GetFullPath(owned)) != directory)
                    throw new CargoLockCleanupException("Cargo lock cleanup ownership or deadline validation failed.");
                File.Delete(owned);
                if (File.Exists(owned)) throw new CargoLockCleanupException("Cargo lock staging file remained after cleanup.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(exception);
            }
        }
        return failures.Count == 0 ? null : new CargoLockCleanupException("Cargo lock owned cleanup failed.", new AggregateException(failures));
    }

    private static void WriteStaged(string path, byte[] bytes, ref bool owned)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        owned = true;
        stream.Write(bytes); stream.Flush(flushToDisk: true);
    }

    private static void CheckPath(string path, CargoLoadBudget budget)
    {
        string? component = Path.GetFullPath(path);
        for (int depth = 0; component is not null && depth < 128; depth++)
        {
            budget.Step(path, default);
            if (new FileInfo(component).LinkTarget is not null || new DirectoryInfo(component).LinkTarget is not null ||
                (File.Exists(component) || Directory.Exists(component)) && (File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                throw new CargoLoadException(CargoLockResolver.IncompatibleLockDiagnostic, "Cargo lock paths cannot traverse filesystem links.", path, default);
            component = Path.GetDirectoryName(component);
        }
        if (component is not null) throw new CargoLoadException(CargoWorkspace.LimitDiagnostic, "Cargo lock path depth exceeded its bound.", path, default);
    }
}
