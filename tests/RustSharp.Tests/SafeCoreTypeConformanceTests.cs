using RustSharp.Compiler;
using RustSharp.Conformance;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreTypeConformanceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("type conformance v2 fixes 96 source outcomes and 16 required categories", ChecksCatalogAsync),
        new("type conformance rejects wrong diagnostic codes and source spans", RejectsMismatchedEvidenceAsync),
        new("type conformance rejects incomplete categories and malformed fixtures", RejectsMalformedCatalogAsync),
        new("type conformance validation bounds work and respects cancellation", BoundsValidationAsync),
        new("type conformance cannot pass skipped incomplete or failed cleanup reports", RejectsIncompleteReportsAsync),
    ];

    private static Task ChecksCatalogAsync()
    {
        IReadOnlyList<SafeCoreTypeProfileRunner.Fixture> catalog = SafeCoreTypeProfileRunner.Catalog;
        AssertEx.Equal(2, SafeCoreTypeProfileRunner.CatalogVersion);
        AssertEx.Equal(96, catalog.Count);
        AssertEx.Equal(60, catalog.Count(static fixture => fixture.ExpectedSuccess));
        AssertEx.Equal(16, SafeCoreTypeProfileRunner.RequiredCategories.Count);
        AssertEx.Equal(catalog.Count, catalog.Select(static fixture => fixture.Id).Distinct(StringComparer.Ordinal).Count());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        SafeCoreTypeProfileRunner.ValidateCatalog(catalog, deadline.Token);
        foreach (SafeCoreTypeProfileRunner.Fixture fixture in catalog)
        {
            deadline.Token.ThrowIfCancellationRequested();
            CompilationResult result = CompilerDriver.Check(fixture.Source, fixture.Id + ".rs",
                CompilationProfile.SafeCoreTypes, deadline.Token);
            AssertEx.True(SafeCoreTypeProfileRunner.Matches(fixture, result),
                $"{fixture.Id}: {string.Join("; ", result.Diagnostics)}");
        }
        return Task.CompletedTask;
    }

    private static Task RejectsMismatchedEvidenceAsync()
    {
        SafeCoreTypeProfileRunner.Fixture fixture = SafeCoreTypeProfileRunner.Catalog.Single(static fixture => fixture.Id == "integer-range");
        int start = fixture.Source.IndexOf("256", StringComparison.Ordinal);
        CompilationResult valid = CompilationResult.Failed([new Diagnostic("RST2006", "range", new TextSpan(start, 3))]);
        AssertEx.True(SafeCoreTypeProfileRunner.Matches(fixture, valid), "Expected code and exact source text should match.");
        CompilationResult wrongCode = CompilationResult.Failed([new Diagnostic("RST0002", "timeout", new TextSpan(start, 3))]);
        AssertEx.False(SafeCoreTypeProfileRunner.Matches(fixture, wrongCode), "A budget failure must not count as a type rejection.");
        CompilationResult wrongSpan = CompilationResult.Failed([new Diagnostic("RST2006", "range", new TextSpan(start, 2))]);
        AssertEx.False(SafeCoreTypeProfileRunner.Matches(fixture, wrongSpan), "Different source text must not match an exact span expectation.");
        CompilationResult extraError = CompilationResult.Failed([new Diagnostic("RST2006", "range", new TextSpan(start, 3)),
            new Diagnostic("RST0002", "timeout", new TextSpan(start, 3))]);
        AssertEx.False(SafeCoreTypeProfileRunner.Matches(fixture, extraError), "A valid error must not hide a budget failure.");
        AssertEx.False(SafeCoreTypeProfileRunner.Matches(fixture, new(true, [], null)), "An accepted invalid fixture must fail its gate.");
        return Task.CompletedTask;
    }

    private static Task RejectsMalformedCatalogAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreTypeProfileRunner.Fixture[] catalog = [.. SafeCoreTypeProfileRunner.Catalog];
        AssertEx.Throws<ArgumentException>(() => SafeCoreTypeProfileRunner.ValidateCatalog([catalog[0]], deadline.Token));
        AssertEx.Throws<ArgumentException>(() => SafeCoreTypeProfileRunner.ValidateCatalog(
            catalog.Where(static fixture => fixture.Category != "closures").ToArray(), deadline.Token));
        AssertEx.Throws<ArgumentException>(() => SafeCoreTypeProfileRunner.ValidateCatalog(
            catalog.Where(static fixture => fixture.Category != "const" || fixture.ExpectedSuccess).ToArray(), deadline.Token));
        AssertEx.Throws<ArgumentException>(() => SafeCoreTypeProfileRunner.ValidateCatalog(
            catalog.Where(static fixture => fixture.Id != "signed-primitives").ToArray(), deadline.Token));
        AssertEx.Throws<ArgumentException>(() => SafeCoreTypeProfileRunner.ValidateCatalog([.. catalog, catalog[0]], deadline.Token));
        SafeCoreTypeProfileRunner.Fixture original = catalog[0];
        catalog[0] = original with { Id = "../escaped" };
        AssertEx.Throws<ArgumentException>(() => SafeCoreTypeProfileRunner.ValidateCatalog(catalog, deadline.Token));
        catalog[0] = original with { Category = "unknown" };
        AssertEx.Throws<ArgumentException>(() => SafeCoreTypeProfileRunner.ValidateCatalog(catalog, deadline.Token));
        catalog[0] = original with { Category = "tuples" };
        AssertEx.Throws<ArgumentException>(() => SafeCoreTypeProfileRunner.ValidateCatalog(catalog, deadline.Token));
        catalog[0] = original with { ExpectedDiagnosticCode = "RST2002" };
        AssertEx.Throws<ArgumentException>(() => SafeCoreTypeProfileRunner.ValidateCatalog(catalog, deadline.Token));
        catalog[0] = original;
        int failure = Array.FindIndex(catalog, static fixture => !fixture.ExpectedSuccess);
        SafeCoreTypeProfileRunner.Fixture expectedFailure = catalog[failure];
        catalog[failure] = expectedFailure with { ExpectedDiagnosticCode = "RST0002" };
        AssertEx.Throws<ArgumentException>(() => SafeCoreTypeProfileRunner.ValidateCatalog(catalog, deadline.Token));
        catalog[failure] = expectedFailure with { ExpectedDiagnosticText = "not in source" };
        AssertEx.Throws<ArgumentException>(() => SafeCoreTypeProfileRunner.ValidateCatalog(catalog, deadline.Token));
        catalog[failure] = expectedFailure with { ExpectedDiagnosticStart = int.MaxValue };
        AssertEx.Throws<ArgumentException>(() => SafeCoreTypeProfileRunner.ValidateCatalog(catalog, deadline.Token));
        return Task.CompletedTask;
    }

    private static Task BoundsValidationAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreTypeProfileRunner.Fixture first = SafeCoreTypeProfileRunner.Catalog[0];
        AssertEx.Throws<ArgumentException>(() => SafeCoreTypeProfileRunner.ValidateCatalog(
            Enumerable.Repeat(first, 129).ToArray(), deadline.Token));
        SafeCoreTypeProfileRunner.Fixture[] oversized = [.. SafeCoreTypeProfileRunner.Catalog];
        oversized[0] = first with { Source = new string(' ', 65_537) };
        AssertEx.Throws<ArgumentException>(() => SafeCoreTypeProfileRunner.ValidateCatalog(oversized, deadline.Token));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreTypeProfileRunner.ValidateCatalog(SafeCoreTypeProfileRunner.Catalog, cancelled.Token));
        return Task.CompletedTask;
    }

    private static Task RejectsIncompleteReportsAsync()
    {
        AssertEx.Equal("passed", SafeCoreTypeProfileRunner.ReportStatus(96, 96, 0, 0, true, false, null));
        AssertEx.Equal("blocked", SafeCoreTypeProfileRunner.ReportStatus(96, 95, 0, 1, true, false, null));
        AssertEx.Equal("blocked", SafeCoreTypeProfileRunner.ReportStatus(96, 96, 0, 0, false, false, null));
        AssertEx.Equal("blocked", SafeCoreTypeProfileRunner.ReportStatus(96, 96, 0, 0, true, true, null));
        AssertEx.Equal("failed", SafeCoreTypeProfileRunner.ReportStatus(96, 95, 1, 0, true, false, null));
        AssertEx.Equal("failed", SafeCoreTypeProfileRunner.ReportStatus(96, 95, 0, 0, true, false, null));
        AssertEx.Equal("failed", SafeCoreTypeProfileRunner.ReportStatus(96, 96, 0, 0, true, false, "owned directory remains"));
        AssertEx.Equal("failed", SafeCoreTypeProfileRunner.ReportStatus(0, 0, 0, 0, true, false, null));
        return Task.CompletedTask;
    }
}
