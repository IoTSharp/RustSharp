using System.Text;
using RustSharp.Compiler;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreWorkspaceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("workspace loads only declared modules and maps their source", LoadsDeclaredModulesAsync),
        new("workspace resolves flat nested inline and raw modules", ResolvesNestedModulesAsync),
        new("workspace diagnoses missing and conflicting module files", DiagnosesMissingAndConflictingAsync),
        new("workspace retains child syntax paths and spans", RetainsSyntaxDiagnosticsAsync),
        new("workspace preserves UTF8 bytes and rejects invalid encoding", PreservesEncodingAsync),
        new("workspace handles shebang comments and module inner attributes", PreservesPreamblesAsync),
        new("workspace refuses module attribute evaluation", RejectsModuleAttributesAsync),
        new("workspace enforces file byte source depth operation and time budgets", EnforcesBudgetsAsync),
        new("workspace propagates caller cancellation", PropagatesCancellationAsync),
        new("workspace rejects linked source files and directories", RejectsLinksAsync),
        new("workspace clips mappings to original source fragments", MapsBoundariesAsync),
        new("workspace source map rejects invalid public construction", RejectsInvalidMapsAsync),
    ];

    private static Task LoadsDeclaredModulesAsync() => WithFixture(fixture =>
    {
        fixture.Write("main.rs", "mod api; fn main() { api::value(); }");
        const string child = "pub fn value() -> i32 { 42 }";
        string childPath = fixture.Write("api.rs", child);
        fixture.WriteBytes("unmentioned.rs", [0xff]);
        SafeCoreWorkspaceResult result = fixture.Load();
        AssertSuccessful(result);
        SafeCoreSourceMap map = result.SourceMap!;
        AssertEx.Equal(2, map.Documents.Count);
        AssertEx.False(map.Documents.Any(document => document.Path.EndsWith("unmentioned.rs", StringComparison.Ordinal)),
            "An undeclared invalid source file must not be read.");
        int expandedStart = result.SourceText.IndexOf("value() ->", StringComparison.Ordinal);
        SafeCoreSourceLocation location = map.MapSpan(new TextSpan(expandedStart, "value".Length));
        AssertEx.Equal(childPath, location.Document.Path);
        AssertEx.Equal(child.IndexOf("value", StringComparison.Ordinal), location.Span.Start);
        AssertEx.Equal("value", location.Document.Text.Substring(location.Span.Start, location.Span.Length));
        SafeCoreSyntaxResult parsed = SafeCoreSyntax.Parse(result.SourceText);
        AssertEx.True(parsed.IsSuccessful, "The expanded source must parse.");
        AssertEx.False(((SafeCoreModuleSyntax)parsed.Root!.Items[0]).IsExternal, "The declaration must have its loaded body.");
    });

    private static Task ResolvesNestedModulesAsync() => WithFixture(fixture =>
    {
        fixture.Write("main.rs", "mod flat; mod directory; mod inline { mod leaf; } mod r#type;");
        fixture.Write("flat.rs", "mod nested;");
        fixture.Write("flat/nested.rs", "pub fn from_flat() {}");
        fixture.Write("directory/mod.rs", "mod nested;");
        fixture.Write("directory/nested.rs", "pub fn from_directory() {}");
        fixture.Write("inline/leaf.rs", "pub fn from_inline() {}");
        fixture.Write("type.rs", "pub fn from_raw() {}");
        SafeCoreWorkspaceResult result = fixture.Load();
        AssertSuccessful(result);
        AssertEx.Equal(7, result.SourceMap!.Documents.Count);
        AssertEx.True(result.SourceText.Contains("from_flat", StringComparison.Ordinal), "flat.rs descendants resolve in flat/.");
        AssertEx.True(result.SourceText.Contains("from_directory", StringComparison.Ordinal), "directory/mod.rs descendants resolve in directory/.");
        AssertEx.True(result.SourceText.Contains("from_inline", StringComparison.Ordinal), "Inline module directories descend by module name.");
        AssertEx.True(result.SourceText.Contains("from_raw", StringComparison.Ordinal), "Raw module names use their identifier file name.");
    });

    private static Task DiagnosesMissingAndConflictingAsync() => WithFixture(fixture =>
    {
        string entry = fixture.Write("main.rs", "mod absent;");
        SafeCoreWorkspaceResult missing = fixture.Load();
        AssertFailure(missing, SafeCoreWorkspaceDiagnosticCodes.MissingModule);
        AssertEx.Equal(entry, missing.Diagnostics[0].SourcePath!);
        AssertEx.Equal(new TextSpan(0, 11), missing.Diagnostics[0].Span);
        fixture.Write("main.rs", "mod both;");
        fixture.Write("both.rs", "");
        fixture.Write("both/mod.rs", "");
        AssertFailure(fixture.Load(), SafeCoreWorkspaceDiagnosticCodes.AmbiguousModule);
    });

    private static Task RetainsSyntaxDiagnosticsAsync() => WithFixture(fixture =>
    {
        fixture.Write("main.rs", "mod broken; fn main() {}");
        const string source = "\n\npub fn broken(";
        string path = fixture.Write("broken.rs", source);
        SafeCoreSyntaxResult original = SafeCoreSyntax.Parse(source, path);
        SafeCoreWorkspaceResult result = fixture.Load();
        AssertEx.False(result.IsSuccessful, "Invalid child syntax must stop loading.");
        AssertEx.True(result.SourceMap is null && result.SourceText.Length == 0, "Failed loads must not return partial expanded source.");
        AssertEx.Equal(original.Diagnostics.Count, result.Diagnostics.Count);
        for (int index = 0; index < original.Diagnostics.Count && index < 128; index++)
        {
            AssertEx.Equal(original.Diagnostics[index].Code, result.Diagnostics[index].Code);
            AssertEx.Equal(original.Diagnostics[index].Span, result.Diagnostics[index].Span);
            AssertEx.Equal(path, result.Diagnostics[index].SourcePath!);
        }
    });

    private static Task PreservesEncodingAsync() => WithFixture(fixture =>
    {
        fixture.Write("main.rs", "mod utf8;");
        const string source = "pub fn café() {}";
        byte[] bytes = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(source)];
        string childPath = fixture.WriteBytes("utf8.rs", bytes);
        SafeCoreWorkspaceResult result = fixture.Load();
        AssertSuccessful(result);
        SafeCoreSourceDocument document = result.SourceMap!.Documents.Single(document => document.Path == childPath);
        AssertEx.Equal(source, document.Text);
        AssertEx.True(document.Bytes.Span.SequenceEqual(bytes), "Debug checksums must retain the original UTF-8 BOM.");
        int start = result.SourceText.IndexOf("café", StringComparison.Ordinal);
        AssertEx.Equal(source.IndexOf("café", StringComparison.Ordinal), result.SourceMap.MapSpan(new TextSpan(start, 4)).Span.Start);
        fixture.WriteBytes("utf8.rs", [0xc0, 0xaf]);
        SafeCoreWorkspaceResult invalid = fixture.Load();
        AssertFailure(invalid, SafeCoreWorkspaceDiagnosticCodes.InvalidSource);
        AssertEx.Equal(childPath, invalid.Diagnostics[0].SourcePath!);
    });

    private static Task PreservesPreamblesAsync() => WithFixture(fixture =>
    {
        fixture.Write("main.rs", "#!/usr/bin/env rust\nmod child; fn main() {}");
        const string source = "#!/usr/bin/env rust\n//! child docs\npub fn value() {} // no final newline";
        string childPath = fixture.Write("child.rs", source);
        SafeCoreWorkspaceResult result = fixture.Load();
        AssertSuccessful(result);
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(result.SourceText);
        AssertEx.True(syntax.IsSuccessful, "Child shebang and terminal line comment must not corrupt the combined module.");
        SafeCoreModuleSyntax module = (SafeCoreModuleSyntax)syntax.Root!.Items[0];
        AssertEx.Equal(1, module.InnerAttributes.Count);
        AssertEx.True(module.InnerAttributes[0].IsDocumentation, "Child inner documentation remains module documentation.");
        int start = result.SourceText.IndexOf("pub fn value", StringComparison.Ordinal);
        SafeCoreSourceLocation location = result.SourceMap!.MapSpan(new TextSpan(start, 3));
        AssertEx.Equal(childPath, location.Document.Path);
        AssertEx.Equal(source.IndexOf("pub fn value", StringComparison.Ordinal), location.Span.Start);
    });

    private static Task RejectsModuleAttributesAsync() => WithFixture(fixture =>
    {
        fixture.Write("main.rs", "#[path = \"elsewhere.rs\"] mod child;");
        AssertFailure(fixture.Load(), SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax);
        fixture.Write("main.rs", "#[cfg(disabled)] mod child;");
        AssertFailure(fixture.Load(), SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax);
        fixture.Write("main.rs", "#[cfg(disabled)] mod parent { mod child; }");
        AssertFailure(fixture.Load(), SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax);
        fixture.Write("main.rs", "mod child;");
        string childPath = fixture.Write("child.rs", "#![cfg(disabled)] mod missing;");
        SafeCoreWorkspaceResult inner = fixture.Load();
        AssertFailure(inner, SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax);
        AssertEx.Equal(childPath, inner.Diagnostics[0].SourcePath!);
        AssertEx.Equal(new TextSpan(0, 17), inner.Diagnostics[0].Span);
        fixture.Write("main.rs", "#![cfg(disabled)] mod absent;");
        AssertFailure(fixture.Load(), SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax);
        fixture.Write("main.rs", "mod child { #![cfg(disabled)] mod absent; }");
        AssertFailure(fixture.Load(), SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax);
        fixture.Write("main.rs", "/// Child documentation\nmod child;");
        fixture.Write("child.rs", "pub fn value() {}");
        AssertSuccessful(fixture.Load());
    });

    private static Task EnforcesBudgetsAsync() => WithFixture(fixture =>
    {
        fixture.Write("main.rs", "mod child;");
        fixture.Write("child.rs", "mod nested;");
        fixture.Write("child/nested.rs", "pub fn value() {}");
        AssertFailure(fixture.Load(new SafeCoreWorkspaceOptions { MaximumFiles = 1 }), SafeCoreWorkspaceDiagnosticCodes.LimitReached);
        AssertFailure(fixture.Load(new SafeCoreWorkspaceOptions { MaximumFileBytes = 1 }), SafeCoreWorkspaceDiagnosticCodes.LimitReached);
        AssertFailure(fixture.Load(new SafeCoreWorkspaceOptions { MaximumTotalBytes = 15 }), SafeCoreWorkspaceDiagnosticCodes.LimitReached);
        AssertFailure(fixture.Load(new SafeCoreWorkspaceOptions { MaximumExpandedLength = 30 }), SafeCoreWorkspaceDiagnosticCodes.LimitReached);
        AssertFailure(fixture.Load(new SafeCoreWorkspaceOptions { MaximumModuleDepth = 1 }), SafeCoreWorkspaceDiagnosticCodes.LimitReached);
        AssertFailure(fixture.Load(new SafeCoreWorkspaceOptions { MaximumOperations = 1 }), SafeCoreWorkspaceDiagnosticCodes.LimitReached);
        AssertFailure(fixture.Load(new SafeCoreWorkspaceOptions { Timeout = TimeSpan.FromTicks(1) }), SafeCoreWorkspaceDiagnosticCodes.LimitReached);
    });

    private static Task PropagatesCancellationAsync() => WithFixture(fixture =>
    {
        fixture.Write("main.rs", "fn main() {}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreWorkspace.Load(fixture.EntryPath, cancellationToken: cancellation.Token));
    });

    private static Task RejectsLinksAsync() => WithFixture(fixture =>
    {
        fixture.Write("main.rs", "mod link;");
        string target = fixture.Write("actual.rs", "pub fn actual() {}");
        string fileLink = Path.Combine(fixture.Root, "link.rs");
        try { File.CreateSymbolicLink(fileLink, target); }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) when (OperatingSystem.IsWindows()) { return; }
        try { AssertFailure(fixture.Load(), SafeCoreWorkspaceDiagnosticCodes.UnsafePath); }
        finally { File.Delete(fileLink); }

        fixture.Write("main.rs", "mod link { mod child; }");
        fixture.Write("actual/child.rs", "pub fn actual() {}");
        string directoryLink = Path.Combine(fixture.Root, "link");
        Directory.CreateSymbolicLink(directoryLink, Path.Combine(fixture.Root, "actual"));
        try { AssertFailure(fixture.Load(), SafeCoreWorkspaceDiagnosticCodes.UnsafePath); }
        finally { Directory.Delete(directoryLink); }
    });

    private static Task MapsBoundariesAsync() => WithFixture(fixture =>
    {
        const string entry = "mod child; fn after() {}";
        string entryPath = fixture.Write("main.rs", entry);
        fixture.Write("child.rs", "fn inside() {}");
        SafeCoreWorkspaceResult result = fixture.Load();
        AssertSuccessful(result);
        SafeCoreSourceMap map = result.SourceMap!;
        SafeCoreSourceLocation complete = map.MapSpan(new TextSpan(0, result.SourceText.Length));
        AssertEx.Equal(entryPath, complete.Document.Path);
        AssertEx.Equal("mod child", complete.Document.Text.Substring(complete.Span.Start, complete.Span.Length));
        SafeCoreSourceLocation delimiter = map.MapSpan(new TextSpan(entry.IndexOf(';', StringComparison.Ordinal), 1));
        AssertEx.Equal(";", delimiter.Document.Text.Substring(delimiter.Span.Start, delimiter.Span.Length));
        int after = result.SourceText.IndexOf("after", StringComparison.Ordinal);
        AssertEx.Equal(entry.IndexOf("after", StringComparison.Ordinal), map.MapSpan(new TextSpan(after, 5)).Span.Start);
        SafeCoreSourceLocation eof = map.MapSpan(new TextSpan(result.SourceText.Length, 0));
        AssertEx.Equal(entry.Length, eof.Span.Start);
        AssertEx.Equal(0, eof.Span.Length);
        AssertEx.Throws<ArgumentOutOfRangeException>(() => map.MapSpan(new TextSpan(-1, 1)));
        fixture.Write("main.rs", "");
        SafeCoreWorkspaceResult empty = fixture.Load();
        AssertSuccessful(empty);
        AssertEx.Equal(new TextSpan(0, 0), empty.SourceMap!.MapSpan(new TextSpan(0, 0)).Span);
    });

    private static Task RejectsInvalidMapsAsync()
    {
        var document = new SafeCoreSourceDocument("source.rs", "abc", Encoding.UTF8.GetBytes("abc"));
        var other = new SafeCoreSourceDocument("other.rs", "abc", Encoding.UTF8.GetBytes("abc"));
        AssertEx.Throws<ArgumentException>(() => _ = new SafeCoreSourceMap([document], [], 1));
        AssertEx.Throws<ArgumentException>(() => _ = new SafeCoreSourceMap([document],
            [new SafeCoreSourceMapSegment(new TextSpan(1, 1), document, new TextSpan(0, 1))], 2));
        AssertEx.Throws<ArgumentException>(() => _ = new SafeCoreSourceMap([document],
            [new SafeCoreSourceMapSegment(new TextSpan(0, 2), document, new TextSpan(2, 2))], 2));
        AssertEx.Throws<ArgumentException>(() => _ = new SafeCoreSourceMap([document],
            [new SafeCoreSourceMapSegment(new TextSpan(0, 2), document, new TextSpan(0, 1))], 2));
        AssertEx.Throws<ArgumentException>(() => _ = new SafeCoreSourceMap([document],
            [new SafeCoreSourceMapSegment(new TextSpan(0, 1), other, new TextSpan(0, 1))], 1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => _ = new SafeCoreSourceMap([document], [], 0, cancellation.Token));
        return Task.CompletedTask;
    }

    private static void AssertSuccessful(SafeCoreWorkspaceResult result) =>
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));

    private static void AssertFailure(SafeCoreWorkspaceResult result, string code)
    {
        AssertEx.False(result.IsSuccessful, "The invalid workspace must fail.");
        AssertEx.True(result.SourceMap is null && result.SourceText.Length == 0, "Failure must not expose a partial source map or expansion.");
        AssertEx.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == code),
            "Expected " + code + ": " + string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
    }

    private static Task WithFixture(Action<WorkspaceFixture> action)
    {
        using var fixture = new WorkspaceFixture();
        action(fixture);
        return Task.CompletedTask;
    }

    private sealed class WorkspaceFixture : IDisposable
    {
        private readonly string _parent = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests"));
        private readonly string _name = "ws-" + Guid.NewGuid().ToString("N");

        internal WorkspaceFixture()
        {
            Root = Path.Combine(_parent, _name);
            if (Directory.Exists(Root)) throw new InvalidOperationException("The test workspace must be unique.");
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }
        internal string EntryPath => Path.Combine(Root, "main.rs");

        internal string Write(string relativePath, string source) => WriteBytes(relativePath, Encoding.UTF8.GetBytes(source));

        internal string WriteBytes(string relativePath, byte[] bytes)
        {
            string path = Path.GetFullPath(Path.Combine(Root, relativePath));
            AssertEx.True(path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal), "A fixture path must stay in its owned workspace.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        internal SafeCoreWorkspaceResult Load(SafeCoreWorkspaceOptions? options = null) => SafeCoreWorkspace.Load(EntryPath, options);

        public void Dispose()
        {
            string fullPath = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(fullPath) != _parent || Path.GetFileName(fullPath) != _name || !_name.StartsWith("ws-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove a directory not owned by this workspace test.");
            }
            Directory.Delete(fullPath, recursive: true);
        }
    }
}
