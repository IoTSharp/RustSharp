using System.Diagnostics;
using RustSharp.Compiler;
using RustSharp.Semantics;

namespace RustSharp.Tests;

internal static class SafeCoreMirReferenceStorageTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("MIR stored references reject slot and aggregate alias violations", () => CheckAsync(false,
            "fn main() { let first=1; let second=2; let mut reference=&first; let nested=&reference; reference=&second; println!(\"{}\", **nested); }",
            "fn main() { let mut owner=1; let mut reference=&mut owner; let nested=&mut reference; owner=2; println!(\"{}\", **nested); }",
            "fn main() { let first=1; let second=2; let mut tuple=(&first,); let borrowed=&tuple; tuple.0=&second; println!(\"{}\", *borrowed.0); }",
            "fn main() { let mut owner=1; let tuple=(&mut owner,); let borrowed=&tuple.0; let moved=tuple; println!(\"{}\", **borrowed); }")),
        new("MIR stored references reject partial moves and joined aliases", () => CheckAsync(false,
            "fn main() { let mut first=1; let mut second=2; let mut tuple=(&mut first,); let moved=tuple; tuple.0=&mut second; }",
            "fn bad(owner:&mut i32)->(&mut i32,&mut i32) { (owner,owner) } fn main() {}",
            "fn main() { let mut first=1; let second=2; let choose=true; let selected=if choose {&first} else {&second}; let tuple=(selected,); first=3; println!(\"{}\", *tuple.0); }",
            "fn main() { let mut first=1; let second=2; let references=[&first,&second]; let index:usize=1; let selected=references[index]; first=3; println!(\"{}\", *selected); }")),
        new("MIR stored references allow projected rebinding and independent field liveness", () => CheckAsync(true,
            "fn main() { let first=1; let second=2; let mut tuple=(&first,); let borrowed=&mut tuple; borrowed.0=&second; println!(\"{}\", *borrowed.0); }",
            "fn main() { let mut first=1; let second=2; let references=[&first,&second]; let selected=references[1]; first=3; println!(\"{}\", *selected); println!(\"{}\", first); }")),
        new("MIR stored references keep element loans through borrowed containers and dynamic reborrows", () => CheckAsync(false,
            "fn main() { let mut owner=1; let references=[&owner]; let view=&references; owner=2; println!(\"{}\", *view[0]); }",
            "fn main() { let mut first=1; let mut second=2; let mut references=[&mut first,&mut second]; let index:usize=1; let borrowed=&mut *references[index]; first=3; println!(\"{}\", *borrowed); }")),
        new("MIR stored references permit disjoint fixed ranges and normalize nested indices", () => CheckAsync(true,
            "fn main() { let mut values=[1,2,3]; let [first, rest @ ..]=&mut values; *first=4; rest[0]=5; println!(\"{}\", *first+rest[0]); }")),
        new("MIR stored references reject overlapping fixed ranges", () => CheckAsync(false,
            "fn main() { let mut values=[1,2,3]; let view=&mut values; let first=&mut view[0..2]; let rest=&mut view[1..3]; first[1]=4; rest[0]=5; }",
            "fn main() { let mut values=[1,2,3]; let view=&mut values; let rest=&mut view[1..3]; let element=&mut rest[0]; view[1]=4; println!(\"{}\", *element); }",
            "fn main() { let mut values=[1,2,3]; let view=&mut values; let first=&mut view[0..1]; let rest=&mut view[1..3]; first[0]=4; rest[0]=5; println!(\"{}\", first[0]+rest[0]); }")),
        new("MIR stored references reject forged fixed range ownership evidence", RangeEvidenceAsync),
        new("MIR stored references preserve inactive enum proofs and reject forged branch pruning", InactiveEnumEvidenceAsync),
        new("MIR stored references reject moved enums despite optional payloads", () => CheckAsync(false,
            "enum Value { Number(i32), Ref(&'static i32) } fn main() { let value=Value::Number(1); let moved=value; match value { Value::Number(n)=>println!(\"{}\",n),Value::Ref(r)=>println!(\"{}\",*r) }; }")),
    ];

    private static Task InactiveEnumEvidenceAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        SafeCoreMirPipelineResult result = SafeCoreMirPipeline.Analyze(
            "enum Value { Number(i32), Ref(&'static i32) } fn main() { let value=Value::Number(9); match value { Value::Number(n)=>println!(\"{}\",n), Value::Ref(r)=>println!(\"{}\",*r) }; }",
            "inactive-proof.rs", new() { EnableP1Extensions = true, RequireOwnershipEvidence = true,
                Timeout = TimeSpan.FromSeconds(4), CancellationToken = deadline.Token });
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        SafeCoreOwnershipProgram proof = result.Ownership!.Program!;
        SafeCoreOwnershipFunction function = proof.Functions.Single();
        AssertEx.True(function.Blocks.Count <= 32 && function.Locals.Count <= 64, "The inactive enum proof fixture has bounded blocks and slots.");
        AssertEx.True(function.Blocks.SelectMany(block => block.Instructions).Any(instruction => instruction.EnumVariantReferenceSlots is { Count: > 0 }),
            "The fixture requires a discriminant comparison with checked reference-slot facts.");
        SafeCoreMirOwnershipOptions options = new() { Timeout = TimeSpan.FromSeconds(3), CancellationToken = deadline.Token, InferNonLexicalLifetimes = true };
        AssertEx.True(SafeCoreMirOwnershipAdapter.Analyze(result.Mir!.Program!, proof, options).IsSuccessful,
            "Exact inactive-variant and branch facts must round trip.");
        var forged = new SafeCoreOwnershipProgram([new SafeCoreOwnershipFunction(function.Name, function.Locals, function.Scopes,
            function.Blocks.Select(block => new SafeCoreOwnershipBlock(block.Id, block.ScopeId,
                block.Instructions.Select(instruction => instruction with { EnumVariantReferenceSlots = null }).ToArray(),
                block.Terminator, block.Source)).ToArray(), function.EntryBlockId, function.PanicStrategy, function.Source)]);
        SafeCoreMirOwnershipResult rejected = SafeCoreMirOwnershipAdapter.Analyze(result.Mir.Program!, forged, options);
        AssertEx.True(!rejected.IsSuccessful && rejected.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreMirOwnershipAdapter.EvidenceMismatch),
            "Explicit evidence cannot erase the source of branch-pruning facts.");

        SafeCoreMirProgram mir = result.Mir.Program!;
        SafeCoreMirFunction original = mir.Functions.Single();
        SafeCoreMirBlock payload = original.Blocks.Single(block => block.Statements.Any(statement => statement.Value.Operands.Any(operand =>
            operand.Place?.Projections.Any(projection => projection.Kind == SafeCoreMirProjectionKind.Downcast && projection.Index == 1) == true)));
        SafeCoreMirBlock[] blocks = original.Blocks.Select(block => block.Id == original.EntryBlockId
            ? new SafeCoreMirBlock(block.Id, block.Statements, SafeCoreMirTerminator.Goto(payload.Id, block.Terminator.Source), block.Source)
            : block).ToArray();
        var invalid = new SafeCoreMirProgram([new SafeCoreMirFunction(original.Id, original.Name, original.ReturnType,
            original.Locals, blocks, original.EntryBlockId, original.Source)], mir.AdtLayouts);
        SafeCoreMirOwnershipResult badDowncast = SafeCoreMirOwnershipAdapter.Analyze(invalid, options);
        AssertEx.False(badDowncast.IsSuccessful, "A raw inactive downcast without its tag branch must remain invalid.");
        AssertEx.True(badDowncast.Ownership?.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.UseAfterMove) == true,
            "Removing the discriminant guard must expose the uninitialized payload instead of silently pruning the invalid block.");
        return Task.CompletedTask;
    }

    private static Task RangeEvidenceAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        SafeCoreMirPipelineResult result = SafeCoreMirPipeline.Analyze(
            "fn main() { let values=[1,2,3]; let [first, part @ ..]=&values; println!(\"{}\", *first+part[0]); }", "range-proof.rs",
            new() { EnableP1Extensions = true, RequireOwnershipEvidence = true, Timeout = TimeSpan.FromSeconds(4), CancellationToken = deadline.Token });
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        SafeCoreOwnershipProgram proof = result.Ownership!.Program!;
        SafeCoreOwnershipFunction function = proof.Functions.Single();
        SafeCoreOwnershipBlock block = function.Blocks.Single();
        AssertEx.True(block.Instructions.Count <= 64, "The fixed range evidence fixture is bounded to 64 effects.");
        int index = block.Instructions.ToList().FindIndex(instruction =>
            instruction.Place?.Projections.Any(projection => projection.Kind == SafeCoreOwnershipProjectionKind.ArrayRange) == true);
        AssertEx.True(index >= 0, "The fixed range requires exact ownership projection evidence.");
        SafeCoreMirOwnershipOptions options = new() { Timeout = TimeSpan.FromSeconds(3), CancellationToken = deadline.Token, InferNonLexicalLifetimes = true };
        SafeCoreMirOwnershipResult roundtrip = SafeCoreMirOwnershipAdapter.Analyze(result.Mir!.Program!, proof, options);
        AssertEx.True(roundtrip.IsSuccessful, "Exact range evidence must round trip: " +
            string.Join("; ", roundtrip.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        for (int mutation = 0; mutation < 3; mutation++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            SafeCoreOwnershipInstruction[] effects = block.Instructions.ToArray();
            SafeCoreOwnershipInstruction original = effects[index];
            SafeCoreOwnershipPlace place = original.Place!;
            effects[index] = mutation switch
            {
                0 => original with { Place = SafeCoreOwnershipPlace.Root(place.LocalId) },
                1 => original with { Place = new(place.LocalId, place.Projections.Select(projection =>
                    projection.Kind == SafeCoreOwnershipProjectionKind.ArrayRange ? projection with { EndIndex = projection.EndIndex + 1 } : projection).ToArray()) },
                _ => original,
            };
            if (mutation == 2) Array.Reverse(effects);
            var forged = new SafeCoreOwnershipProgram([new SafeCoreOwnershipFunction(function.Name, function.Locals, function.Scopes,
                [new SafeCoreOwnershipBlock(block.Id, block.ScopeId, effects, block.Terminator, block.Source)],
                function.EntryBlockId, function.PanicStrategy, function.Source)]);
            SafeCoreMirOwnershipResult rejected = SafeCoreMirOwnershipAdapter.Analyze(result.Mir.Program!, forged, options);
            AssertEx.False(rejected.IsSuccessful, "A forged range or effect order cannot replace MIR evidence.");
            AssertEx.True(rejected.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreMirOwnershipAdapter.EvidenceMismatch),
                "Forged fixed ranges must fail evidence correlation.");
        }
        return Task.CompletedTask;
    }

    private static Task CheckAsync(bool expected, params string[] fixtures)
    {
        AssertEx.True(fixtures.Length is > 0 and <= 4, "Reference storage fixture groups are bounded to four sources.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var clock = Stopwatch.StartNew();
        for (int index = 0; index < fixtures.Length; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(20), "Reference storage fixtures exceeded their deadline.");
            CompilationResult result = CompilerDriver.Check(fixtures[index], "reference-storage.rs", CompilationProfile.SafeCoreMirV2, deadline.Token);
            string diagnostics = string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message));
            AssertEx.Equal(expected, result.Success, $"Fixture {index + 1}: {fixtures[index]}\n{diagnostics}");
            if (!expected)
                AssertEx.True(result.Diagnostics.Any(diagnostic => diagnostic.Code is "RSO1001" or "RSO1002" or "RSO1003" or "RSO1004"),
                    "The negative fixture must fail ownership rather than an unsupported syntax boundary: " + diagnostics);
        }
        return Task.CompletedTask;
    }
}
