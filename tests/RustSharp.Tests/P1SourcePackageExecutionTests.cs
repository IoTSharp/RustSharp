using RustSharp.Compiler;
using RustSharp.CodeGen.IL;
using RustSharp.Semantics;

namespace RustSharp.Tests;

internal static class P1SourcePackageExecutionTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1-09 source aggregate identity and move execute across packages", AggregateAsync),
        new("P1-09 source shared and mutable reference calls execute across packages", ReferencesAsync),
        new("P1-09 source slice calls preserve owner and mutation across packages", SlicesAsync),
        new("P1-09 imported source moves reject reuse before emission", RejectMoveAsync),
        new("P1-09 imported source borrow conflicts reject before emission", RejectBorrowAsync),
        new("P1-09 imported source Drop transfers and cleans up exactly once", DropAsync),
        new("P1-09 source tuple and fixed array use producer CLR layouts", StructuralAsync),
        new("P1-09 imported nominal types support source annotations and constructors", ConstructorsAsync),
        new("P1-09 source composite return references preserve each origin", CompositeAsync),
        new("P1-09 source composite parameter reference paths execute", CompositeParameterAsync),
        new("P1-09 source projected reference origins execute across packages", ProjectedAsync),
        new("P1-09 imported composite borrow retains ownership conflicts", RejectCompositeAsync),
        new("P1-09 producer unwind runs consumer Drop exactly once", UnwindAsync),
        new("P1-09 producer abort skips consumer Drop", AbortAsync),
        new("P1-09 imported static parameter accepts promoted reference", StaticAsync),
        new("P1-09 imported static parameter rejects caller stack reference", RejectStaticAsync),
        new("P1-09 source nominal identities reject another producer's same-name layout", NominalIdentityAsync),
        new("P1-09 source private aggregate fields reject consumer access", PrivateFieldsAsync),
        new("P1-09 source unsupported generic nominal exports reject before publication", GenericNominalAsync),
        new("P1-09 source enum variants construct and match across packages", EnumAsync),
        new("P1-09 source enum returns retain active static payload origins", EnumReferencesAsync),
        new("P1-09 source nested module types and calls preserve source paths", NestedAsync),
        new("P1-09 source private module exports remain inaccessible", PrivateModuleAsync),
    ];

    internal const string AggregateProducer = "pub struct Pair { pub left: i32, pub right: bool } pub fn make() -> Pair { Pair { left: 42, right: true } } pub fn consume(value: Pair) -> i32 { value.left } fn main() {}";
    private static Task AggregateAsync() => ExecuteAsync(AggregateProducer,
        "use SourceProducer::{make, consume}; fn main() { let pair = make(); println!(\"{}\", pair.right); println!(\"{}\", consume(pair)); }", "true\n42\n");
    private static Task ReferencesAsync() => ExecuteAsync(
        "pub fn identity(value: &i32) -> &i32 { value } pub fn add(value: &mut i32) { *value = *value + 1; } fn main() {}",
        "use SourceProducer::{identity, add}; fn main() { let mut value = 41; add(&mut value); let view = identity(&value); println!(\"{}\", *view); }", "42\n");
    private static Task SlicesAsync() => ExecuteAsync(
        "pub fn view(value: &[i32]) -> &[i32] { value } pub fn add(value: &mut [i32]) { value[0] = value[0] + 1; } fn main() {}",
        "use SourceProducer::{view, add}; fn main() { let mut values = [41, 7]; add(&mut values); let slice = view(&values); println!(\"{}\", slice[0]); println!(\"{}\", slice.len()); }", "42\n2\n");
    private static Task RejectMoveAsync() => ExecuteAsync(AggregateProducer,
        "use SourceProducer::{make, consume}; fn main() { let pair = make(); consume(pair); println!(\"{}\", pair.left); }", null);
    private static Task RejectBorrowAsync() => ExecuteAsync(
        "pub fn identity(value: &i32) -> &i32 { value } fn main() {}",
        "use SourceProducer::identity; fn main() { let mut value = 41; let view = identity(&value); value = 42; println!(\"{}\", *view); }", null);
    private static Task DropAsync() => ExecuteAsync(
        "pub struct Resource { pub value: i32 } impl Drop for Resource { fn drop(&mut self) { println!(\"drop\"); } } pub fn make() -> Resource { Resource { value: 42 } } pub fn consume(value: Resource) { println!(\"{}\", value.value); } fn main() {}",
        "use SourceProducer::{make, consume}; fn main() { let value = make(); consume(value); }", "42\ndrop\n");

    private static Task StructuralAsync() => ExecuteAsync(
        "pub fn pair() -> (i32, bool) { (42, true) } pub fn sum(value: (i32, bool)) -> i32 { value.0 } pub fn array() -> [i32; 2] { [40, 2] } pub fn add(value: [i32; 2]) -> i32 { value[0] + value[1] } fn main() {}",
        "use SourceProducer::{pair, sum, array, add}; fn main() { let tuple = pair(); println!(\"{}\", tuple.1); println!(\"{}\", sum(tuple)); let values = array(); println!(\"{}\", add(values)); }", "true\n42\n42\n");

    private static Task ConstructorsAsync() => ExecuteAsync(
        "pub struct Pair { pub left: i32, pub right: bool } pub struct Tuple(pub i32); pub struct Unit; pub fn read(value: Pair) -> i32 { value.left } pub fn tuple(value: Tuple) -> i32 { value.0 } pub fn unit(value: Unit) -> i32 { 42 } fn main() {}",
        "use SourceProducer::{Pair, Tuple, Unit, read, tuple, unit}; fn main() { let value: Pair = Pair { left: 42, right: true }; println!(\"{}\", read(value)); println!(\"{}\", tuple(Tuple(42))); println!(\"{}\", unit(Unit)); }", "42\n42\n42\n");

    private static Task CompositeAsync() => ExecuteAsync(
        "pub fn pair(value: &(i32, i32)) -> (&i32, &i32) { (&value.0, &value.1) } fn main() {}",
        "use SourceProducer::pair; fn main() { let owner = (40, 2); let view = pair(&owner); println!(\"{}\", *view.0 + *view.1); }", "42\n");

    private static Task CompositeParameterAsync() => ExecuteAsync(
        "pub fn first(value: (&i32, i32)) -> &i32 { value.0 } fn main() {}",
        "use SourceProducer::first; fn main() { let owner = 42; let view = first((&owner, 1)); println!(\"{}\", *view); }", "42\n");

    private static Task ProjectedAsync() => ExecuteAsync(
        "pub fn first(value: &(i32, i32)) -> &i32 { &value.0 } fn main() {}",
        "use SourceProducer::first; fn main() { let owner = (42, 1); let view = first(&owner); println!(\"{}\", *view); }", "42\n");

    private static Task RejectCompositeAsync() => ExecuteAsync(
        "pub fn pair(value: &(i32, i32)) -> (&i32, &i32) { (&value.0, &value.1) } fn main() {}",
        "use SourceProducer::pair; fn main() { let mut owner = (40, 2); let view = pair(&owner); owner.0 = 42; println!(\"{}\", *view.0); }", null);

    private const string PanicProducer = "pub fn fail() -> i32 { let value = 2147483647; value + 1 } fn main() {}";
    private const string PanicConsumer = "use SourceProducer::fail; struct Guard; impl Drop for Guard { fn drop(&mut self) { println!(\"drop\"); } } fn main() { let guard = Guard; println!(\"start\"); fail(); }";
    private static Task UnwindAsync() => ExecuteAsync(PanicProducer, PanicConsumer, "start\ndrop\n", expectedExit: -1);
    private static Task AbortAsync() => ExecuteAsync(PanicProducer, PanicConsumer, "start\n", SafeCorePanicStrategy.Abort, 134);
    private static Task StaticAsync() => ExecuteAsync(
        "pub fn read(value: &'static i32) -> i32 { *value } fn main() {}",
        "use SourceProducer::read; fn main() { println!(\"{}\", read(&42)); }", "42\n");
    private static Task RejectStaticAsync() => ExecuteAsync(
        "pub fn read(value: &'static i32) -> i32 { *value } fn main() {}",
        "use SourceProducer::read; fn main() { let owner = 42; println!(\"{}\", read(&owner)); }", null);

    private static Task PrivateFieldsAsync() => ExecuteAsync(
        "pub struct Secret { value: i32 } pub fn make() -> Secret { Secret { value: 42 } } fn main() {}",
        "use SourceProducer::make; fn main() { let value = make(); println!(\"{}\", value.value); }", null, diagnosticPrefix: "RST");

    private static Task GenericNominalAsync() => ExecuteAsync(
        "pub struct Record<T> { pub value: T } pub fn make() -> Record<i32> { Record { value: 42 } } fn main() {}",
        "fn main() {}", null, diagnosticPrefix: "RSC", producerMayReject: true);

    private static Task EnumAsync() => ExecuteAsync(
        "pub enum Value { Empty, Number(i32), Named { value: i32 } } pub fn make() -> Value { Value::Number(40) } pub fn read(value: Value) -> i32 { match value { Value::Empty => 0, Value::Number(n) => n, Value::Named { value: n } => n } } fn main() {}",
        "use SourceProducer::{Value, make, read}; fn main() { let value: Value = make(); println!(\"{}\", read(value)); println!(\"{}\", read(Value::Named { value: 42 })); println!(\"{}\", read(Value::Empty)); }", "40\n42\n0\n");

    private static Task EnumReferencesAsync() => ExecuteAsync(
        "pub enum Value { Empty, Shared(&'static i32) } pub fn none() -> Value { Value::Empty } pub fn some() -> Value { Value::Shared(&42) } pub fn read(value: Value) -> i32 { match value { Value::Empty => 0, Value::Shared(r) => *r } } fn main() {}",
        "use SourceProducer::{none, some, read}; fn main() { println!(\"{}\", read(none())); println!(\"{}\", read(some())); }", "0\n42\n");

    private static Task NestedAsync() => ExecuteAsync(
        "pub mod nested { pub struct Point { pub value: i32 } pub fn add(value: Point) -> i32 { value.value + 1 } } fn main() {}",
        "use SourceProducer::nested::{Point as LocalPoint, add}; fn main() { let value: LocalPoint = LocalPoint { value: 41 }; println!(\"{}\", add(value)); }", "42\n");

    private static Task PrivateModuleAsync() => ExecuteAsync(
        "mod hidden { pub fn value() -> i32 { 42 } } fn main() {}",
        "use SourceProducer::hidden::value; fn main() { println!(\"{}\", value()); }", null, diagnosticPrefix: "RSN");

    private static Task NominalIdentityAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "rustsharp-p1-09-execution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            string first = Path.Combine(root, "ProducerA.dll"), second = Path.Combine(root, "ProducerB.dll"), output = Path.Combine(root, "Consumer.dll");
            CompilationResult a = CompilerDriver.Compile("pub struct Item { pub value: i32 } pub fn take(value: Item) -> i32 { value.value } fn main() {}",
                Path.Combine(root, "first.rs"), first, "ProducerA", CompilationProfile.SafeCoreMirV2, deadline.Token);
            CompilationResult b = CompilerDriver.Compile("pub struct Item { pub value: bool } pub fn make() -> Item { Item { value: true } } fn main() {}",
                Path.Combine(root, "second.rs"), second, "ProducerB", CompilationProfile.SafeCoreMirV2, deadline.Token);
            AssertEx.True(a.Success && b.Success, string.Join("; ", a.Diagnostics.Concat(b.Diagnostics)));
            CompilationResult rejected = CompilerDriver.CompileWithMetadataReferences(
                "use ProducerA::take; use ProducerB::make; fn main() { take(make()); }", Path.Combine(root, "consumer.rs"), output,
                "Consumer", CompilationProfile.SafeCoreMirV2, [second, first], cancellationToken: deadline.Token);
            AssertEx.False(rejected.Success, "Nominal identity cannot be inferred from the common source name or CLR layout hash.");
            AssertEx.True(rejected.Diagnostics.Any(diagnostic => diagnostic.Code.StartsWith("RST", StringComparison.Ordinal)), string.Join("; ", rejected.Diagnostics));
            AssertEx.False(File.Exists(output), "A nominal identity mismatch must emit no PE.");
            return Task.CompletedTask;
        }
        finally { DeleteOwnedDirectory(root); }
    }

    private static async Task ExecuteAsync(string producerSource, string consumerSource, string? expected,
        SafeCorePanicStrategy panicStrategy = SafeCorePanicStrategy.Unwind, int expectedExit = 0,
        string diagnosticPrefix = "RSO", bool producerMayReject = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "rustsharp-p1-09-execution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            string producer = Path.Combine(root, "SourceProducer.dll"), consumer = Path.Combine(root, "SourceConsumer.dll");
            string producerPath = Path.Combine(root, "producer.rs");
            File.WriteAllText(producerPath, producerSource);
            CompilationResult produced = CompilerDriver.CompileWithPanicStrategy(producerSource, producerPath, producer, panicStrategy,
                "SourceProducer", CompilationProfile.SafeCoreMirV2, deadline.Token);
            if (producerMayReject && !produced.Success)
            {
                AssertEx.False(File.Exists(producer), "Unsupported nominal shapes must publish no producer PE.");
                AssertEx.True(produced.Diagnostics.Any(diagnostic => diagnostic.Code.StartsWith("RS", StringComparison.Ordinal)), string.Join("; ", produced.Diagnostics));
                return;
            }
            AssertEx.True(produced.Success, string.Join("; ", produced.Diagnostics));
            RustSharpMetadataImportResult metadata = RustSharpMetadataConsumer.ReadAssembly(producer);
            AssertEx.True(metadata.IsSuccessful, string.Join("; ", metadata.Diagnostics));
            CompilationResult consumed = CompilerDriver.CompileWithMetadataReferences(consumerSource, Path.Combine(root, "consumer.rs"), consumer,
                "SourceConsumer", CompilationProfile.SafeCoreMirV2, [producer], cancellationToken: deadline.Token);
            if (expected is null)
            {
                AssertEx.False(consumed.Success, "The source contract violation must reject before publication.");
                AssertEx.False(File.Exists(consumer), "Rejected source packages must publish no PE.");
                AssertEx.True(consumed.Diagnostics.Any(diagnostic => diagnostic.Code.StartsWith(diagnosticPrefix, StringComparison.Ordinal)),
                    "Expected the source boundary diagnostic: " + string.Join("; ", consumed.Diagnostics));
                return;
            }
            AssertEx.True(consumed.Success, string.Join("; ", consumed.Diagnostics));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(new("dotnet", [consumer], root, TimeSpan.FromSeconds(5)), deadline.Token);
            Console.WriteLine("Source package execution PID=" + run.StartedProcess.ProcessId + "; parent=" + run.StartedProcess.ParentProcessId +
                "; started=" + run.StartedProcess.StartedAt.ToString("O") + "; command=" + run.StartedProcess.CommandLine +
                "; cleanupIncomplete=" + run.ProcessTreeCleanupIncomplete);
            AssertEx.True((expectedExit == -1 ? run.ExitCode is not null and not 0 : run.ExitCode == expectedExit) && run.Termination == BoundedProcessTermination.Exited &&
                !run.OutputTruncated && !run.ProcessTreeCleanupIncomplete, run.StandardError);
            if (expectedExit == -1) AssertEx.True(run.StandardError.Contains("OverflowException", StringComparison.Ordinal) &&
                !run.StandardError.Contains("RustSharp panic abort:", StringComparison.Ordinal), run.StandardError);
            if (panicStrategy == SafeCorePanicStrategy.Abort) AssertEx.True(run.StandardError.Contains("RustSharp panic abort:", StringComparison.Ordinal), run.StandardError);
            AssertEx.Equal(expected, run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            DeleteOwnedDirectory(root);
        }
    }

    private static void DeleteOwnedDirectory(string root)
    {
            string full = Path.GetFullPath(root);
            if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(full).StartsWith("rustsharp-p1-09-execution-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected task temporary path.");
            Directory.Delete(full, true);
    }
}
