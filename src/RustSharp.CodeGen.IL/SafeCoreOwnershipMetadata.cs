using RustSharp.Semantics;

namespace RustSharp.CodeGen.IL;

/// <summary>
/// Bridges a successful ownership analysis into the persisted metadata carried
/// by an emitted program.  This is intentionally an evidence bridge: it does
/// not infer ownership from CLR layouts and refuses truncated/failed analyses.
/// </summary>
public static class SafeCoreOwnershipMetadata
{
    public static SafeCoreClrResult Attach(
        SafeCoreClrResult result,
        SafeCoreMirOwnershipResult ownership)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(ownership);
        if (!ownership.IsSuccessful || ownership.Program is null || ownership.Ownership is null)
            throw new ArgumentException("A successful ownership result is required.", nameof(ownership));
        if (result.Methods.Any(static method => method.IsCompilerGenerated &&
            (method.IsPublic || method.SourceQualifiedName is not null)))
            throw new ArgumentException("Compiler helpers must be internal and cannot claim a source ownership identity.", nameof(result));
        if (ownership.Program.Functions.Count != result.Methods.Count(static method => !method.IsCompilerGenerated))
            throw new ArgumentException("Ownership and emitted method counts must match.", nameof(ownership));

        var facts = new List<RustSharpMetadataOwnershipFunction>(ownership.Program.Functions.Count);
        foreach (SafeCoreOwnershipFunction function in ownership.Program.Functions)
        {
            SafeCoreOwnershipPath[] paths = [.. ownership.Ownership.Paths
                .Where(path => string.Equals(path.FunctionName, function.Name, StringComparison.Ordinal))];
            if (paths.Length == 0)
                throw new ArgumentException("Ownership analysis has no path evidence for '" + function.Name + "'.", nameof(ownership));

            string[] drops = [.. paths.SelectMany(static path => path.DropOrder)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];
            string[] outcomes = [.. paths.Select(static path => path.Outcome.ToString())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];
            string[] borrows = [.. paths.SelectMany(static path => path.Trace)
                .Where(static value => value.StartsWith("borrow", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];
            facts.Add(new(
                function.Name,
                function.PanicStrategy.ToString().ToLowerInvariant(),
                drops,
                outcomes,
                borrows));
        }

        return result with { Ownership = facts };
    }
}
