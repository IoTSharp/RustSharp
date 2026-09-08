using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreModuleResolutionTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("explicit imports bind independent type and value targets", DualNamespaceImportsAsync),
        new("namespace import branches preserve shadowing visibility and cycles", NamespaceImportEdgesAsync),
        new("module resolution expands nested grouped imports and self aliases", GroupedImportsAsync),
        new("module resolution expands module enum and crate-root glob imports", GlobImportsAsync),
        new("module resolution gives explicit bindings namespace precedence over globs", GlobShadowingAsync),
        new("module resolution defers glob ambiguity until a name is used", GlobAmbiguityAsync),
        new("module resolution computes glob chains and cycles to a fixed point", GlobFixedPointAsync),
        new("module resolution intersects glob reexport visibility", GlobVisibilityAsync),
        new("module glob resolution obeys symbols operations deadlines and cancellation", GlobLimitsAsync),
        new("module resolution validates anonymous imports without binding names", AnonymousImportsAsync),
        new("module resolution respects restricted visibility across module paths", RestrictedVisibilityAsync),
        new("module resolution rejects access outside visibility subtrees", PrivateAccessAsync),
        new("module resolution rejects invalid visibility ancestors", InvalidVisibilityAsync),
        new("module resolution prevents imports from widening visibility", ReexportVisibilityAsync),
        new("module import expansion obeys operations depth deadlines and cancellation", LimitsAsync),
    ];

    private static Task DualNamespaceImportsAsync()
    {
        const string source = "mod api { pub type Name = i32; pub fn Name() {} } " +
            "use crate::api::Name; use self::Name as Alias; fn main() { let n: Name = 1; Alias(); }";
        SafeCoreNameResolutionResult result = Resolve(source);
        AssertEx.True(result.IsSuccessful, Format(result));
        SafeCoreSymbol[] bindings = result.Symbols.Where(static symbol => symbol.IsImport).ToArray();
        AssertEx.Equal(4, bindings.Length);
        AssertEx.Equal(2, bindings.Count(static symbol => symbol.Namespace == SafeCoreSymbolNamespace.Type));
        AssertEx.Equal(2, bindings.Count(static symbol => symbol.Namespace == SafeCoreSymbolNamespace.Value));
        AssertEx.True(bindings.All(static symbol => symbol.ResolvedImportTargetQualifiedName == "crate::api::Name"),
            "Aliases retain the canonical target in each namespace.");
        AssertEx.Equal(SafeCoreSymbolNamespace.Type, result.FindResolution("Name")!.Symbol!.Namespace);
        AssertEx.Equal(SafeCoreSymbolNamespace.Value, result.FindResolution("Alias")!.Symbol!.Namespace);

        SafeCoreNameResolutionResult grouped = Resolve("mod api { pub type Name = i32; pub fn Name() {} } " +
            "use api::{Name as Renamed}; fn main() { let n: Renamed = 1; Renamed(); }");
        AssertEx.True(grouped.IsSuccessful, Format(grouped));
        AssertEx.Equal(2, grouped.Symbols.Count(static symbol => symbol.IsImport));
        SafeCoreNameResolutionResult constructor = Resolve("mod api { pub struct Unit; } " +
            "use api::Unit; fn main() { let x: Unit = Unit; }");
        AssertEx.True(constructor.IsSuccessful, Format(constructor));
        AssertEx.Equal(SafeCoreSymbolNamespace.Both, constructor.Symbols.Single(static symbol => symbol.IsImport).Namespace);
        return Task.CompletedTask;
    }

    private static Task NamespaceImportEdgesAsync()
    {
        SafeCoreNameResolutionResult shadow = Resolve("mod api { pub type Name = i32; pub fn Name() {} } " +
            "mod values { pub fn Name() {} } use api::*; use values::Name; " +
            "fn main() { let n: Name = 1; Name(); }");
        AssertEx.True(shadow.IsSuccessful, Format(shadow));
        AssertEx.Equal("crate::api::Name", shadow.Resolutions.Single(static resolution =>
            resolution.Path == "Name" && resolution.Symbol?.Namespace == SafeCoreSymbolNamespace.Type).Symbol!.ResolvedImportTargetQualifiedName!);
        AssertEx.Equal("crate::values::Name", shadow.Resolutions.Single(static resolution =>
            resolution.Path == "Name" && resolution.Symbol?.Namespace == SafeCoreSymbolNamespace.Value).Symbol!.ResolvedImportTargetQualifiedName!);

        SafeCoreNameResolutionResult transitive = Resolve("use relay::Name; " +
            "mod relay { pub use api::*; use crate::api; } " +
            "mod api { pub type Name = i32; pub fn Name() {} } fn main() { let n: Name = 1; Name(); }");
        AssertEx.True(transitive.IsSuccessful, Format(transitive));
        SafeCoreNameResolutionResult privateCounterpart = Resolve("mod api { pub type Name = i32; fn Name() {} } " +
            "use api::Name; fn main() { let n: Name = 1; }");
        AssertEx.True(privateCounterpart.IsSuccessful, Format(privateCounterpart));
        AssertEx.Equal(SafeCoreSymbolNamespace.Type, privateCounterpart.Symbols.Single(static symbol => symbol.IsImport).Namespace);
        SafeCoreNameResolutionResult sharedPrefix = Resolve("mod api { pub fn api() {} } use crate::api::api; fn main() { api(); }");
        AssertEx.True(sharedPrefix.IsSuccessful, Format(sharedPrefix));
        SafeCoreNameResolutionResult mixedVisibility = Resolve("mod api { pub type Name = i32; pub(crate) fn Name() {} } pub use api::Name;");
        AssertEx.True(mixedVisibility.IsSuccessful, Format(mixedVisibility));
        AssertEx.True(mixedVisibility.Symbols.Single(static symbol => symbol.IsImport && symbol.Namespace == SafeCoreSymbolNamespace.Type)
            .VisibilityScopePath is null, "The public type is re-exported publicly.");
        AssertEx.Equal("crate", mixedVisibility.Symbols.Single(static symbol => symbol.IsImport && symbol.Namespace == SafeCoreSymbolNamespace.Value)
            .VisibilityScopePath!, "The value counterpart retains its narrower visibility.");
        AssertCode(Resolve("mod api { pub type Name = i32; pub fn Name() {} } use api::Name; fn Name() {}"),
            SafeCoreNameResolutionDiagnosticCodes.DuplicateSymbol);
        SafeCoreNameResolutionResult separateDependencies = Resolve("mod api { pub type Name = i32; pub fn Value() {} } " +
            "use api::Name as A; use api::Value as B; use self::B as A; use self::A as B; " +
            "fn main() { let n: A = 1; A(); }");
        AssertCode(separateDependencies, SafeCoreNameResolutionDiagnosticCodes.DuplicateSymbol);
        return Task.CompletedTask;
    }

    private static Task GroupedImportsAsync()
    {
        const string source = "mod api { pub fn f() {} pub mod nested { pub const VALUE: i32 = 7; } } " +
            "use crate::api::{self as a, f as run, nested::{VALUE as number}}; use crate::api::{}; " +
            "fn main() { run(); a::f(); number; }";
        SafeCoreNameResolutionResult result = Resolve(source);
        AssertEx.True(result.IsSuccessful, Format(result));
        SafeCoreSymbol alias = result.Symbols.Single(static symbol => symbol.QualifiedName == "crate::a");
        AssertEx.Equal("crate::api", alias.ResolvedImportTargetQualifiedName!);
        AssertEx.Equal(SafeCoreSymbolNamespace.Type, alias.Namespace);
        AssertEx.Equal("crate::api::nested::VALUE", result.Symbols.Single(static symbol => symbol.Name == "number").ResolvedImportTargetQualifiedName!);
        AssertEx.Equal(3, result.Symbols.Count(static symbol => symbol.IsImport));
        AssertEx.True(result.Symbols.Where(static symbol => symbol.IsImport).All(symbol => source.Substring(symbol.Span.Start, symbol.Span.Length).Contains("as", StringComparison.Ordinal)),
            "Grouped imports retain separate leaf spans.");
        AssertCode(Resolve("fn f() {} use crate::f::{self as local};"), SafeCoreNameResolutionDiagnosticCodes.UnresolvedName);
        AssertCode(Resolve("use crate::missing::{};"), SafeCoreNameResolutionDiagnosticCodes.UnresolvedName);
        AssertCode(Resolve("mod api { pub struct S; } use crate::api::S::{self as T};"), SafeCoreNameResolutionDiagnosticCodes.InvalidPath);
        AssertCode(Resolve("struct S; use crate::S::{};"), SafeCoreNameResolutionDiagnosticCodes.InvalidPath);
        return Task.CompletedTask;
    }

    private static Task GlobImportsAsync()
    {
        const string source = "pub mod api { pub fn f() {} pub const VALUE: i32 = 7; fn hidden() {} } " +
            "use crate::api::*; fn main() { f(); VALUE; }";
        SafeCoreNameResolutionResult result = Resolve(source);
        AssertEx.True(result.IsSuccessful, Format(result));
        SafeCoreSymbol function = result.Symbols.Single(static symbol => symbol.Name == "f" && symbol.IsImport);
        SafeCoreSymbol value = result.Symbols.Single(static symbol => symbol.Name == "VALUE" && symbol.IsImport);
        AssertEx.Equal("crate::api::f", function.ResolvedImportTargetQualifiedName!);
        AssertEx.Equal("crate::api::VALUE", value.ResolvedImportTargetQualifiedName!);
        AssertEx.False(result.Symbols.Any(static symbol => symbol.Name == "hidden" && symbol.IsImport),
            "A glob import must skip inaccessible private members.");

        SafeCoreNameResolutionResult enumResult = Resolve(
            "pub enum E { A, B } use crate::E::*; fn main() { A; B; }");
        AssertEx.True(enumResult.IsSuccessful, Format(enumResult));
        AssertEx.Equal(2, enumResult.Symbols.Count(static symbol => symbol.IsImport));
        SafeCoreNameResolutionResult roots = Resolve("fn f() {} mod child { use crate::*; fn g() { f(); } }");
        AssertEx.True(roots.IsSuccessful, Format(roots));
        SafeCoreNameResolutionResult grouped = Resolve("mod api { pub fn f() {} pub mod nested { pub fn g() {} } } use crate::api::{*, nested::*}; fn main() { f(); g(); }");
        AssertEx.True(grouped.IsSuccessful, Format(grouped));
        AssertCode(Resolve("mod api {} use ::crate::api::*;"), SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax);
        AssertCode(Resolve("struct S; use crate::S::*;"), SafeCoreNameResolutionDiagnosticCodes.InvalidPath);
        AssertCode(Resolve("use crate::*;"), SafeCoreNameResolutionDiagnosticCodes.InvalidPath);
        AssertCode(Resolve("mod m { use self::*; }"), SafeCoreNameResolutionDiagnosticCodes.InvalidPath);
        return Task.CompletedTask;
    }

    private static Task GlobShadowingAsync()
    {
        const string source = "mod api { pub fn f() {} pub fn Name() {} pub const A: i32 = 1; } " +
            "use crate::api::*; fn f() {} type Name = i32; const A: i32 = 2; " +
            "fn main() { f(); Name(); A; }";
        SafeCoreNameResolutionResult result = Resolve(source);
        AssertEx.True(result.IsSuccessful, Format(result));
        AssertEx.Equal("crate::f", result.FindResolution("f")!.Symbol!.QualifiedName);
        AssertEx.Equal("crate::api::Name", result.FindResolution("Name")!.Symbol!.ResolvedImportTargetQualifiedName!);
        AssertEx.Equal("crate::A", result.FindResolution("A")!.Symbol!.QualifiedName);
        SafeCoreNameResolutionResult explicitImport = Resolve("mod a { pub fn f() {} } mod b { pub fn f() {} } use crate::a::*; use crate::b::f; fn main() { f(); }");
        AssertEx.True(explicitImport.IsSuccessful, Format(explicitImport));
        AssertEx.Equal("crate::b::f", explicitImport.FindResolution("f")!.Symbol!.ResolvedImportTargetQualifiedName!);
        SafeCoreNameResolutionResult unit = Resolve("mod api { pub struct S; } use crate::api::*; use self::S as T; fn main() { S; T; }");
        AssertEx.True(unit.IsSuccessful, Format(unit));
        AssertEx.Equal(SafeCoreSymbolNamespace.Both, unit.Symbols.Single(static symbol => symbol.Name == "T").Namespace);
        SafeCoreNameResolutionResult prefixes = Resolve("mod api { pub fn f() {} } mod values { pub fn api() {} } use values::*; fn main() { api(); api::f(); }");
        AssertEx.True(prefixes.IsSuccessful, Format(prefixes));
        AssertEx.Equal("crate::values::api", prefixes.FindResolution("api")!.Symbol!.ResolvedImportTargetQualifiedName!);
        AssertEx.Equal("crate::api::f", prefixes.FindResolution("api::f")!.Symbol!.QualifiedName);
        return Task.CompletedTask;
    }

    private static Task GlobAmbiguityAsync()
    {
        const string prefix = "mod a { pub fn f() {} } mod b { pub fn f() {} } use crate::a::*; use crate::b::*; ";
        AssertEx.True(Resolve(prefix).IsSuccessful, "Unused conflicting globs must remain legal.");
        AssertCode(Resolve(prefix + "fn main() { f(); }"), SafeCoreNameResolutionDiagnosticCodes.AmbiguousName);
        SafeCoreNameResolutionResult repeated = Resolve("mod a { pub fn f() {} } use crate::a::*; use crate::a::*; fn main() { f(); }");
        AssertEx.True(repeated.IsSuccessful, Format(repeated));
        SafeCoreNameResolutionResult diamond = Resolve("mod a { pub fn f() {} } mod b { pub use crate::a::*; } mod c { pub use crate::a::*; } use crate::b::*; use crate::c::*; fn main() { f(); }");
        AssertEx.True(diamond.IsSuccessful, Format(diamond));
        return Task.CompletedTask;
    }

    private static Task GlobFixedPointAsync()
    {
        SafeCoreNameResolutionResult reverse = Resolve("use crate::first::*; mod first { pub use crate::second::*; } mod second { pub use crate::third::*; } mod third { pub fn f() {} } fn main() { f(); }");
        AssertEx.True(reverse.IsSuccessful, Format(reverse));
        AssertEx.Equal("crate::third::f", reverse.FindResolution("f")!.Symbol!.ResolvedImportTargetQualifiedName!);
        SafeCoreNameResolutionResult empty = Resolve("mod a { pub use crate::b::*; } mod b { pub use crate::a::*; } use crate::a::*;");
        AssertEx.True(empty.IsSuccessful, Format(empty));
        SafeCoreNameResolutionResult seeded = Resolve("mod a { pub use crate::b::*; } mod b { pub use crate::a::*; pub fn f() {} } use crate::a::*; fn main() { f(); }");
        AssertEx.True(seeded.IsSuccessful, Format(seeded));
        SafeCoreNameResolutionResult explicitShadow = Resolve("use crate::relay::*; mod relay { pub use crate::source::*; pub use crate::other::f; } mod source { pub fn f() {} } mod other { pub fn f() {} } fn main() { f(); }");
        AssertEx.True(explicitShadow.IsSuccessful, Format(explicitShadow));
        AssertEx.Equal("crate::other::f", explicitShadow.FindResolution("f")!.Symbol!.ResolvedImportTargetQualifiedName!);
        SafeCoreNameResolutionResult lateAlias = Resolve("use crate::relay::*; mod relay { pub use crate::api::*; pub use self::f as g; } mod api { pub fn f() {} } fn main() { g(); }");
        AssertEx.True(lateAlias.IsSuccessful, Format(lateAlias));
        AssertEx.Equal("crate::api::f", lateAlias.FindResolution("g")!.Symbol!.ResolvedImportTargetQualifiedName!);
        SafeCoreNameResolutionResult groupPrefix = Resolve("mod api { pub mod nested { pub fn f() {} } } use crate::api::*; use nested::{f}; fn main() { f(); }");
        AssertEx.True(groupPrefix.IsSuccessful, Format(groupPrefix));
        SafeCoreNameResolutionResult prefixShadow = Resolve("mod api { pub mod api { pub fn f() {} } pub fn g() {} } use api::*; fn main() { g(); api::g(); }");
        AssertEx.True(prefixShadow.IsSuccessful, Format(prefixShadow));
        return Task.CompletedTask;
    }

    private static Task GlobVisibilityAsync()
    {
        SafeCoreNameResolutionResult result = Resolve("mod api { pub(crate) fn f() {} pub fn g() {} } pub use crate::api::*; fn main() { f(); g(); }");
        AssertEx.True(result.IsSuccessful, Format(result));
        AssertEx.Equal("crate", result.Symbols.Single(static symbol => symbol.Name == "f" && symbol.IsImport).VisibilityScopePath!);
        AssertEx.True(result.Symbols.Single(static symbol => symbol.Name == "g" && symbol.IsImport).VisibilityScopePath is null,
            "A public glob retains unrestricted visibility for public targets.");
        SafeCoreNameResolutionResult routes = Resolve("mod api { pub fn f() {} } mod relay { pub(crate) use crate::api::*; pub use crate::api::*; } pub use crate::relay::*;");
        AssertEx.True(routes.IsSuccessful, Format(routes));
        AssertEx.True(routes.Symbols.Single(static symbol => symbol.QualifiedName == "crate::f").VisibilityScopePath is null,
            "Repeated routes to one target preserve the wider public route.");
        AssertCode(Resolve("mod outer { mod source { pub(super) fn f() {} } pub use self::source::*; } fn main() { outer::f(); }"), SafeCoreNameResolutionDiagnosticCodes.PrivateName);
        return Task.CompletedTask;
    }

    private static Task GlobLimitsAsync()
    {

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreSyntaxResult boundedSyntax = SafeCoreSyntax.Parse(
            "mod api { pub fn f() {} } use crate::api::*;",
            "glob-limit.rs",
            null,
            deadline.Token);
        AssertEx.True(boundedSyntax.IsSuccessful, string.Join("; ", boundedSyntax.Diagnostics));
        SafeCoreNameResolutionResult bounded = SafeCoreNameResolution.Resolve(
            boundedSyntax,
            new() { MaximumSymbols = 2, CancellationToken = deadline.Token, Timeout = TimeSpan.FromSeconds(5) });
        AssertEx.True(bounded.IsTruncated, "Glob expansion must obey the symbol budget.");
        AssertEx.True(SafeCoreNameResolution.Resolve(boundedSyntax, new() { MaximumOperations = 60, CancellationToken = deadline.Token }).IsTruncated,
            "Glob rounds must obey the operation budget.");
        AssertEx.True(SafeCoreNameResolution.Resolve(boundedSyntax, new() { Timeout = TimeSpan.FromTicks(1), CancellationToken = deadline.Token }).IsTruncated,
            "Glob rounds must obey the deadline.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreNameResolution.Resolve(boundedSyntax, new() { CancellationToken = cancelled.Token }));
        return Task.CompletedTask;
    }

    private static Task AnonymousImportsAsync()
    {
        SafeCoreNameResolutionResult result = Resolve("mod api { pub fn f() {} } use crate::api::{f as _, f as _}; use crate::api::f as _;");
        AssertEx.True(result.IsSuccessful, Format(result));
        AssertEx.Equal(3, result.Symbols.Count(static symbol => symbol.IsAnonymousImport));
        AssertEx.True(result.Symbols.Where(static symbol => symbol.IsAnonymousImport).All(static symbol => symbol.Name == "_" && symbol.ResolvedImportTargetQualifiedName == "crate::api::f"),
            "Anonymous imports still resolve their canonical targets.");
        AssertCode(Resolve("mod api { pub fn f() {} } use crate::api::f as _; fn main() { f(); }"), SafeCoreNameResolutionDiagnosticCodes.UnresolvedName);
        AssertCode(Resolve("use crate::missing as _;"), SafeCoreNameResolutionDiagnosticCodes.UnresolvedName);
        AssertCode(Resolve("mod api { fn f() {} } use crate::api::f as _;"), SafeCoreNameResolutionDiagnosticCodes.PrivateName);
        return Task.CompletedTask;
    }

    private static Task RestrictedVisibilityAsync()
    {
        const string source = "pub mod outer { pub mod inner { " +
            "pub(crate) fn wide() {} pub(super) fn parent() {} pub(in crate::outer) fn ancestor() {} " +
            "pub(self) fn own() {} pub mod child { fn check() { super::own(); } } } " +
            "pub(super) use self::inner::wide as exported; fn check() { inner::parent(); inner::ancestor(); } } " +
            "fn main() { outer::inner::wide(); outer::exported(); } " +
            "struct S { pub(crate) x: i32 } struct T(pub(self) i32);";
        SafeCoreNameResolutionResult result = Resolve(source);
        AssertEx.True(result.IsSuccessful, Format(result));
        AssertEx.Equal("crate", result.Symbols.Single(static symbol => symbol.Name == "wide").VisibilityScopePath!);
        AssertEx.Equal("crate::outer", result.Symbols.Single(static symbol => symbol.Name == "parent").VisibilityScopePath!);
        AssertEx.Equal("crate::outer", result.Symbols.Single(static symbol => symbol.Name == "ancestor").VisibilityScopePath!);
        AssertEx.Equal("crate::outer::inner", result.Symbols.Single(static symbol => symbol.Name == "own").VisibilityScopePath!);
        return Task.CompletedTask;
    }

    private static Task PrivateAccessAsync()
    {
        AssertCode(Resolve("pub mod outer { pub mod inner { pub(super) fn f() {} } } fn main() { outer::inner::f(); }"), SafeCoreNameResolutionDiagnosticCodes.PrivateName);
        AssertCode(Resolve("pub mod outer { pub mod inner { pub(self) fn f() {} } fn check() { inner::f(); } }"), SafeCoreNameResolutionDiagnosticCodes.PrivateName);
        AssertCode(Resolve("pub mod outer { pub mod inner { pub(in crate::outer) fn f() {} } } use crate::outer::inner::f;"), SafeCoreNameResolutionDiagnosticCodes.PrivateName);
        return Task.CompletedTask;
    }

    private static Task InvalidVisibilityAsync()
    {
        AssertCode(Resolve("pub(super) fn f() {}"), SafeCoreNameResolutionDiagnosticCodes.InvalidPath);
        AssertCode(Resolve("mod sibling {} pub(in crate::sibling) fn f() {}"), SafeCoreNameResolutionDiagnosticCodes.InvalidPath);
        AssertCode(Resolve("mod outer { mod child {} pub(in self::child) fn f() {} }"), SafeCoreNameResolutionDiagnosticCodes.InvalidPath);
        AssertCode(Resolve("mod real {} use crate::real as alias; mod inner { pub(in crate::alias) fn f() {} }"), SafeCoreNameResolutionDiagnosticCodes.InvalidPath);
        AssertCode(Resolve("struct T(pub(super) i32);"), SafeCoreNameResolutionDiagnosticCodes.InvalidPath);
        return Task.CompletedTask;
    }

    private static Task ReexportVisibilityAsync()
    {
        AssertCode(Resolve("mod api { pub(crate) fn f() {} } pub use crate::api::{f};"), SafeCoreNameResolutionDiagnosticCodes.PrivateName);
        AssertCode(Resolve("mod api { pub fn f() {} } mod relay { pub(crate) use crate::api::f as narrow; pub use self::narrow as wide; }"), SafeCoreNameResolutionDiagnosticCodes.PrivateName);
        AssertCode(Resolve("mod outer { mod inner { pub(super) fn f() {} } pub(crate) use self::inner::f; }"), SafeCoreNameResolutionDiagnosticCodes.PrivateName);
        SafeCoreNameResolutionResult result = Resolve("mod hidden { pub fn f() {} } pub use crate::hidden::{f as exported}; fn main() { exported(); }");
        AssertEx.True(result.IsSuccessful, "A public target may be re-exported through a private containing module: " + Format(result));
        AssertEx.True(result.Symbols.Single(static symbol => symbol.Name == "exported").VisibilityScopePath is null,
            "Unrestricted re-exports retain public visibility.");
        return Task.CompletedTask;
    }

    private static Task LimitsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse("mod api { pub fn f() {} } use crate::{api::{f as first, f as second}};", "module-limits.rs", null, deadline.Token);
        AssertEx.True(syntax.IsSuccessful, "The bounded group fixture must parse.");
        AssertEx.True(SafeCoreNameResolution.Resolve(syntax).IsSuccessful, "The tiny fixture must resolve before the limit checks.");
        SafeCoreNameResolutionResult operations = SafeCoreNameResolution.Resolve(syntax, new() { MaximumOperations = 8, CancellationToken = deadline.Token });
        AssertEx.True(operations.IsTruncated, "Import expansion must obey its operation limit.");
        SafeCoreNameResolutionResult depth = SafeCoreNameResolution.Resolve(syntax, new() { MaximumNestingDepth = 1, CancellationToken = deadline.Token });
        AssertEx.True(depth.IsTruncated, "Import expansion must obey its nesting limit.");
        SafeCoreNameResolutionResult timed = SafeCoreNameResolution.Resolve(syntax, new() { Timeout = TimeSpan.FromTicks(1), CancellationToken = deadline.Token });
        AssertEx.True(timed.IsTruncated, "Resolution must obey its wall-clock budget.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreNameResolution.Resolve(syntax, new() { CancellationToken = cancelled.Token }));
        return Task.CompletedTask;
    }

    private static SafeCoreNameResolutionResult Resolve(string source)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "module-resolution.rs", null, deadline.Token);
        AssertEx.True(syntax.IsSuccessful, source + ": " + string.Join("; ", syntax.Diagnostics));
        return SafeCoreNameResolution.Resolve(syntax, new() { CancellationToken = deadline.Token, Timeout = TimeSpan.FromSeconds(5) });
    }

    private static void AssertCode(SafeCoreNameResolutionResult result, string code)
    {
        AssertEx.False(result.IsSuccessful, "The invalid module fixture must fail resolution.");
        AssertEx.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == code), code + ": " + Format(result));
    }

    private static string Format(SafeCoreNameResolutionResult result) => string.Join("; ", result.Diagnostics);
}
