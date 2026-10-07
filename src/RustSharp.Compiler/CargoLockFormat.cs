using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Compiler;

internal static class CargoLockFormat
{
    internal static string Serialize(IReadOnlyList<CargoLockPackage> packages, string path, CargoLoadBudget budget)
    {
        var text = new StringBuilder("version = 4\n");
        foreach (CargoLockPackage package in packages)
        {
            budget.Step(path, default);
            text.Append("\n[[package]]\nname = \"").Append(package.Name).Append("\"\nversion = \"").Append(package.Version)
                .Append("\"\ndependencies = [");
            for (int index = 0; index < package.Dependencies.Count; index++)
            {
                budget.Step(path, default);
                if (index != 0) text.Append(", ");
                text.Append('"').Append(package.Dependencies[index]).Append('"');
            }
            text.Append("]\n");
        }
        budget.Check(path, default);
        return text.ToString();
    }

    internal static IReadOnlyList<CargoLockPackage> Parse(string text, string path, CargoWorkspaceOptions limits, CargoLoadBudget budget)
    {
        CargoParsedManifest parsed;
        try { parsed = new CargoManifestParser(path, text, budget, lockFormat: true).Parse(); }
        catch (CargoLoadException exception) when (exception.Code != CargoWorkspace.LimitDiagnostic)
        { throw new CargoLoadException(CargoLockResolver.IncompatibleLockDiagnostic, exception.Message, exception.SourcePath, exception.Span); }
        CargoTomlTable root = parsed.Tables[0];
        if (root.Entries.Count != 1 || !root.Entries.TryGetValue("version", out CargoTomlEntry? format) || format.Value.Value is not int version || version != 4)
            Fail("Cargo.lock requires its only top-level key to be version = 4.", path, root.Span);
        var packages = new List<CargoLockPackage>(); var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (CargoTomlTable table in parsed.Tables.Skip(1))
        {
            budget.Step(path, table.Span);
            if (!table.IsArray || table.Path.Count != 1 || table.Path[0] != "package" || table.Entries.Keys.Any(static key => key is not "name" and not "version" and not "dependencies"))
                Fail("Cargo.lock admits only [[package]] name/version/dependencies keys; source/checksum are unsupported.", path, table.Span);
            if (packages.Count >= limits.MaximumPackages) throw new CargoLoadException(CargoWorkspace.LimitDiagnostic, "Cargo.lock package count exceeded its bound.", path, table.Span);
            string name = String(table, "name", path); string packageVersion = String(table, "version", path);
            if (name.Length is < 1 or > 128 || !(char.IsAsciiLetter(name[0]) || name[0] == '_') || !name.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                Fail("Cargo.lock contains an invalid package name.", path, table.Entries["name"].Value.Span);
            string[] parts = packageVersion.Split('.');
            if (packageVersion.Length > 128 || parts.Length != 3 || parts.Any(static p => p.Length == 0 || p.Length > 1 && p[0] == '0' || !p.All(char.IsAsciiDigit)))
                Fail("Cargo.lock requires an exact numeric package version.", path, table.Entries["version"].Value.Span);
            string identity = name + "@" + packageVersion;
            if (!identities.Add(identity)) Fail("Duplicate Cargo.lock package identity.", path, table.Span);
            var edges = new HashSet<string>(StringComparer.Ordinal);
            if (table.Entries.TryGetValue("dependencies", out CargoTomlEntry? dependencies))
            {
                if (dependencies.Value.Value is not IReadOnlyList<CargoTomlValue>) Fail("Cargo.lock dependencies must be a single-line string array.", path, dependencies.Value.Span);
                var values = (IReadOnlyList<CargoTomlValue>)dependencies.Value.Value;
                if (values.Count > limits.MaximumDependenciesPerPackage) throw new CargoLoadException(CargoWorkspace.LimitDiagnostic, "Cargo.lock dependency count exceeded its bound.", path, dependencies.Value.Span);
                foreach (CargoTomlValue value in values)
                {
                    budget.Step(path, value.Span);
                    if (value.Value is not string edge || !edges.Add(edge)) Fail("Cargo.lock dependencies must be unique strings.", path, value.Span);
                }
            }
            packages.Add(new(name, packageVersion, Array.AsReadOnly(edges.Order(StringComparer.Ordinal).ToArray())));
        }
        if (packages.Count == 0) Fail("Cargo.lock must contain at least one package.", path, default);
        return Array.AsReadOnly(packages.OrderBy(static p => p.Identity, StringComparer.Ordinal).ToArray());
    }

    private static string String(CargoTomlTable table, string key, string path)
    {
        if (!table.Entries.TryGetValue(key, out CargoTomlEntry? entry) || entry.Value.Value is not string)
            Fail("Cargo.lock requires a string " + key + ".", path, table.Span);
        return (string)entry!.Value.Value;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string message, string path, TextSpan span) => throw new CargoLoadException(CargoLockResolver.IncompatibleLockDiagnostic, message, path, span);
}
