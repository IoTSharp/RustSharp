using RustSharp.Compiler;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreModuleCompilationTests
{
    private const CompilationProfile Profile = CompilationProfile.SafeCorePrimitives;

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("HIR preserves both targets of a simple namespace import", LowersNamespaceImportsAsync),
        new("module imports lower to bound HIR without widening visibility", LowersImportsAsync),
        new("documentation remains attached to its HIR owner", LowersDocumentationAsync),
        new("documented file modules execute through transitive globs", RunsDocumentedGlobsAsync),
        new("multi-file modules execute through grouped imports and restricted visibility", RunsModulesAsync),
        new("module diagnostics retain originating file and source span", MapsDiagnosticsAsync),
        new("module compilation protects every loaded source from output collisions", ProtectsSourcesAsync),
        new("in-memory compilation keeps external modules explicit and cancellable", PreservesMemoryBoundaryAsync),
    ];

    private static async Task LowersNamespaceImportsAsync()
    {
        const string source = "mod api { pub mod Name { pub fn value() -> i32 { 42 } } " +
            "pub fn Name() -> i32 { 7 } } use api::Name; " +
            "fn main() { println!(\"{}\", Name()); println!(\"{}\", Name::value()); }";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "namespace-hir.rs", null, deadline.Token);
        SafeCoreHirResult hir = SafeCoreHirLowering.Lower(syntax, new() { CancellationToken = deadline.Token });
        AssertEx.True(hir.IsSuccessful, string.Join("; ", hir.Diagnostics));
        SafeCoreHirNode group = hir.Nodes.Single(static node => node.Kind == SafeCoreHirNodeKind.ImportGroup);
        AssertEx.Equal(2, group.ChildIds.Count);
        AssertEx.True(group.ChildIds.Select(id => hir.GetNode(id).DeclaredSymbol!.Namespace)
            .ToHashSet().SetEquals([SafeCoreSymbolNamespace.Type, SafeCoreSymbolNamespace.Value]),
            "A simple import with two targets retains both declarations in HIR.");
        SafeCoreHirResult repeated = SafeCoreHirLowering.Lower(syntax, new() { CancellationToken = deadline.Token });
        AssertEx.True(repeated.IsSuccessful, string.Join("; ", repeated.Diagnostics));
        AssertEx.Equal(string.Join(";", hir.Nodes.Select(static node => $"{node.Id}:{node.Kind}:{node.DeclaredSymbol?.Namespace}")),
            string.Join(";", repeated.Nodes.Select(static node => $"{node.Id}:{node.Kind}:{node.DeclaredSymbol?.Namespace}")));
        CompilationResult check = CompilerDriver.Check(source, "namespace-hir.rs", Profile, deadline.Token);
        AssertEx.True(check.Success, string.Join("; ", check.Diagnostics));
        string directory = NewDirectory();
        try
        {
            string output = Path.Combine(directory, "namespace.dll");
            CompilationResult compile = CompilerDriver.Compile(source, "namespace-hir.rs", output,
                profile: Profile, cancellationToken: deadline.Token);
            AssertEx.True(compile.Success, string.Join("; ", compile.Diagnostics));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [output], directory, TimeSpan.FromSeconds(15)), deadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded && !run.ProcessTreeCleanupIncomplete, run.StandardError);
            AssertEx.Equal("7\n42\n", run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally { DeleteDirectory(directory); }
    }

    private static Task LowersDocumentationAsync()
    {
        const string source = """
            //! crate documentation
            /// module documentation
            mod api {
                //! inner module documentation
                /// function documentation
                pub fn answer() -> i32 {
                    //! body documentation
                    42
                }
            }
            /// struct documentation
            struct Data { /// field documentation
                value: i32 }
            /// enum documentation
            enum Choice { /// variant documentation
                Some(/** tuple field documentation */ i32), None }
            /// import documentation
            use api::answer;
            fn main() { answer(); }
            """;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "documentation-hir.rs", null, deadline.Token);
        SafeCoreHirResult hir = SafeCoreHirLowering.Lower(syntax,
            new() { CancellationToken = deadline.Token });
        AssertEx.True(hir.IsSuccessful, string.Join("; ", hir.Diagnostics));
        SafeCoreHirNode[] attributes = hir.Nodes.Where(static node => node.Kind == SafeCoreHirNodeKind.Attribute).ToArray();
        AssertEx.Equal(11, attributes.Length, "Every documentation comment must survive lowering exactly once.");
        AssertEx.True(attributes.All(attribute => attribute.Name == "doc" &&
            attribute.Modifiers.HasFlag(SafeCoreHirNodeModifiers.DocumentationAttribute) &&
            syntax.GetText(attribute.Span).Contains("documentation", StringComparison.Ordinal)),
            "HIR documentation carries both its classification and exact source span.");
        AssertEx.Equal(3, attributes.Count(static attribute =>
            attribute.Modifiers.HasFlag(SafeCoreHirNodeModifiers.InnerAttribute)));
        SafeCoreHirNode module = hir.Nodes.Single(static node => node.Kind == SafeCoreHirNodeKind.Module);
        AssertEx.Equal(2, module.ChildIds.Count(id => hir.GetNode(id).Kind == SafeCoreHirNodeKind.Attribute));
        SafeCoreHirNode body = hir.Nodes.First(static node => node.Kind == SafeCoreHirNodeKind.Block);
        AssertEx.Equal(SafeCoreHirNodeKind.Attribute, hir.GetNode(body.ChildIds[0]).Kind);
        AssertEx.True(hir.Nodes.Where(static node => node.Kind is SafeCoreHirNodeKind.Field or SafeCoreHirNodeKind.EnumVariant)
            .Count(node => node.ChildIds.Any(id => hir.GetNode(id).Kind == SafeCoreHirNodeKind.Attribute)) == 3,
            "Named fields, tuple fields and enum variants retain their own documentation.");
        CompilationResult noStd = CompilerDriver.Check("#![no_std] fn main() {}", "no-std.rs", Profile, deadline.Token);
        AssertEx.True(!noStd.Success && noStd.Diagnostics.Any(static diagnostic => diagnostic.Code == "RST1001"),
            "Retaining legacy no_std HIR must not claim executable no_std support.");
        return Task.CompletedTask;
    }

    private static async Task RunsDocumentedGlobsAsync()
    {
        string directory = NewDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            Write(directory, "main.rs", """
                //! root documentation
                /// API documentation
                mod api;
                mod relay { pub use crate::api::*; }
                /// glob documentation
                use relay::*;
                use api::*;
                /// single import documentation
                use api::answer as chosen;
                /// entry documentation
                fn main() {
                    //! entry body documentation
                    println!("{}", answer());
                    println!("{}", chosen() == 42);
                }
                """);
            Write(directory, "api.rs", """
                //! external module documentation
                /// value documentation
                pub(crate) fn answer() -> i32 {
                    //! function body documentation
                    if true { //! nested block documentation
                        return 42;
                    } else { 0 }
                }
                /// empty body documentation
                pub fn empty() { //! empty inner documentation
                }
                """);
            string entry = Path.Combine(directory, "main.rs");
            SafeCoreWorkspaceResult workspace = SafeCoreWorkspace.Load(entry, cancellationToken: deadline.Token);
            AssertEx.True(workspace.IsSuccessful, string.Join("; ", workspace.Diagnostics));
            SafeCoreHirResult hir = SafeCoreHirLowering.Lower(SafeCoreSyntax.Parse(workspace.SourceText, entry),
                new() { CancellationToken = deadline.Token });
            AssertEx.True(hir.IsSuccessful, string.Join("; ", hir.Diagnostics));
            AssertEx.True(hir.Nodes.Where(static node => node.Kind == SafeCoreHirNodeKind.Import)
                .All(static node => node.DeclaredSymbol?.ResolvedImportTargetQualifiedName is not null),
                "Every glob expansion must reach HIR with a bound canonical target.");
            string output = Path.Combine(directory, "program.dll");
            CompilationResult compile = CompilerDriver.CompileFile(entry, output, profile: Profile,
                cancellationToken: deadline.Token);
            AssertEx.True(compile.Success, string.Join("; ", compile.Diagnostics));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [output], directory, TimeSpan.FromSeconds(15)), deadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded && !run.ProcessTreeCleanupIncomplete, run.StandardError);
            AssertEx.Equal("42\ntrue\n", run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally { DeleteDirectory(directory); }
    }

    private static Task LowersImportsAsync()
    {
        const string source = """
            mod api { pub(crate) fn first() -> i32 { 1 } pub fn second() -> i32 { 2 } }
            use api::{self as a, first as one, second, first as _};
            use api::{};
            fn main() { println!("{}", one() + second() + a::first()); }
            """;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "imports-hir.rs", null, deadline.Token);
        SafeCoreHirResult hir = SafeCoreHirLowering.Lower(syntax);
        AssertEx.True(hir.IsSuccessful, string.Join("; ", hir.Diagnostics));
        SafeCoreHirNode[] imports = hir.Nodes.Where(static node => node.Kind == SafeCoreHirNodeKind.Import).ToArray();
        AssertEx.Equal(4, imports.Length);
        AssertEx.True(imports.All(static node => node.DeclaredSymbol?.ResolvedImportTargetQualifiedName is not null),
            "Each grouped import leaf must carry its canonical target into HIR.");
        SafeCoreSymbol first = hir.NameResolution!.Symbols.Single(static symbol => symbol.QualifiedName == "crate::api::first");
        AssertEx.False(first.IsPublic, "Crate visibility must not become unrestricted public visibility.");
        AssertEx.Equal("crate", first.VisibilityScopePath!);
        AssertEx.True(imports.Single(static node => node.Name == "_").DeclaredSymbol!.IsAnonymousImport,
            "Anonymous imports retain their binding without introducing an underscore name.");
        AssertEx.True(CompilerDriver.Check(source, "imports-hir.rs", Profile, deadline.Token).Success,
            "The primitive compiler must consume grouped import HIR.");
        SafeCoreHirResult limited = SafeCoreHirLowering.Lower(syntax,
            new SafeCoreHirLoweringOptions { Timeout = TimeSpan.FromTicks(1) });
        AssertEx.True(limited.IsTruncated && !limited.IsSuccessful, "Group lowering must respect its own deadline.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreHirLowering.Lower(syntax,
            new SafeCoreHirLoweringOptions { CancellationToken = cancelled.Token }));
        return Task.CompletedTask;
    }

    private static async Task RunsModulesAsync()
    {
        string directory = NewDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            Write(directory, "main.rs", """
                mod calc;
                mod nested { pub mod value; }
                use calc::{self as math, answer as compute};
                use nested::{value::extra};
                fn main() { println!("{}", compute() + extra()); println!("{}", math::answer()); }
                """);
            Write(directory, "calc.rs", "mod base; pub(crate) fn answer() -> i32 { base::number() }\n");
            Write(directory, "calc/base/mod.rs", "pub(super) fn number() -> i32 { 40 }\n");
            Write(directory, "nested/value.rs", "pub(in crate) fn extra() -> i32 { 2 }\n");
            string entry = Path.Combine(directory, "main.rs");
            CompilationResult check = CompilerDriver.CheckFile(entry, Profile, deadline.Token);
            AssertEx.True(check.Success, string.Join("; ", check.Diagnostics));
            string output = Path.Combine(directory, "program.dll");
            CompilationResult compilation = CompilerDriver.CompileFile(entry, output, profile: Profile,
                cancellationToken: deadline.Token);
            AssertEx.True(compilation.Success, string.Join("; ", compilation.Diagnostics));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [output], directory, TimeSpan.FromSeconds(15)), deadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded, run.StandardError);
            AssertEx.False(run.ProcessTreeCleanupIncomplete, "The runtime process tree must be reclaimed.");
            AssertEx.Equal("42\n40\n", run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally { DeleteDirectory(directory); }
    }

    private static Task MapsDiagnosticsAsync()
    {
        string directory = NewDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            Write(directory, "main.rs", "mod child; fn main() { child::value(); }\n");
            (string Source, string Code, string Text)[] cases =
            [
                ("pub fn value() { let x: bool = 1; }", "RST1002", "let x: bool = 1;"),
                ("pub fn value() { missing(); }", "RSN1003", "missing"),
                ("pub fn value() { loop {} }", "RSN1007", "loop {}"),
                ("#[cfg(any())] pub fn value() {}", "RSN1007", "#[cfg(any())]"),
            ];
            foreach (var item in cases)
            {
                deadline.Token.ThrowIfCancellationRequested();
                Write(directory, "child.rs", item.Source);
                CompilationResult check = CompilerDriver.CheckFile(Path.Combine(directory, "main.rs"), Profile, deadline.Token);
                AssertEx.False(check.Success, "Child source errors must fail a file check.");
                Diagnostic diagnostic = check.Diagnostics.First(diagnostic => diagnostic.Code == item.Code);
                AssertEx.Equal(Path.Combine(directory, "child.rs"), diagnostic.SourcePath!);
                AssertEx.Equal(item.Text, item.Source.Substring(diagnostic.Span.Start, diagnostic.Span.Length));
                CompilationResult compile = CompilerDriver.CompileFile(Path.Combine(directory, "main.rs"),
                    Path.Combine(directory, "out", "program.dll"), profile: Profile, cancellationToken: deadline.Token);
                AssertEx.False(compile.Success, "Child source errors must prevent compilation.");
                AssertEx.True(compile.Diagnostics.Contains(diagnostic), "Check and compile must report the same original file and span.");
                AssertEx.False(Directory.Exists(Path.Combine(directory, "out")), "Rejected modules must not create output directories.");
            }
        }
        finally { DeleteDirectory(directory); }
        return Task.CompletedTask;
    }

    private static Task ProtectsSourcesAsync()
    {
        string directory = NewDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            const string child = "pub fn value() -> i32 { 1 }";
            Write(directory, "main.rs", "mod child; fn main() { child::value(); }");
            Write(directory, "child.rs", child);
            CompilationResult compile = CompilerDriver.CompileFile(Path.Combine(directory, "main.rs"),
                Path.Combine(directory, "child.rs"), profile: Profile, cancellationToken: deadline.Token);
            AssertEx.False(compile.Success, "An output must never overwrite an imported module.");
            AssertEx.True(compile.Diagnostics.Any(static diagnostic => diagnostic.Code == "RSC0006"), "A source collision must diagnose.");
            AssertEx.Equal(child, File.ReadAllText(Path.Combine(directory, "child.rs")));
            AssertEx.Equal(2, Directory.GetFiles(directory).Length, "Collision detection must precede artifact transactions.");
        }
        finally { DeleteDirectory(directory); }
        return Task.CompletedTask;
    }

    private static Task PreservesMemoryBoundaryAsync()
    {
        CompilationResult result = CompilerDriver.Check("mod external; fn main() {}", "in-memory.rs", Profile);
        AssertEx.False(result.Success, "In-memory checks do not implicitly read files.");
        AssertEx.True(result.Diagnostics.Any(static diagnostic => diagnostic.Code == "RSN1007"), "External modules require the file entry point.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool observed = false;
        try { CompilerDriver.CheckFile("cancelled.rs", Profile, cancelled.Token); }
        catch (OperationCanceledException) { observed = true; }
        AssertEx.True(observed, "Cancellation must take precedence over file loading.");
        return Task.CompletedTask;
    }

    private static string NewDirectory()
    {
        string directory = Path.GetFullPath(Path.Combine("artifacts", "tests", "module-compile-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Write(string directory, string relativePath, string source)
    {
        string path = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source);
    }

    private static void DeleteDirectory(string directory)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "tests")) + Path.DirectorySeparatorChar;
        AssertEx.True(Path.GetFullPath(directory).StartsWith(root, StringComparison.Ordinal) &&
            Path.GetFileName(directory).StartsWith("module-compile-", StringComparison.Ordinal), "Cleanup must target the test-owned directory.");
        Directory.Delete(directory, recursive: true);
    }
}
