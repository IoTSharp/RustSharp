using RustSharp.Compiler;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreMirEnumTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("MIR v2 enum unit tuple and named variants execute through calls", ConstructorsAsync),
        new("MIR v2 enum payload construction preserves source evaluation order", ConstructionOrderAsync),
        new("MIR v2 enum matching tests payload only after its tag", PayloadConditionAsync),
        new("MIR v2 enum matching supports borrowed payloads", BorrowedPatternsAsync),
        new("MIR v2 enum matching writes through mutable payload bindings", MutablePatternsAsync),
        new("MIR v2 enum nested payloads and rest patterns execute", NestedPatternsAsync),
        new("MIR v2 enum payloads store promoted static reference handles", StoredReferencesAsync),
        new("MIR v2 enum explicit discriminants retain evaluated values", DiscriminantsAsync),
        new("MIR v2 enum payload moves remain tracked", MoveAsync),
        new("MIR enum validation rejects forged layout tags and downcasts", ValidationAsync),
        new("MIR enum validation checks variant constructor fields", ConstructorValidationAsync),
    ];

    private static Task ConstructorsAsync() => RunAsync(
        "enum Event { Empty, Number(i32), Pair { left: i32, right: i32 } } " +
        "fn make(flag: bool) -> Event { if flag { Event::Number(7) } else { Event::Empty } } " +
        "fn read(value: Event) -> i32 { match value { Event::Empty => 0, Event::Number(n) => n, Event::Pair { left, right } => left + right } } " +
        "fn main() { println!(\"{}\", read(make(true))); println!(\"{}\", read(make(false))); " +
        "println!(\"{}\", read(Event::Pair { right: 5, left: 3 })); }", "7\n0\n8\n");

    private static Task ConstructionOrderAsync() => RunAsync(
        "enum Event { Empty, Pair { left: i32, right: i32 } } fn value(x: i32) -> i32 { println!(\"{}\", x); x } " +
        "fn main() { let event = Event::Pair { right: value(2), left: value(1) }; " +
        "match event { Event::Pair { left, right } => println!(\"{}\", left * 10 + right), Event::Empty => println!(\"empty\") }; }",
        "2\n1\n12\n");

    private static Task PayloadConditionAsync() => RunAsync(
        "enum Event { Empty, Number(i32), Pair(bool, i32) } fn read(value: Event) -> i32 { match value { " +
        "Event::Empty => 1, Event::Number(0) => 2, Event::Number(n) if n > 0 => n, Event::Number(_) => -1, " +
        "Event::Pair(true, 2) => 20, Event::Pair(_, n) => n } } fn main() { println!(\"{}\", read(Event::Empty)); " +
        "println!(\"{}\", read(Event::Number(0))); println!(\"{}\", read(Event::Number(-5))); " +
        "println!(\"{}\", read(Event::Pair(true, 2))); println!(\"{}\", read(Event::Pair(false, 9))); }", "1\n2\n-1\n20\n9\n");

    private static Task BorrowedPatternsAsync() => RunAsync(
        "enum Event { Empty, Number(i32) } fn read(value: &Event) -> i32 { match value { Event::Empty => 0, Event::Number(n) => *n } } " +
        "fn main() { let value = Event::Number(17); println!(\"{}\", read(&value)); println!(\"{}\", read(&value)); }", "17\n17\n");

    private static Task MutablePatternsAsync() => RunAsync(
        "enum Event { Empty, Number(i32) } fn change(value: &mut Event) { match value { Event::Empty => (), Event::Number(n) => *n += 4 }; } " +
        "fn main() { let mut value = Event::Number(3); change(&mut value); match value { Event::Empty => (), Event::Number(n) => println!(\"{}\", n) }; }", "7\n");

    private static Task NestedPatternsAsync() => RunAsync(
        "enum Inner { Empty, Number(i32) } enum Outer { Empty, Pair(i32, Inner, i32) } " +
        "fn read(value: Outer) -> i32 { match value { Outer::Pair(_, Inner::Number(n), ..) => n, _ => 0 } } " +
        "fn main() { println!(\"{}\", read(Outer::Pair(1, Inner::Number(8), 9))); println!(\"{}\", read(Outer::Empty)); }", "8\n0\n");

    private static Task StoredReferencesAsync() => RunAsync(
        "enum Value { Empty, Shared(&'static i32) } " +
        "fn read(value: Value) -> i32 { match value { Value::Empty => 0, Value::Shared(r) => *r } } " +
        "fn main() { println!(\"{}\", read(Value::Shared(&3))); println!(\"{}\", read(Value::Shared(&7))); }", "3\n7\n");

    private static Task DiscriminantsAsync()
    {
        const string source = "const START: isize = 2 + 2; enum Tag { First = START, Second, Third = 10, Fourth } fn main() { let x = Tag::Second; }";
        SafeCoreMirPipelineResult result = Analyze(source);
        AssertEx.True(result.IsSuccessful, Format(result.Diagnostics));
        SafeCoreMirAdtLayout layout = result.Mir!.Program!.AdtLayouts.Single();
        AssertEx.Equal("4,5,10,11", string.Join(',', layout.Variants.Select(static variant => variant.Discriminant)));
        AssertEx.True(result.MirSnapshot!.Contains("variant", StringComparison.Ordinal), "Snapshots must carry declared enum variants.");
        SafeCoreMirPipelineResult duplicate = Analyze("enum Tag { A = 2, B = 2 } fn main() {}");
        AssertEx.False(duplicate.IsSuccessful, "Duplicate discriminants must be rejected.");
        SafeCoreMirPipelineResult needsRepresentation = Analyze("enum Tag { A = 2, B(i32) } fn main() {}");
        AssertEx.False(needsRepresentation.IsSuccessful, "A data-carrying enum with explicit tags requires an integer repr attribute.");
        AssertEx.True(needsRepresentation.Diagnostics.Any(static item => item.Code == "RST2001"), Format(needsRepresentation.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task MoveAsync()
    {
        const string source = "struct Token(i32); enum Event { Empty, Token(Token) } fn take(value: Token) {} " +
            "fn main() { let event = Event::Token(Token(1)); match event { Event::Token(value) => { take(value); take(value); }, Event::Empty => () }; }";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CompilationResult result = CompilerDriver.Check(source, "enum-move.rs", CompilationProfile.SafeCoreMirV2, deadline.Token);
        AssertEx.False(result.Success, "Non-Copy variant payloads cannot be consumed twice.");
        AssertEx.True(result.Diagnostics.Any(static item => item.Code == SafeCoreOwnershipDiagnosticCodes.UseAfterMove), Format(result.Diagnostics));
        return Task.CompletedTask;
    }

    private static readonly SafeCoreType Integer = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.I32);
    private static readonly SafeCoreType Unit = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Unit);
    private static readonly SafeCoreType Choice = SafeCoreType.Adt("crate::Choice");
    private static readonly SafeCoreMirSource Source = new("enum-layout.rs", new(0, 10), 0, 10);
    private static SafeCoreMirAdtLayout Layout(int offset = 1, int firstTag = 0, int secondTag = 1) => new(Choice,
        [new("$tag", Integer, Source), new("$v1$0", Integer, Source)],
        [new("crate::Choice::Empty", firstTag, 1, [], Source),
         new("crate::Choice::Number", secondTag, offset, [new("0", Integer, Source)], Source)], Source);

    private static SafeCoreMirProgram Program(SafeCoreMirAdtLayout layout, SafeCoreMirOperand? returned = null,
        IReadOnlyList<SafeCoreMirStatement>? statements = null) => new(
        [new(0, "crate::main", returned?.Type ?? Unit,
            [new(0, "choice", Choice, SafeCoreMirLocalKind.Parameter, false, Source)],
            [new(0, statements ?? [], SafeCoreMirTerminator.Return(returned, Source), Source)], 0, Source)], [layout]);

    private static Task ValidationAsync()
    {
        SafeCoreMirPlace downcast = SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.Downcast(1)).Append(SafeCoreMirProjection.Field("0"));
        AssertEx.True(SafeCoreMirValidation.Validate(Program(Layout(), SafeCoreMirOperand.PlaceValue(downcast, Integer, Source))).IsSuccessful,
            "Declared variant payload downcasts must validate.");
        AssertEx.False(SafeCoreMirValidation.Validate(Program(Layout(offset: 0))).IsSuccessful, "Payload offsets cannot overlap the tag.");
        AssertEx.False(SafeCoreMirValidation.Validate(Program(Layout(secondTag: 0))).IsSuccessful, "Variant tags must be unique.");
        SafeCoreMirPlace missing = SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.Downcast(3)).Append(SafeCoreMirProjection.Field("0"));
        AssertEx.False(SafeCoreMirValidation.Validate(Program(Layout(), SafeCoreMirOperand.PlaceValue(missing, Integer, Source))).IsSuccessful,
            "Invalid variant ordinals must fail validation.");
        SafeCoreMirPlace physical = SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.Field("$v1$0"));
        AssertEx.False(SafeCoreMirValidation.Validate(Program(Layout(), SafeCoreMirOperand.PlaceValue(physical, Integer, Source))).IsSuccessful,
            "Enum payloads require an explicit downcast; physical fields are not source fields.");
        return Task.CompletedTask;
    }

    private static Task ConstructorValidationAsync()
    {
        SafeCoreMirRvalue correct = SafeCoreMirRvalue.Enum(1, [SafeCoreMirOperand.Constant(Integer, "5", Source)], Choice, Source);
        AssertEx.True(SafeCoreMirValidation.Validate(Program(Layout(), statements: [new(0, correct, Source)])).IsSuccessful,
            "Valid variant constructors must validate.");
        SafeCoreMirRvalue wrong = SafeCoreMirRvalue.Enum(0, [SafeCoreMirOperand.Constant(Integer, "5", Source)], Choice, Source);
        AssertEx.False(SafeCoreMirValidation.Validate(Program(Layout(), statements: [new(0, wrong, Source)])).IsSuccessful,
            "Unit variants must reject fabricated payload operands.");
        return Task.CompletedTask;
    }

    private static SafeCoreMirPipelineResult Analyze(string source) => SafeCoreMirPipeline.Analyze(source, "enum-source.rs", new()
    {
        EnableP1Extensions = true, EnableRepeatedArrays = true,
        RequireOwnershipEvidence = true, RequireCleanupEvidence = true, Timeout = TimeSpan.FromSeconds(10),
    });

    private static async Task RunAsync(string source, string expected)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RustSharp.Tests"));
        string directory = Path.Combine(root, "enum-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Directory.CreateDirectory(directory);
        try
        {
            string output = Path.Combine(directory, "enum.dll");
            CompilationResult result = CompilerDriver.Compile(source, "enum-runtime.rs", output,
                assemblyName: "EnumRuntime", profile: CompilationProfile.SafeCoreMirV2, cancellationToken: deadline.Token);
            AssertEx.True(result.Success, Format(result.Diagnostics));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [output], directory, TimeSpan.FromSeconds(10)), deadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded && !run.ProcessTreeCleanupIncomplete, run.StandardError);
            AssertEx.Equal(expected, run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            AssertEx.True(Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "Enum test cleanup must target only its unique owned directory.");
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Format(IReadOnlyList<Diagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(static item => item.Code + ": " + item.Message));
}
