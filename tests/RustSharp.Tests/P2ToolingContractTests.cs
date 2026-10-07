using System.Diagnostics;
using System.Text.Json.Nodes;
using RustSharp.Compiler;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class P2ToolingContractTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P2-08.01 freezes complete tooling scope and source provenance", ScopeAsync),
        new("P2-08.01 reconciles all 49 syntax outcomes and lossless token trivia", SyntaxCorpusAsync),
        new("P2-08.01 preserves nested comments documentation literals BOM and shebang foundations", TriviaAsync),
        new("P2-08.01 rejects changed schema version and fixed denominator", IdentityAsync),
        new("P2-08.01 rejects unknown and duplicate JSON properties", UnknownFieldsAsync),
        new("P2-08.01 rejects removed syntax families and documentation APIs", SurfaceAsync),
        new("P2-08.01 rejects every omitted cache identity input", CacheInputsAsync),
        new("P2-08.01 rejects duplicate cache inputs and unsafe determinism claims", CacheDuplicatesAsync),
        new("P2-08.01 rejects weakened resource limits", LimitsAsync),
        new("P2-08.01 rejects reduced or substituted case inventory", CasesAsync),
        new("P2-08.01 rejects changed frozen failure diagnostics", DiagnosticsAsync),
        new("P2-08.01 rejects corpus path traversal and duplicate fixtures", PathsAsync),
        new("P2-08.01 rejects stale syntax baseline and source hashes", SourceHashesAsync),
        new("P2-08.01 rejects changed source outcomes and corpus substitution", CorpusOutcomesAsync),
        new("P2-08.01 accepts reordered set inputs without reducing scope", ReorderedSetsAsync),
        new("P2-08.01 bounds malformed oversized and deeply nested JSON", MalformedAsync),
        new("P2-08.01 supports cancellation before parse and corpus IO", CancellationAsync),
        new("P2-08.01 uses explicit portable provenance text hashing", HashEncodingAsync),
    ];

    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static string ManifestText => File.ReadAllText(Path.Combine(Root, "tools/RustSharp.Conformance/fixtures", ToolingContract.ManifestFileName));
    private static JsonObject ManifestNode() => JsonNode.Parse(ManifestText)!.AsObject();
    private static Task ScopeAsync()
    {
        ToolingContractManifest manifest = ToolingContract.Parse(ManifestText);
        ToolingContract.ValidateCorpus(manifest, Root);
        AssertEx.Equal(91, manifest.Denominator);
        AssertEx.Equal(49, manifest.Corpus.Count);
        AssertEx.Equal(42, manifest.Cases.Count);
        AssertEx.Equal(34, manifest.Corpus.Count(item => item.Expected == "format-preserve"));
        AssertEx.Equal(15, manifest.Corpus.Count(item => item.Expected == "format-reject"));
        AssertEx.Equal(10, manifest.Cases.Count(item => item.Area == "formatter"));
        AssertEx.Equal(12, manifest.Cases.Count(item => item.Area == "documentation"));
        AssertEx.Equal(10, manifest.Cases.Count(item => item.Area == "incremental-keys"));
        AssertEx.Equal(10, manifest.Cases.Count(item => item.Area == "incremental-artifacts"));
        AssertEx.Equal(ToolingContract.SourceHash(ManifestText), manifest.ManifestSha256);
        return Task.CompletedTask;
    }

    private static Task SyntaxCorpusAsync()
    {
        ToolingContractManifest manifest = ToolingContract.Parse(ManifestText);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        long started = Stopwatch.GetTimestamp();
        // Trial the same bounded parse on a tiny source before the 49-fixture batch.
        AssertEx.True(SafeCoreSyntax.Parse("fn main() {}", "trial.rs", null, cancellation.Token).IsSuccessful, "Tiny syntax trial failed.");
        int parsed = 0;
        foreach (ToolingCorpusCase item in manifest.Corpus)
        {
            Guard(started, cancellation.Token);
            string source = File.ReadAllText(Path.Combine(Root, item.File));
            SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source, item.File,
                new SafeCoreSyntaxOptions { Timeout = TimeSpan.FromSeconds(2) }, cancellation.Token);
            AssertEx.Equal(item.Expected == "format-preserve", result.IsSuccessful, item.Id);
            if (item.DiagnosticCode is not null)
                AssertEx.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == item.DiagnosticCode &&
                    result.GetText(diagnostic.Span) == item.DiagnosticText), "Frozen rejection source span changed: " + item.Id);
            AssertLossless(source, result.LexResult);
            parsed++;
        }
        AssertEx.Equal(49, parsed);
        return Task.CompletedTask;
    }

    private static Task TriviaAsync()
    {
        string[] sources =
        ["fn f(){/* outer /* inner */ tail */let x=1;} // tail\n",
            "//! module\n/// function\nfn f(){/** inner */let x=r#\"a\r\nb\"#;}\n",
            "\uFEFF#!/usr/bin/env rsc\nfn main(){let text=\"<script>&\\\"\";}\n"];
        long started = Stopwatch.GetTimestamp();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (int index = 0; index < sources.Length; index++)
        {
            Guard(started, cancellation.Token);
            RustLexResult lexed = RustLexer.Lex(sources[index], "trivia.rs",
                new RustLexerOptions { Timeout = TimeSpan.FromSeconds(2) }, cancellation.Token);
            AssertEx.False(lexed.IsTruncated, "Trivia foundation must retain the complete source.");
            AssertLossless(sources[index], lexed);
        }
        return Task.CompletedTask;
    }

    private static void AssertLossless(string source, RustLexResult result)
    {
        AssertEx.True(result.Tokens.Count <= 250_000 && result.Trivia.Count <= 500_000, "Corpus retention exceeded the frozen token/trivia budget.");
        var parts = result.Tokens.Select(item => (item.Span.Start, item.Text))
            .Concat(result.Trivia.Select(item => (item.Span.Start, item.Text))).OrderBy(item => item.Start).ToArray();
        AssertEx.Equal(source, string.Concat(parts.Select(item => item.Text)), "Lossless token/trivia foundation changed.");
    }

    private static Task IdentityAsync() => MutationsAsync(
    [node => node["schemaVersion"] = 2, node => node["version"] = 2, node => node["profile"] = "tooling-v2",
        node => node["denominator"] = 90, node => node["rids"] = new JsonArray("win-x64"),
        node => node["dependencies"] = new JsonArray("P1-02")]);
    private static Task UnknownFieldsAsync()
    {
        RejectText(ManifestText.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"schemaVersion\": 1,", StringComparison.Ordinal));
        return MutationsAsync([node => node["unexpected"] = true, node => node["formatter"]!["unexpected"] = "ignored",
            node => node["cases"]![0]!["skip"] = true]);
    }
    private static Task SurfaceAsync() => MutationsAsync(
    [node => node["formatter"]!["syntaxFamilies"]!.AsArray().RemoveAt(0),
        node => node["formatter"]!["preservation"]!.AsArray().RemoveAt(0),
        node => node["documentation"]!["itemKinds"]!.AsArray().RemoveAt(0),
        node => node["documentation"]!["apis"]!.AsArray().RemoveAt(0),
        node => node["documentation"]!["links"]!.AsArray().RemoveAt(7),
        node => node["documentation"]!["rendering"] = "Allow raw script HTML"]);
    private static Task CacheInputsAsync()
    {
        int count = ManifestNode()["cache"]!["keyInputs"]!.AsArray().Count;
        AssertEx.Equal(26, count);
        long started = Stopwatch.GetTimestamp();
        for (int index = 0; index < count; index++)
        {
            Guard(started, CancellationToken.None);
            JsonObject node = ManifestNode();
            node["cache"]!["keyInputs"]!.AsArray().RemoveAt(index);
            RejectText(node.ToJsonString());
        }
        return Task.CompletedTask;
    }
    private static Task CacheDuplicatesAsync() => MutationsAsync(
    [node => node["cache"]!["keyInputs"]![1] = "cacheSchemaVersion",
        node => node["cache"]!["keyInputs"]![0] = "mtime",
        node => node["cache"]!["invalidation"] = "Changed source only, never reverse dependents",
        node => node["artifacts"]!["determinismFields"]!.AsArray().RemoveAt(1),
        node => node["artifacts"]!["publication"] = "Partial outputs are accepted",
        node => node["testTargets"]!["contract"] = "test:unregistered"]);
    private static Task LimitsAsync()
    {
        string[] fields = ["timeoutMilliseconds", "maximumSourceCharacters", "maximumTokens", "maximumTrivia", "maximumFiles",
            "maximumDocItems", "maximumLinks", "maximumCacheEntries", "maximumOutputBytes", "maximumWriters", "maximumOperations"];
        long started = Stopwatch.GetTimestamp();
        foreach (string field in fields)
        {
            Guard(started, CancellationToken.None);
            JsonObject node = ManifestNode();
            node["limits"]![field] = 0;
            RejectText(node.ToJsonString());
        }
        return Task.CompletedTask;
    }
    private static Task CasesAsync() => MutationsAsync(
    [node => node["cases"]!.AsArray().RemoveAt(0),
        node => node["cases"]![0]!["id"] = "format-budget",
        node => node["cases"]![0]!["id"] = "format-unknown",
        node => node["cases"]![0]!["area"] = "documentation",
        node => node["cases"]![0]!["scenario"] = "TODO",
        node => node["cases"]![0]!["expected"] = "skipped"]);
    private static Task DiagnosticsAsync() => MutationsAsync(
    [node => node["cases"]![7]!["diagnosticCode"] = "RSTF9999",
        node => node["cases"]![16]!["diagnosticCode"] = "RSTD9999",
        node => node["cases"]![20]!["diagnosticCode"] = "RSTD1001",
        node => node["cases"]![36]!["diagnosticCode"] = "RSTI9999"]);
    private static Task PathsAsync() => MutationsAsync(
    [node => node["formatter"]!["corpus"]![0]!["file"] = "tools/RustSharp.Conformance/fixtures/../secret.rs",
        node => node["formatter"]!["corpus"]![0]!["file"] = "D:\\outside.rs",
        node => node["formatter"]!["corpus"]![1]!["file"] = node["formatter"]!["corpus"]![0]!["file"]!.GetValue<string>(),
        node => node["formatter"]!["corpus"]![1]!["id"] = node["formatter"]!["corpus"]![0]!["id"]!.GetValue<string>()]);
    private static Task SourceHashesAsync()
    {
        JsonObject node = ManifestNode();
        node["syntaxBasis"]!["sha256"] = new string('0', 64);
        RejectCorpus(ToolingContract.Parse(node.ToJsonString()));
        node = ManifestNode();
        node["formatter"]!["corpus"]![0]!["sourceSha256"] = new string('0', 64);
        RejectCorpus(ToolingContract.Parse(node.ToJsonString()));
        return Task.CompletedTask;
    }
    private static Task CorpusOutcomesAsync()
    {
        ToolingContractManifest manifest = ToolingContract.Parse(ManifestText);
        var cases = manifest.Corpus.ToArray();
        cases[0] = cases[0] with { Id = "unknown-substitution" };
        RejectCorpus(manifest with { Corpus = cases });
        cases = manifest.Corpus.ToArray();
        cases[2] = cases[2] with { DiagnosticText = "fn" };
        RejectCorpus(manifest with { Corpus = cases });
        cases = manifest.Corpus.ToArray();
        cases[0] = cases[0] with { Expected = "format-reject" };
        RejectCorpus(manifest with { Corpus = cases });
        return Task.CompletedTask;
    }
    private static Task ReorderedSetsAsync()
    {
        JsonObject node = ManifestNode();
        node["rids"] = new JsonArray("linux-x64", "win-x64");
        node["cache"]!["keyInputs"] = new JsonArray(node["cache"]!["keyInputs"]!.AsArray().Reverse().Select(item => item!.DeepClone()).ToArray());
        ToolingContractManifest manifest = ToolingContract.Parse(node.ToJsonString());
        ToolingContract.ValidateCorpus(manifest, Root);
        AssertEx.Equal(91, manifest.Denominator);
        return Task.CompletedTask;
    }
    private static Task MalformedAsync()
    {
        RejectText("{"); RejectText("[]"); RejectText(new string(' ', 131_073));
        RejectText(new string('[', 18) + "0" + new string(']', 18));
        return Task.CompletedTask;
    }
    private static Task CancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => ToolingContract.Parse(ManifestText, cancellation.Token));
        ToolingContractManifest manifest = ToolingContract.Parse(ManifestText);
        AssertEx.Throws<OperationCanceledException>(() => ToolingContract.ValidateCorpus(manifest, "Z:/not-read", cancellation.Token));
        return Task.CompletedTask;
    }
    private static Task HashEncodingAsync()
    {
        AssertEx.Equal(ToolingContract.SourceHash("hello\n"), ToolingContract.SourceHash("\uFEFFhello\r\n"));
        AssertEx.False(ToolingContract.SourceHash("hello\rworld") == ToolingContract.SourceHash("hello\nworld"), "Bare CR is semantic source content.");
        AssertEx.False(ToolingContract.SourceHash("hello") == ToolingContract.SourceHash("hello\n"), "Final newline remains part of provenance.");
        return Task.CompletedTask;
    }
    private static Task MutationsAsync(IReadOnlyList<Action<JsonObject>> mutations)
    {
        AssertEx.True(mutations.Count is > 0 and <= 8, "Mutation batch must remain bounded.");
        long started = Stopwatch.GetTimestamp();
        foreach (Action<JsonObject> mutate in mutations)
        {
            Guard(started, CancellationToken.None);
            JsonObject node = ManifestNode(); mutate(node); RejectText(node.ToJsonString());
        }
        return Task.CompletedTask;
    }
    private static void RejectText(string json) => AssertEx.Equal(ToolingContract.InvalidManifest,
        AssertEx.Throws<ToolingContractException>(() => ToolingContract.Parse(json)).Code);
    private static void RejectCorpus(ToolingContractManifest manifest) => AssertEx.Equal(ToolingContract.CorpusMismatch,
        AssertEx.Throws<ToolingContractException>(() => ToolingContract.ValidateCorpus(manifest, Root)).Code);
    private static void Guard(long started, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        AssertEx.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(20), "Bounded tooling contract batch exceeded its deadline.");
    }
}
