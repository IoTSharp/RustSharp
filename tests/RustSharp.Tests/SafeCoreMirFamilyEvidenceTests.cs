using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text;
using RustSharp.CodeGen.IL;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreMirFamilyEvidenceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("MIR families preserve deterministic MIR LIR PE PDB and original source mappings", DeterminismAsync),
        new("MIR families enforce work depth time cancellation and no partial publication", BudgetsAsync),
    ];

    private const string Combined = """
        // Original document: enum payloads, captures, slices and promoted storage.
        enum Answer { Empty, Number(i32), Static(&'static i32) }
        fn choose(values: &[i32], flag: bool) -> Answer {
            if flag { Answer::Number(values[0]) } else { Answer::Static(&7) }
        }
        fn main() {
            let mut values = [1, 2, 3];
            let mut change = |value: i32| { values[0] += value; };
            change(4);
            let selected = &values[1..];
            let answer = match choose(selected, true) {
                Answer::Empty => 0, Answer::Number(value) => value, Answer::Static(value) => *value
            };
            println!("{}", answer);
        }
        """;

    private static Task DeterminismAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        const string path = "original-families.rs";
        SafeCoreMirPipelineResult first = Analyze(Combined, path, deadline.Token);
        SafeCoreMirPipelineResult second = Analyze(Combined, path, deadline.Token);
        AssertEx.True(first.IsSuccessful, Format(first.Diagnostics));
        AssertEx.True(second.IsSuccessful, Format(second.Diagnostics));
        AssertEx.Equal(first.MirSnapshot!, second.MirSnapshot!);
        SafeCoreClrResult firstLir = SafeCoreMirClrLowering.Lower(first.Mir!.Program!, deadline.Token);
        SafeCoreClrResult secondLir = SafeCoreMirClrLowering.Lower(second.Mir!.Program!, deadline.Token);
        AssertEx.True(firstLir.IsSuccessful, Format(firstLir.Diagnostics));
        AssertEx.True(secondLir.IsSuccessful, Format(secondLir.Diagnostics));
        AssertEx.Equal(LirSnapshot(firstLir, deadline.Token), LirSnapshot(secondLir, deadline.Token));
        GeneratedAssembly firstImage = ClrLirAssemblyEmitter.EmitProgram(firstLir, "FamilyEvidence", Combined, path,
            "FamilyEvidence.pdb", cancellationToken: deadline.Token);
        GeneratedAssembly secondImage = ClrLirAssemblyEmitter.EmitProgram(secondLir, "FamilyEvidence", Combined, path,
            "FamilyEvidence.pdb", cancellationToken: deadline.Token);
        AssertEx.True(firstImage.PeImage.AsSpan().SequenceEqual(secondImage.PeImage), "Combined family PE bytes must be deterministic.");
        AssertEx.True(firstImage.PdbImage!.AsSpan().SequenceEqual(secondImage.PdbImage), "Combined family PDB bytes must be deterministic.");
        using var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(firstImage.PdbImage!, writable: false));
        MetadataReader reader = provider.GetMetadataReader();
        AssertEx.Equal(1, reader.Documents.Count);
        Document document = reader.GetDocument(reader.Documents.Single());
        AssertEx.Equal(path, reader.GetString(document.Name));
        AssertEx.True(SHA256.HashData(Encoding.UTF8.GetBytes(Combined)).AsSpan().SequenceEqual(reader.GetBlobBytes(document.Hash)),
            "The original document checksum must survive every family lowering pass.");
        int visible = 0;
        int visitedMethods = 0;
        foreach (MethodDebugInformationHandle handle in reader.MethodDebugInformation)
        {
            deadline.Token.ThrowIfCancellationRequested();
            AssertEx.True(++visitedMethods <= 128, "Generated method debug evidence must remain bounded.");
            MethodDebugInformation method = reader.GetMethodDebugInformation(handle);
            int visitedPoints = 0;
            foreach (SequencePoint point in method.GetSequencePoints())
            {
                deadline.Token.ThrowIfCancellationRequested();
                AssertEx.True(++visitedPoints <= 4096, "Sequence-point traversal must remain bounded.");
                if (point.IsHidden) continue;
                DocumentHandle pointDocument = point.Document.IsNil ? method.Document : point.Document;
                AssertEx.Equal(path, reader.GetString(reader.GetDocument(pointDocument).Name));
                AssertEx.True(point.StartLine >= 1 && point.EndLine >= point.StartLine && point.EndLine <= 16,
                    "Introduced CLR methods must map to original source lines.");
                visible++;
            }
        }
        AssertEx.True(visible >= 2, "Both original Rust functions must retain visible sequence points.");
        return Task.CompletedTask;
    }

    private static string LirSnapshot(SafeCoreClrResult result, CancellationToken token)
    {
        var builder = new StringBuilder();
        int instructions = 0;
        foreach (ClrLirMethod method in result.Methods)
        {
            token.ThrowIfCancellationRequested();
            builder.Append(method.Name).Append(':').Append(method.ReturnType).Append('(').AppendJoin(',', method.Parameters).AppendLine(")");
            foreach (ClrLirLocal local in method.Locals) builder.AppendLine(local.ToString());
            foreach (ClrLirBlock block in method.Blocks)
            {
                token.ThrowIfCancellationRequested();
                builder.AppendLine(block.Label);
                foreach (ClrLirInstruction instruction in block.Instructions)
                {
                    token.ThrowIfCancellationRequested();
                    AssertEx.True(++instructions <= 100_000, "LIR evidence formatting must remain bounded.");
                    builder.AppendLine(instruction.ToString());
                }
            }
        }
        return builder.ToString();
    }

    private static Task BudgetsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string[] cases =
        [
            "enum E { A, B(i32) } fn main() { let value = E::B(2); match value { E::A => (), E::B(x) => println!(\"{}\", x) }; }",
            "fn read(values: &[i32]) -> i32 { values[0] } fn main() { let values = [1, 2]; println!(\"{}\", read(&values[1..])); }",
            "fn constant() -> &'static i32 { &7 } fn main() { println!(\"{}\", *constant()); }",
            "fn main() { let mut owner = 1; let mut change = || { owner += 1; }; change(); println!(\"{}\", owner); }",
            "fn choose(pair:(i32,i32))->i32 { match pair { (x,_) | (_,x) if x>1=>x, _=>0 } } fn main(){ println!(\"{}\",choose((0,2))); }",
        ];
        SafeCoreMirLoweringOptions[] limits =
        [
            new() { EnableP1Extensions = true, MaximumOperations = 1 },
            new() { EnableP1Extensions = true, MaximumNestingDepth = 1 },
            new() { EnableP1Extensions = true, MaximumLocalsPerFunction = 1 },
            new() { EnableP1Extensions = true, Timeout = TimeSpan.FromTicks(1) },
        ];
        for (int caseIndex = 0; caseIndex < cases.Length; caseIndex++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            SafeCoreMirPipelineResult baseline = Analyze(cases[caseIndex], "family-budget.rs", deadline.Token);
            AssertEx.True(baseline.IsSuccessful, Format(baseline.Diagnostics));
            for (int limitIndex = 0; limitIndex < limits.Length; limitIndex++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                SafeCoreMirLoweringResult limited = SafeCoreMirLowering.Lower(baseline.Types!, limits[limitIndex], deadline.Token);
                AssertEx.True(limited.IsTruncated && limited.Program is null, "Every family must reject exhausted budgets without publishing partial MIR.");
                AssertEx.True(limited.Diagnostics.All(static diagnostic => diagnostic.Code == SafeCoreMirLowering.LimitReached), Format(limited.Diagnostics));
            }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            AssertEx.Throws<OperationCanceledException>(() => SafeCoreMirLowering.Lower(baseline.Types!,
                new() { EnableP1Extensions = true }, cancelled.Token));
        }
        return Task.CompletedTask;
    }

    private static SafeCoreMirPipelineResult Analyze(string source, string path, CancellationToken token) =>
        SafeCoreMirPipeline.Analyze(source, path, new()
        {
            EnableP1Extensions = true, EnableRepeatedArrays = true, RequireOwnershipEvidence = true,
            RequireCleanupEvidence = true, Timeout = TimeSpan.FromSeconds(10), CancellationToken = token,
        });
    private static string Format(IReadOnlyList<Diagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(static item => item.Code + ": " + item.Message));
}
