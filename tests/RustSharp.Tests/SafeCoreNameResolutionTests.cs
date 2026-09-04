using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreNameResolutionTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("name resolution collects safe-core symbols", CollectsSymbolsAsync),
        new("name resolution resolves imports and qualified paths", ResolvesImportsAndPathsAsync),
        new("name resolution reports duplicate and ambiguous names", ReportsDuplicatesAsync),
        new("name resolution reports unresolved and private names", ReportsMissingAndPrivateAsync),
        new("name resolution reports import cycles", ReportsImportCyclesAsync),
        new("name resolution obeys local declaration order and shadowing", ResolvesLocalScopesAsync),
        new("name resolution canonicalizes Unicode identifiers", CanonicalizesUnicodeIdentifiersAsync),
        new("name resolution rejects qualified lexical members", RejectsQualifiedLexicalMembersAsync),
        new("name resolution obeys explicit work limits", ObeysLimitsAsync),
    ];

    private static Task CollectsSymbolsAsync()
    {
        const string source =
            "pub mod api {\n" +
            "    pub struct User { pub id: i32 }\n" +
            "    pub enum State { Ready, Done }\n" +
            "    pub type Id = i32;\n" +
            "    pub const LIMIT: i32 = 1;\n" +
            "    pub fn make(value: Id) -> User { return value; }\n" +
            "}\n" +
            "use crate::api::User as PublicUser;\n";

        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "name-resolution-symbols.rs");
        AssertEx.True(syntax.IsSuccessful, "The symbol fixture must parse.");
        SafeCoreNameResolutionResult result = SafeCoreNameResolution.Resolve(syntax);
        AssertEx.True(result.IsSuccessful, FormatDiagnostics(result));

        AssertSymbol(result, "crate::api", SafeCoreSymbolKind.Module);
        AssertSymbol(result, "crate::api::User", SafeCoreSymbolKind.Struct);
        AssertSymbol(result, "crate::api::State", SafeCoreSymbolKind.Enum);
        AssertSymbol(result, "crate::api::Id", SafeCoreSymbolKind.TypeAlias);
        AssertSymbol(result, "crate::api::LIMIT", SafeCoreSymbolKind.Const);
        AssertSymbol(result, "crate::api::make", SafeCoreSymbolKind.Function);
        AssertSymbol(result, "crate::PublicUser", SafeCoreSymbolKind.Import);
        AssertEx.True(
            result.Symbols.Any(symbol => symbol.QualifiedName == "crate::api::State::Ready" &&
                symbol.Kind == SafeCoreSymbolKind.EnumVariant),
            "Enum variants should be present in the member scope.");
        AssertEx.True(
            result.Scopes.Any(scope => scope.Path == "crate::api"),
            "The module scope should be exposed.");
        return Task.CompletedTask;
    }

    private static Task ResolvesImportsAndPathsAsync()
    {
        const string source =
            "pub mod api {\n" +
            "    pub struct User { pub id: i32 }\n" +
            "    pub enum State { Ready }\n" +
            "    pub type Id = i32;\n" +
            "    pub fn make(value: Id) -> User { return value; }\n" +
            "}\n" +
            "pub mod r#raw_api { pub type r#Item = i32; }\n" +
            "use crate::api::User as PublicUser;\n" +
            "use crate::api as a;\n" +
            "use a as b;\n" +
            "use crate::r#raw_api::r#Item as r#RawItem;\n" +
            "type ImportedId = b::Id;\n" +
            "type ImportedRaw = RawItem;\n" +
            "type QualifiedRaw = crate::r#raw_api::r#Item;\n" +
            "fn r#raw_fn() {}\n" +
            "fn call_raw_fn() { raw_fn(); r#raw_fn(); }\n" +
            "fn consume(value: PublicUser) -> crate::api::Id {\n" +
            "    crate::api::State::Ready;\n" +
            "    return value;\n" +
            "}\n";

        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "name-resolution-paths.rs");
        AssertEx.True(syntax.IsSuccessful, "The path fixture must parse: " + string.Join("; ", syntax.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message + "@" + diagnostic.Span.Start + "+" + diagnostic.Span.Length + "='" + syntax.GetText(diagnostic.Span) + "'")));
        SafeCoreNameResolutionResult result = SafeCoreNameResolution.Resolve(syntax);
        AssertEx.True(result.IsSuccessful, FormatDiagnostics(result));

        SafeCorePathResolution importUse = AssertEx.NotNull(
            result.FindResolution("PublicUser"),
            "The imported type reference should be recorded.");
        AssertEx.Equal(SafeCoreNameResolutionStatus.Resolved, importUse.Status);
        AssertEx.Equal(SafeCoreSymbolKind.Import, importUse.Symbol!.Kind);

        SafeCorePathResolution qualifiedType = AssertEx.NotNull(
            result.FindResolution("crate::api::Id"),
            "The qualified type reference should be recorded.");
        AssertEx.Equal(SafeCoreNameResolutionStatus.Resolved, qualifiedType.Status);
        AssertEx.Equal("crate::api::Id", qualifiedType.Symbol!.QualifiedName);

        SafeCorePathResolution chainedAlias = AssertEx.NotNull(
            result.FindResolution("b::Id"),
            "A qualified path through two import aliases should be recorded.");
        AssertEx.Equal(SafeCoreNameResolutionStatus.Resolved, chainedAlias.Status);
        AssertEx.Equal("crate::api::Id", chainedAlias.Symbol!.QualifiedName);

        AssertSymbol(result, "crate::raw_api", SafeCoreSymbolKind.Module);
        AssertSymbol(result, "crate::raw_api::Item", SafeCoreSymbolKind.TypeAlias);
        AssertSymbol(result, "crate::raw_fn", SafeCoreSymbolKind.Function);
        SafeCorePathResolution rawImport = AssertEx.NotNull(
            result.FindResolution("RawItem"),
            "A plain path should resolve an import declared with a raw identifier.");
        AssertEx.Equal(SafeCoreNameResolutionStatus.Resolved, rawImport.Status);
        AssertEx.Equal("RawItem", rawImport.Symbol!.Name);
        AssertEx.Equal("crate::RawItem", rawImport.Symbol.QualifiedName);
        AssertEx.Equal("crate::r#raw_api::r#Item", rawImport.Symbol.TargetPath!);

        SafeCorePathResolution rawQualifiedType = AssertEx.NotNull(
            result.FindResolution("crate::r#raw_api::r#Item"),
            "A qualified path should preserve raw source spelling in the resolution record.");
        AssertEx.Equal(SafeCoreNameResolutionStatus.Resolved, rawQualifiedType.Status);
        AssertEx.Equal("crate::raw_api::Item", rawQualifiedType.Symbol!.QualifiedName);

        foreach (string path in new[] { "raw_fn", "r#raw_fn" })
        {
            SafeCorePathResolution rawFunction = AssertEx.NotNull(
                result.FindResolution(path, "crate::call_raw_fn"),
                $"Function lookup should canonicalize '{path}'.");
            AssertEx.Equal(SafeCoreNameResolutionStatus.Resolved, rawFunction.Status);
            AssertEx.Equal("crate::raw_fn", rawFunction.Symbol!.QualifiedName);
        }

        SafeCorePathResolution variant = AssertEx.NotNull(
            result.FindResolution("crate::api::State::Ready"),
            "The qualified enum variant should be recorded.");
        AssertEx.Equal(SafeCoreNameResolutionStatus.Resolved, variant.Status);
        AssertEx.Equal(SafeCoreSymbolKind.EnumVariant, variant.Symbol!.Kind);
        return Task.CompletedTask;
    }

    private static Task ReportsDuplicatesAsync()
    {
        const string source =
            "fn same() {} fn r#same() {} " +
            "type Alias = i32; type r#Alias = i32; " +
            "fn imported() {} " +
            "use imported as duplicate_import; use r#imported as r#duplicate_import; " +
            "fn caller() { same(); }";
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "name-resolution-duplicate.rs");
        AssertEx.True(syntax.IsSuccessful, "The duplicate fixture must parse.");
        SafeCoreNameResolutionResult result = SafeCoreNameResolution.Resolve(syntax);

        AssertEx.False(result.IsSuccessful, "Duplicate declarations must fail resolution.");
        AssertEx.True(
            result.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.DuplicateSymbol),
            "A duplicate declaration should emit RSN1002.");
        AssertEx.True(
            result.Diagnostics.Count(diagnostic =>
                diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.DuplicateSymbol) >= 3,
            "Raw-equivalent function, type, and import declarations should each emit RSN1002.");
        AssertEx.True(
            result.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.AmbiguousName),
            "A use of duplicate declarations should emit RSN1004.");
        SafeCorePathResolution call = AssertEx.NotNull(result.FindResolution("same"), "The call path should be recorded.");
        AssertEx.Equal(SafeCoreNameResolutionStatus.Ambiguous, call.Status);
        AssertEx.Equal(2, call.Candidates.Count);
        AssertEx.True(
            call.Candidates.All(candidate => candidate.Name == "same" && candidate.QualifiedName == "crate::same"),
            "Raw-equivalent function symbols should expose canonical names.");
        AssertEx.Equal(
            2,
            result.Symbols.Count(symbol =>
                symbol.Kind == SafeCoreSymbolKind.TypeAlias &&
                symbol.Name == "Alias" &&
                symbol.QualifiedName == "crate::Alias"));
        SafeCoreSymbol[] duplicateImports = result.Symbols
            .Where(symbol => symbol.Kind == SafeCoreSymbolKind.Import && symbol.Name == "duplicate_import")
            .ToArray();
        AssertEx.Equal(2, duplicateImports.Length);
        AssertEx.True(
            duplicateImports.All(symbol => symbol.QualifiedName == "crate::duplicate_import"),
            "Raw-equivalent imports should expose one canonical qualified name.");
        AssertEx.True(
            duplicateImports.Any(symbol => symbol.TargetPath == "r#imported"),
            "An import target should preserve its original raw source spelling.");
        return Task.CompletedTask;
    }

    private static Task ReportsMissingAndPrivateAsync()
    {
        const string source =
            "mod hidden { fn secret() {} }\n" +
            "fn caller() { crate::hidden::secret(); missing(); }\n";
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "name-resolution-missing.rs");
        AssertEx.True(syntax.IsSuccessful, "The missing-name fixture must parse.");
        SafeCoreNameResolutionResult result = SafeCoreNameResolution.Resolve(syntax);

        AssertEx.False(result.IsSuccessful, "Missing and private names must fail resolution.");
        AssertEx.True(
            result.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.PrivateName),
            "A private nested item should emit RSN1005.");
        AssertEx.True(
            result.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.UnresolvedName),
            "An unknown item should emit RSN1003.");
        AssertEx.Equal(
            SafeCoreNameResolutionStatus.Private,
            AssertEx.NotNull(result.FindResolution("crate::hidden::secret"), "Private path should be recorded.").Status);
        AssertEx.Equal(
            SafeCoreNameResolutionStatus.Unresolved,
            AssertEx.NotNull(result.FindResolution("missing"), "Missing path should be recorded.").Status);

        const string forbiddenRawSource =
            "fn invalid_paths() { r#crate; r#self; r#super; r#Self; } " +
            "fn r#crate() {}";
        SafeCoreSyntaxResult forbiddenRawSyntax = SafeCoreSyntax.Parse(
            forbiddenRawSource,
            "name-resolution-forbidden-raw.rs");
        AssertEx.True(forbiddenRawSyntax.IsSuccessful, "The forbidden-raw fixture must parse.");
        SafeCoreNameResolutionResult forbiddenRaw = SafeCoreNameResolution.Resolve(forbiddenRawSyntax);
        AssertEx.False(forbiddenRaw.IsSuccessful, "Forbidden raw identifiers must fail resolution.");
        foreach (string path in new[] { "r#crate", "r#self", "r#super", "r#Self" })
        {
            AssertEx.Equal(
                SafeCoreNameResolutionStatus.Invalid,
                AssertEx.NotNull(
                    forbiddenRaw.FindResolution(path, "crate::invalid_paths"),
                    $"Forbidden raw path '{path}' should be recorded.").Status);
        }

        AssertEx.True(
            forbiddenRaw.Diagnostics.Count(diagnostic =>
                diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.InvalidPath) >= 5,
            "Forbidden raw paths and declarations should emit RSN1001.");
        AssertEx.False(
            forbiddenRaw.Symbols.Any(symbol => symbol.Name == "crate"),
            "A forbidden raw declaration must not enter the canonical symbol table.");
        return Task.CompletedTask;
    }

    private static Task ObeysLimitsAsync()
    {
        const string source = "fn one() {} fn two() {} fn three() {}";
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "name-resolution-limit.rs");
        AssertEx.True(syntax.IsSuccessful, "The limit fixture must parse.");
        SafeCoreNameResolutionResult result = SafeCoreNameResolution.Resolve(
            syntax,
            new SafeCoreNameResolutionOptions
            {
                MaximumSymbols = 2,
                MaximumScopes = 8,
                MaximumOperations = 128,
                MaximumDiagnostics = 8,
            });

        AssertEx.True(result.IsTruncated, "A symbol limit should truncate resolution.");
        AssertEx.True(
            result.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.LimitReached),
            "A bounded run should emit RSN0002.");
        AssertEx.True(result.Symbols.Count <= 2, "The symbol count must stay within the configured bound.");

        const string importSource =
            "use second as first; use third as second; use target as third; fn target() {}";
        SafeCoreSyntaxResult importSyntax = SafeCoreSyntax.Parse(importSource, "name-resolution-import-depth.rs");
        AssertEx.True(importSyntax.IsSuccessful, "The import-depth fixture must parse.");
        SafeCoreNameResolutionResult importResult = SafeCoreNameResolution.Resolve(
            importSyntax,
            new SafeCoreNameResolutionOptions
            {
                MaximumNestingDepth = 1,
                MaximumDiagnostics = 8,
                MaximumOperations = 256,
            });
        AssertEx.True(importResult.IsTruncated, "An import chain must obey the nesting limit.");
        AssertEx.True(
            importResult.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.LimitReached),
            "An import-depth limit should emit RSN0002.");
        return Task.CompletedTask;
    }

    private static Task ReportsImportCyclesAsync()
    {
        const string source =
            "use second as first;\n" +
            "use first as second;\n" +
            "fn caller() { first(); }\n";
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "name-resolution-cycle.rs");
        AssertEx.True(syntax.IsSuccessful, "The import-cycle fixture must parse.");
        SafeCoreNameResolutionResult result = SafeCoreNameResolution.Resolve(syntax);

        AssertEx.False(result.IsSuccessful, "An import cycle must fail resolution.");
        AssertEx.True(
            result.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.ImportCycle),
            "An import cycle should emit RSN1006.");
        AssertEx.Equal(
            SafeCoreNameResolutionStatus.Ambiguous,
            AssertEx.NotNull(result.FindResolution("first"), "A use of the cyclic import should be recorded.").Status);
        return Task.CompletedTask;
    }

    private static Task ResolvesLocalScopesAsync()
    {
        const string invalidSource =
            "fn before() { value; let value: i32 = 1; }\n" +
            "fn self_init() { let value: i32 = value; }\n";
        SafeCoreSyntaxResult invalidSyntax = SafeCoreSyntax.Parse(invalidSource, "name-resolution-order.rs");
        AssertEx.True(invalidSyntax.IsSuccessful, "The declaration-order fixture must parse.");
        SafeCoreNameResolutionResult invalid = SafeCoreNameResolution.Resolve(invalidSyntax);

        AssertEx.False(invalid.IsSuccessful, "A future local and a self-initializer must remain unresolved.");
        AssertEx.Equal(
            SafeCoreNameResolutionStatus.Unresolved,
            AssertEx.NotNull(invalid.FindResolution("value", "crate::before"), "The use-before-let path should be recorded.").Status);
        AssertEx.Equal(
            SafeCoreNameResolutionStatus.Unresolved,
            AssertEx.NotNull(invalid.FindResolution("value", "crate::self_init"), "The self-initializer path should be recorded.").Status);

        const string shadowSource =
            "fn shadow(value: i32) { " +
            "let value: i32 = value; let value: i32 = value; value; }\n" +
            "fn raw_to_plain() { let r#raw_value: i32 = 1; raw_value; }\n" +
            "fn plain_to_raw() { let plain_value: i32 = 1; r#plain_value; }\n" +
            "fn raw_shadow(value: i32) { " +
            "let r#value: i32 = value; let value: i32 = r#value; value; }";
        SafeCoreSyntaxResult shadowSyntax = SafeCoreSyntax.Parse(shadowSource, "name-resolution-shadow.rs");
        AssertEx.True(shadowSyntax.IsSuccessful, "The shadowing fixture must parse.");
        SafeCoreNameResolutionResult shadow = SafeCoreNameResolution.Resolve(shadowSyntax);
        AssertEx.True(shadow.IsSuccessful, FormatDiagnostics(shadow));

        SafeCorePathResolution[] uses = shadow.Resolutions
            .Where(resolution => resolution.Path == "value" && resolution.ScopePath == "crate::shadow")
            .ToArray();
        AssertEx.Equal(3, uses.Length);
        AssertEx.Equal(SafeCoreSymbolKind.Parameter, uses[0].Symbol!.Kind);
        AssertEx.Equal(SafeCoreSymbolKind.Local, uses[1].Symbol!.Kind);
        AssertEx.Equal(SafeCoreSymbolKind.Local, uses[2].Symbol!.Kind);
        AssertEx.True(
            uses[1].Symbol!.Span.Start < uses[2].Symbol!.Span.Start,
            "The final use must bind to the later shadowing declaration.");

        SafeCorePathResolution rawToPlain = AssertEx.NotNull(
            shadow.FindResolution("raw_value", "crate::raw_to_plain"),
            "A plain use should resolve a raw-spelled local declaration.");
        AssertEx.Equal(SafeCoreNameResolutionStatus.Resolved, rawToPlain.Status);
        AssertEx.Equal("raw_value", rawToPlain.Symbol!.Name);
        AssertEx.Equal("crate::raw_to_plain::raw_value", rawToPlain.Symbol.QualifiedName);

        SafeCorePathResolution plainToRaw = AssertEx.NotNull(
            shadow.FindResolution("r#plain_value", "crate::plain_to_raw"),
            "A raw-spelled use should resolve a plain local declaration.");
        AssertEx.Equal(SafeCoreNameResolutionStatus.Resolved, plainToRaw.Status);
        AssertEx.Equal("plain_value", plainToRaw.Symbol!.Name);
        AssertEx.Equal("crate::plain_to_raw::plain_value", plainToRaw.Symbol.QualifiedName);

        SafeCorePathResolution[] rawShadowUses = shadow.Resolutions
            .Where(resolution =>
                resolution.ScopePath == "crate::raw_shadow" &&
                resolution.Path is "value" or "r#value")
            .OrderBy(resolution => resolution.Span.Start)
            .ToArray();
        AssertEx.Equal(3, rawShadowUses.Length);
        AssertEx.Equal(SafeCoreSymbolKind.Parameter, rawShadowUses[0].Symbol!.Kind);
        AssertEx.Equal(SafeCoreSymbolKind.Local, rawShadowUses[1].Symbol!.Kind);
        AssertEx.Equal(SafeCoreSymbolKind.Local, rawShadowUses[2].Symbol!.Kind);
        AssertEx.True(
            rawShadowUses[1].Symbol!.Span.Start < rawShadowUses[2].Symbol!.Span.Start,
            "Raw/plain shadowing should select the latest canonical local.");
        AssertEx.True(
            rawShadowUses.All(resolution => resolution.Symbol!.Name == "value"),
            "Raw/plain shadowing should expose one canonical semantic name.");

        const string parameterPatternSource =
            "enum Wrapper<T> { Value(T) } " +
            "fn destructure(Wrapper::Value(inner): Wrapper<i32>) { inner; }";
        SafeCoreSyntaxResult parameterPatternSyntax = SafeCoreSyntax.Parse(
            parameterPatternSource,
            "name-resolution-parameter-pattern.rs");
        AssertEx.True(parameterPatternSyntax.IsSuccessful, "The parameter-pattern fixture must parse.");
        SafeCoreNameResolutionResult parameterPattern = SafeCoreNameResolution.Resolve(parameterPatternSyntax);
        AssertEx.True(parameterPattern.IsSuccessful, FormatDiagnostics(parameterPattern));
        SafeCorePathResolution parameterUse = AssertEx.NotNull(
            parameterPattern.FindResolution("inner", "crate::destructure"),
            "The destructured parameter use should be recorded.");
        AssertEx.Equal(SafeCoreSymbolKind.Parameter, parameterUse.Symbol!.Kind);

        const string duplicatePatternSource =
            "fn duplicate() { let (x, r#x): (i32, i32) = (1, 2); } " +
            "fn duplicate_parameters(x: i32, r#x: i32) {}";
        SafeCoreSyntaxResult duplicatePatternSyntax = SafeCoreSyntax.Parse(
            duplicatePatternSource,
            "name-resolution-raw-pattern.rs");
        AssertEx.True(duplicatePatternSyntax.IsSuccessful, "The raw-pattern fixture must parse.");
        SafeCoreNameResolutionResult duplicatePattern = SafeCoreNameResolution.Resolve(duplicatePatternSyntax);
        AssertEx.False(duplicatePattern.IsSuccessful, "Raw-equivalent names in one pattern must be rejected.");
        AssertEx.True(
            duplicatePattern.Diagnostics.Any(diagnostic =>
                diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.DuplicateSymbol),
            "A raw-equivalent duplicate pattern binding should emit RSN1002.");
        AssertEx.True(
            duplicatePattern.Diagnostics.Count(diagnostic =>
                diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.DuplicateSymbol) >= 2,
            "Raw-equivalent bindings across parameter patterns should also emit RSN1002.");
        return Task.CompletedTask;
    }

    private static Task RejectsQualifiedLexicalMembersAsync()
    {
        const string source =
            "fn hidden_owner() { let hidden: i32 = 1; }\n" +
            "pub struct Record { pub field: i32 }\n" +
            "enum Choice<T> { One(T) }\n" +
            "mod api { pub enum RemoteChoice<T> { One(T) } }\n" +
            "type Invalid = Choice::T;\n" +
            "type RemoteInvalid = crate::api::RemoteChoice::T;\n" +
            "fn inspect() { crate::hidden_owner::hidden; crate::Record::field; }\n";
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "name-resolution-qualified-lexical.rs");
        AssertEx.True(syntax.IsSuccessful, "The qualified-member fixture must parse.");
        SafeCoreNameResolutionResult result = SafeCoreNameResolution.Resolve(syntax);

        AssertEx.False(result.IsSuccessful, "Lexical bindings and fields must not be addressable through '::'.");
        foreach (string path in new[]
        {
            "Choice::T",
            "crate::api::RemoteChoice::T",
            "crate::hidden_owner::hidden",
            "crate::Record::field",
        })
        {
            AssertEx.Equal(
                SafeCoreNameResolutionStatus.Unresolved,
                AssertEx.NotNull(result.FindResolution(path), $"Qualified path '{path}' should be recorded.").Status);
        }

        SafeCorePathResolution remoteGeneric = AssertEx.NotNull(
            result.FindResolution("crate::api::RemoteChoice::T"),
            "The cross-module enum generic path should be recorded.");
        AssertEx.True(
            result.Diagnostics.Any(diagnostic =>
                diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.UnresolvedName &&
                diagnostic.Span.Start == remoteGeneric.Span.Start &&
                diagnostic.Span.Length == remoteGeneric.Span.Length),
            "An ineligible enum generic parameter should emit RSN1003.");
        AssertEx.False(
            result.Diagnostics.Any(diagnostic =>
                diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.PrivateName &&
                diagnostic.Span.Start == remoteGeneric.Span.Start &&
                diagnostic.Span.Length == remoteGeneric.Span.Length),
            "An ineligible enum generic parameter must not emit RSN1005.");

        return Task.CompletedTask;
    }

    private static Task CanonicalizesUnicodeIdentifiersAsync()
    {
        const string decomposedName = "e\u0301";
        const string composedName = "\u00e9";
        string source =
            $"fn {decomposedName}() {{}}\n" +
            $"fn caller() {{ {composedName}(); {decomposedName}(); }}\n";
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "name-resolution-unicode.rs");
        AssertEx.True(syntax.IsSuccessful, "A combining mark must be accepted after an identifier start.");
        AssertEx.Equal(decomposedName, ((SafeCoreFunctionSyntax)syntax.Root!.Items[0]).Name);

        SafeCoreNameResolutionResult result = SafeCoreNameResolution.Resolve(syntax);
        AssertEx.True(result.IsSuccessful, FormatDiagnostics(result));
        AssertSymbol(result, "crate::" + composedName, SafeCoreSymbolKind.Function);
        foreach (string path in new[] { composedName, decomposedName })
        {
            SafeCorePathResolution resolution = AssertEx.NotNull(
                result.FindResolution(path, "crate::caller"),
                $"The Unicode path '{path}' should be recorded.");
            AssertEx.Equal(SafeCoreNameResolutionStatus.Resolved, resolution.Status);
            AssertEx.Equal("crate::" + composedName, resolution.Symbol!.QualifiedName);
        }

        string duplicateSource = $"fn {composedName}() {{}} fn {decomposedName}() {{}}";
        SafeCoreSyntaxResult duplicateSyntax = SafeCoreSyntax.Parse(
            duplicateSource,
            "name-resolution-unicode-duplicate.rs");
        AssertEx.True(duplicateSyntax.IsSuccessful, "NFC-equivalent declarations must both parse.");
        SafeCoreNameResolutionResult duplicate = SafeCoreNameResolution.Resolve(duplicateSyntax);
        AssertEx.False(duplicate.IsSuccessful, "NFC-equivalent declarations must conflict.");
        AssertEx.True(
            duplicate.Diagnostics.Any(diagnostic =>
                diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.DuplicateSymbol),
            "NFC-equivalent declarations should emit RSN1002.");

        const string invalidName = "bad-name";
        var invalidSpan = new TextSpan(0, invalidName.Length);
        var invalidRoot = new SafeCoreCompilationUnitSyntax(
            [],
            [new SafeCoreFunctionSyntax(
                invalidName,
                [],
                [],
                null,
                new SafeCoreBlockSyntax([], null, invalidSpan),
                false,
                [],
                invalidSpan)],
            invalidSpan);
        SafeCoreNameResolutionResult invalid = SafeCoreNameResolution.Collect(
            invalidRoot,
            "name-resolution-invalid-identifier.rs");
        AssertEx.False(invalid.IsSuccessful, "An invalid AST identifier must fail name collection.");
        AssertEx.True(
            invalid.Diagnostics.Any(diagnostic =>
                diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.InvalidPath),
            "An invalid AST identifier should emit RSN1001.");
        AssertEx.False(
            invalid.Symbols.Any(symbol => symbol.Name == invalidName),
            "An invalid AST identifier must not enter the symbol table.");
        return Task.CompletedTask;
    }

    private static void AssertSymbol(
        SafeCoreNameResolutionResult result,
        string qualifiedName,
        SafeCoreSymbolKind kind)
    {
        SafeCoreSymbol symbol = AssertEx.NotNull(
            result.Symbols.SingleOrDefault(candidate => candidate.QualifiedName == qualifiedName),
            $"Expected symbol '{qualifiedName}'.");
        AssertEx.Equal(kind, symbol.Kind);
    }

    private static string FormatDiagnostics(SafeCoreNameResolutionResult result) =>
        string.Join(
            "; ",
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}:{diagnostic.Message} [{diagnostic.Span.Start},{diagnostic.Span.Length}]"));
}
