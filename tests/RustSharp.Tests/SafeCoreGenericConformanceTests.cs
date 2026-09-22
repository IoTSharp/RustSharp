using RustSharp.Compiler;
using RustSharp.Conformance;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreGenericConformanceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("generic conformance fixes 32 source outcomes and seven categories", ChecksCatalogAsync),
        new("generic conformance rejects malformed and unbounded catalogs", RejectsMalformedCatalogAsync),
        new("generic conformance rejects mismatched diagnostic evidence", RejectsMismatchedEvidenceAsync),
        new("generic conformance cannot pass incomplete reports", RejectsIncompleteReportsAsync),
    ];

    private static SafeCoreGenericProfileRunner.CatalogData Load(CancellationToken token = default)
    {
        string repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return SafeCoreGenericProfileRunner.LoadCatalog(repository, token);
    }

    private static Task ChecksCatalogAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        SafeCoreGenericProfileRunner.CatalogData data = Load(deadline.Token);
        IReadOnlyList<SafeCoreGenericProfileRunner.Fixture> catalog = data.Fixtures;
        AssertEx.Equal(2, SafeCoreGenericProfileRunner.CatalogVersion);
        AssertEx.Equal(32, catalog.Count);
        AssertEx.Equal(7, SafeCoreGenericProfileRunner.RequiredCategories.Count);
        AssertEx.Equal(9, catalog.Count(static fixture => fixture.Kind == "compile-pass"));
        AssertEx.Equal(8, catalog.Count(static fixture => fixture.Kind == "run-pass"));
        AssertEx.Equal(10, catalog.Count(static fixture => fixture.Kind == "compile-fail"));
        AssertEx.Equal(5, catalog.Count(static fixture => fixture.Kind == "profile-reject"));
        AssertEx.Equal(64, data.ManifestSha256.Length);
        foreach (SafeCoreGenericProfileRunner.Fixture fixture in catalog)
        {
            deadline.Token.ThrowIfCancellationRequested();
            CompilationResult result = CompilerDriver.Check(fixture.Source, fixture.Id + ".rs",
                CompilationProfile.SafeCoreGenerics, deadline.Token);
            AssertEx.True(SafeCoreGenericProfileRunner.Matches(fixture, result),
                $"{fixture.Id}: {string.Join("; ", result.Diagnostics)}");
        }
        return Task.CompletedTask;
    }

    private static Task RejectsMalformedCatalogAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreGenericProfileRunner.Fixture[] catalog = [.. Load(deadline.Token).Fixtures];
        void Reject(SafeCoreGenericProfileRunner.Fixture[] changed) => AssertEx.Throws<ArgumentException>(
            () => SafeCoreGenericProfileRunner.ValidateCatalog(changed, deadline.Token));
        Reject(catalog[..^1]);
        Reject([.. catalog, catalog[0]]);
        SafeCoreGenericProfileRunner.Fixture first = catalog[0];
        foreach (SafeCoreGenericProfileRunner.Fixture invalid in new[]
        {
            first with { Id = "../escaped" },
            first with { Id = null! },
            first with { Id = catalog[1].Id },
            first with { Category = "unknown" },
            first with { Category = null! },
            first with { Category = "bounds" },
            first with { Source = new string(' ', 65_537) },
            first with { ExpectedDiagnosticCode = "RSG1004" },
            first with { ExpectedRustcSuccess = false },
        })
        {
            catalog[0] = invalid;
            Reject(catalog);
        }
        catalog[0] = first;
        int failure = Array.FindIndex(catalog, static fixture => !fixture.ExpectedSuccess);
        SafeCoreGenericProfileRunner.Fixture expectedFailure = catalog[failure];
        foreach (SafeCoreGenericProfileRunner.Fixture invalid in new[]
        {
            expectedFailure with { ExpectedDiagnosticText = null },
            expectedFailure with { ExpectedDiagnosticCode = "RSG0002" },
            expectedFailure with { ExpectedDiagnosticText = "not in source" },
            expectedFailure with { ExpectedDiagnosticStart = int.MaxValue },
            expectedFailure with { ExpectedRustcSuccess = true },
        })
        {
            catalog[failure] = invalid;
            Reject(catalog);
        }
        catalog[failure] = expectedFailure;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreGenericProfileRunner.ValidateCatalog(catalog, cancelled.Token));
        AssertEx.Throws<OperationCanceledException>(() => Load(cancelled.Token));
        return Task.CompletedTask;
    }

    private static Task RejectsMismatchedEvidenceAsync()
    {
        SafeCoreGenericProfileRunner.Fixture fixture = Load().Fixtures.Single(static fixture => fixture.Id == "call-value-arity");
        int start = fixture.ExpectedDiagnosticStart!.Value;
        int length = fixture.ExpectedDiagnosticText!.Length;
        Diagnostic expected = new(fixture.ExpectedDiagnosticCode!, "arity", new TextSpan(start, length));
        AssertEx.True(SafeCoreGenericProfileRunner.Matches(fixture, CompilationResult.Failed([expected])),
            "The fixed code and exact source span must match.");
        AssertEx.False(SafeCoreGenericProfileRunner.Matches(fixture, CompilationResult.Failed([
            new Diagnostic("RSG0002", "timeout", expected.Span)])), "Budget exhaustion must not count as semantic rejection.");
        AssertEx.False(SafeCoreGenericProfileRunner.Matches(fixture, CompilationResult.Failed([
            new Diagnostic(expected.Code, "arity", new TextSpan(start, length - 1))])), "A shortened span must fail.");
        AssertEx.False(SafeCoreGenericProfileRunner.Matches(fixture, CompilationResult.Failed([
            new Diagnostic(expected.Code, "arity", new TextSpan(start + 1, length))])), "A shifted span must fail.");
        AssertEx.False(SafeCoreGenericProfileRunner.Matches(fixture, CompilationResult.Failed([
            expected, new Diagnostic("RSG0002", "timeout", expected.Span)])), "A matching diagnostic must not hide budget exhaustion.");
        AssertEx.False(SafeCoreGenericProfileRunner.Matches(fixture, new(true, [], null)), "Accepting an invalid fixture must fail.");
        SafeCoreGenericProfileRunner.Fixture pass = Load().Fixtures[0];
        AssertEx.False(SafeCoreGenericProfileRunner.Matches(pass, new(true, [expected], null)), "A passing fixture must have no errors.");
        return Task.CompletedTask;
    }

    private static Task RejectsIncompleteReportsAsync()
    {
        AssertEx.Equal("passed", SafeCoreGenericProfileRunner.ReportStatus(32, 32, 0, 0, true, false, null));
        AssertEx.Equal("blocked", SafeCoreGenericProfileRunner.ReportStatus(32, 31, 0, 1, true, false, null));
        AssertEx.Equal("blocked", SafeCoreGenericProfileRunner.ReportStatus(32, 32, 0, 0, false, false, null));
        AssertEx.Equal("blocked", SafeCoreGenericProfileRunner.ReportStatus(32, 32, 0, 0, true, true, null));
        AssertEx.Equal("failed", SafeCoreGenericProfileRunner.ReportStatus(32, 31, 1, 0, true, false, null));
        AssertEx.Equal("failed", SafeCoreGenericProfileRunner.ReportStatus(32, 31, 0, 0, true, false, null));
        AssertEx.Equal("failed", SafeCoreGenericProfileRunner.ReportStatus(32, 32, 0, 0, true, false, "owned directory remains"));
        AssertEx.Equal("failed", SafeCoreGenericProfileRunner.ReportStatus(0, 0, 0, 0, true, false, null));
        return Task.CompletedTask;
    }
}
