using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Semantics;

namespace RustSharp.Tests;

internal static class P1StructuralOwnerPackageTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1-09 standalone tuple keeps original CLR owner through three source assemblies", TupleAsync),
        new("P1-09 standalone array keeps original CLR owner through three source assemblies", ArrayAsync),
        new("P1-09 nested tuple and array keep original CLR owners through three source assemblies", NestedAsync),
        new("P1-09 anonymous aggregate nominal components retain original identities through three assemblies", NominalComponentsAsync),
        new("P1-09 standalone structural re-export rejects replaced original owner before emission", StaleOwnerAsync),
        new("P1-09 standalone structural owner metadata drift rejects actual PE before emission", OwnerMutationsAsync),
        new("P1-09 source consumer cannot impersonate a predictable metadata nominal owner", NominalImpersonationAsync),
        new("P1-09 non-Copy unit constructors return and transfer typed owned locals without Drop", UnitSourceAsync),
        new("P1-09 non-Copy unit keeps original nominal owner through three source assemblies", UnitPackagesAsync),
    ];

    private const string TupleProducer = "pub fn make() -> (i32, bool) { (42, true) } " +
        "pub fn read(value: (i32, bool)) -> i32 { if value.1 { value.0 } else { 0 } } fn main() {}";
    private const string TupleWrapper = "use StructuralProducer::{make as original_make, read as original_read}; " +
        "pub fn make() -> (i32, bool) { original_make() } pub fn read(value: (i32, bool)) -> i32 { original_read(value) } fn main() {}";
    private const string TupleConsumer = "use StructuralWrapper::{make, read}; " +
        "fn main() { println!(\"{}\", read(make())); println!(\"{}\", read((42, true))); }";

    private static Task TupleAsync() => ExecuteAsync(TupleProducer, TupleWrapper, TupleConsumer, "42\n42\n");

    private static Task ArrayAsync() => ExecuteAsync(
        "pub fn make() -> [i32; 2] { [40, 2] } pub fn read(value: [i32; 2]) -> i32 { value[0] + value[1] } fn main() {}",
        "use StructuralProducer::{make as original_make, read as original_read}; " +
        "pub fn make() -> [i32; 2] { original_make() } pub fn read(value: [i32; 2]) -> i32 { original_read(value) } fn main() {}",
        "use StructuralWrapper::{make, read}; fn main() { let mut value = make(); value[0] = 20; " +
        "println!(\"{}\", read(value)); println!(\"{}\", read([40, 2])); }", "22\n42\n");

    private static Task NestedAsync() => ExecuteAsync(
        "pub fn make() -> ((i32, bool), [i32; 2]) { ((40, true), [1, 1]) } " +
        "pub fn read(value: ((i32, bool), [i32; 2])) -> i32 { value.0.0 + value.1[0] + value.1[1] } fn main() {}",
        "use StructuralProducer::{make as original_make, read as original_read}; " +
        "pub fn make() -> ((i32, bool), [i32; 2]) { original_make() } " +
        "pub fn read(value: ((i32, bool), [i32; 2])) -> i32 { original_read(value) } fn main() {}",
        "use StructuralWrapper::{make, read}; fn main() { println!(\"{}\", read(make())); " +
        "println!(\"{}\", read(((40, true), [1, 1]))); }", "42\n42\n");

    private static Task NominalComponentsAsync() => ExecuteAsync(
        "pub struct Point { pub value: i32 } pub fn make() -> (Point, [i32; 2]) { (Point { value: 40 }, [1, 1]) } " +
        "pub fn read(value: (Point, [i32; 2])) -> i32 { value.0.value + value.1[0] + value.1[1] } fn main() {}",
        "use StructuralProducer::{Point, make as original_make, read as original_read}; " +
        "pub fn make() -> (Point, [i32; 2]) { original_make() } " +
        "pub fn read(value: (Point, [i32; 2])) -> i32 { original_read(value) } fn main() {}",
        "use StructuralWrapper::{make, read}; use StructuralProducer::Point; fn main() { println!(\"{}\", read(make())); " +
        "println!(\"{}\", read((Point { value: 40 }, [1, 1]))); }", "42\n42\n", hasNominalComponent: true);

    private const string UnitProducer = "pub struct Unit; pub fn unit() -> Unit { Unit } " +
        "pub fn early() -> Unit { return Unit; } pub fn transfer(value: Unit) -> Unit { value } " +
        "pub fn read(value: Unit) -> i32 { 42 } fn main() {}";

    private static async Task UnitSourceAsync()
    {
        string directory = CreateDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try
        {
            string source = UnitProducer.Replace("fn main() {}", "fn main() { " +
                "println!(\"{}\", read(transfer(unit()))); println!(\"{}\", read(transfer(early()))); " +
                "let value = Unit; println!(\"{}\", read(transfer(value))); }", StringComparison.Ordinal);
            SafeCoreMirPipelineResult proof = SafeCoreMirPipeline.Analyze(source, "unit-source.rs", new()
            {
                EnableP1Extensions = true, RequireOwnershipEvidence = true, RequireCleanupEvidence = true,
                Timeout = TimeSpan.FromSeconds(5), CancellationToken = deadline.Token,
            });
            AssertEx.True(proof.IsSuccessful, string.Join("; ", proof.Diagnostics));
            SafeCoreMirProgram mir = proof.Mir!.Program!;
            SafeCoreMirAdtLayout unit = mir.AdtLayouts.Single();
            AssertEx.True(unit.Type.Name == "crate::Unit" && !unit.IsCopy && unit.Fields.Count == 0 && unit.ExternalDropFunction is null,
                "A fresh unit value must retain its declared non-Copy nominal type without an invented destructor.");
            AssertEx.True(mir.Functions.Count <= 8 && mir.Functions.All(function => function.Blocks.Count <= 64 &&
                function.Blocks.All(block => block.Statements.Count <= 128)),
                "The unit regression has fixed function and block inspection budgets.");
            SafeCoreMirFunction[] factories = mir.Functions.Where(function => function.Name is "crate::unit" or "crate::unit#value" or
                "crate::early" or "crate::early#value").ToArray();
            AssertEx.Equal(2, factories.Length);
            foreach (SafeCoreMirFunction factory in factories)
            {
                deadline.Token.ThrowIfCancellationRequested();
                SafeCoreMirBlock[] returns = factory.Blocks.Where(block => block.Terminator.Kind == SafeCoreMirTerminatorKind.Return).ToArray();
                AssertEx.Equal(1, returns.Length);
                AssertEx.True(returns
                    .All(block => block.Terminator.Operand is { Kind: SafeCoreMirOperandKind.Local } returned && returned.Type == unit.Type),
                    "Both tail and explicit returns must transfer a typed local, rather than a nominal constant through the Copy bridge.");
                AssertEx.Equal(1, factory.Blocks.SelectMany(block => block.Statements).Count(statement =>
                    statement.Value.Kind == SafeCoreMirRvalueKind.Adt && statement.Value.Type == unit.Type && statement.Value.Operands.Count == 0));
                AssertEx.True(factory.Locals.All(local => local.IsUnitAdt && local.Type == unit.Type && local.DestructorFunctionId is null),
                    "Materializing a non-Copy unit must not invent Drop evidence.");
            }
            AssertEx.True(proof.Ownership!.Program!.Functions.Single(function => function.Name is "crate::transfer" or "crate::transfer#value")
                .Blocks.SelectMany(block => block.Instructions).Any(instruction => instruction.Kind == SafeCoreOwnershipInstructionKind.Move),
                "Transferring the initialized nominal unit must retain an explicit ownership Move.");

            SafeCoreMirPipelineResult twice = SafeCoreMirPipeline.Analyze("struct Unit; fn consume(value: Unit) {} " +
                "fn main() { let value = Unit; consume(value); consume(value); }", "unit-second-move.rs", new()
            {
                EnableP1Extensions = true, RequireOwnershipEvidence = true, RequireCleanupEvidence = true,
                Timeout = TimeSpan.FromSeconds(5), CancellationToken = deadline.Token,
            });
            AssertEx.True(!twice.IsSuccessful && twice.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.UseAfterMove),
                "A zero-field representation must not permit copying its non-Copy source value: " + string.Join("; ", twice.Diagnostics));

            SafeCoreMirFunction tail = factories.Single(function => function.Name is "crate::unit" or "crate::unit#value");
            SafeCoreMirBlock[] rawBlocks = tail.Blocks.Select(block => new SafeCoreMirBlock(block.Id, block.Statements,
                block.Terminator.Kind == SafeCoreMirTerminatorKind.Return ? SafeCoreMirTerminator.Return(
                    SafeCoreMirOperand.Constant(unit.Type, "()", block.Terminator.Source), block.Terminator.Source) : block.Terminator,
                block.Source, deadline.Token)).ToArray();
            SafeCoreMirFunction rawTail = new(tail.Id, tail.Name, tail.ReturnType, tail.Locals, rawBlocks,
                tail.EntryBlockId, tail.Source, tail.IsPublic, deadline.Token);
            SafeCoreMirProgram raw = new(mir.Functions.Select(function => function.Id == tail.Id ? rawTail : function).ToArray(),
                mir.AdtLayouts, mir.ExternalFunctions, deadline.Token);
            SafeCoreMirOwnershipResult rawResult = SafeCoreMirOwnershipAdapter.Analyze(raw, new()
            { Timeout = TimeSpan.FromSeconds(5), CancellationToken = deadline.Token });
            AssertEx.True(!rawResult.IsSuccessful && rawResult.Diagnostics.Any(diagnostic => diagnostic.Code == "RSM3002" &&
                diagnostic.Message.Contains("Only structural Copy locals and constants", StringComparison.Ordinal)),
                "An unmaterialized nominal constant must still reject at the ownership terminator: " + string.Join("; ", rawResult.Diagnostics));

            string output = Path.Combine(directory, "UnitSource.dll");
            CompilationResult compiled = CompilerDriver.Compile(source, Path.Combine(directory, "source.rs"), output,
                "UnitSource", CompilationProfile.SafeCoreMirV2, deadline.Token);
            AssertEx.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
            await RunUnitAssemblyAsync(output, directory, "42\n42\n42\n", deadline.Token).ConfigureAwait(false);
        }
        finally { DeleteDirectory(directory); }
    }

    private static async Task UnitPackagesAsync()
    {
        string directory = CreateDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try
        {
            const string wrapperSource = "use StructuralProducer::{Unit, unit as original_unit, transfer as original_transfer}; " +
                "pub fn make_unit() -> Unit { original_transfer(original_unit()) } " +
                "pub fn read_unit(value: Unit) -> i32 { 42 } fn main() {}";
            (string producer, string wrapper) = CompilePair(directory, UnitProducer, wrapperSource, deadline.Token);
            RustSharpMetadataImportResult original = RustSharpMetadataConsumer.ReadAssembly(producer, cancellationToken: deadline.Token);
            RustSharpMetadataImportResult forwarded = RustSharpMetadataConsumer.ReadAssembly(wrapper,
                dependencyPaths: [producer, wrapper], cancellationToken: deadline.Token);
            AssertEx.True(original.IsSuccessful && forwarded.IsSuccessful, string.Join("; ", original.Diagnostics.Concat(forwarded.Diagnostics)));
            RustSharpMetadataDocument originalDocument = original.Document!;
            RustSharpMetadataDocument forwardedDocument = forwarded.Document!;
            RustSharpMetadataSourceValueType declared = originalDocument.SourceValueTypes.Single();
            RustSharpMetadataSourceValueType exported = forwardedDocument.SourceValueTypes.Single();
            AssertEx.True(declared.ConstructorKind == "unit" && !declared.IsCopy && declared.DropFunctionId is null && !declared.Fields.Any() &&
                exported.ConstructorKind == "unit" && !exported.IsCopy && exported.DropFunctionId is null && !exported.Fields.Any(),
                "Both source packages must retain the actual non-Copy, zero-field, no-Drop unit contract.");
            AssertEx.True(exported.Owner is { } owner && owner.AssemblyName == "StructuralProducer" &&
                owner.ModuleVersionId == original.ModuleVersionId && owner.SourceName == declared.Name && owner.ClrName == declared.ClrName,
                "The wrapper's nominal unit must retain the independently validated original producer owner.");
            AssertEx.Equal("move", originalDocument.CallContracts.Single(contract =>
                RustSharpMetadataConsumer.FindFunction(originalDocument, contract.FunctionId)?.SourceQualifiedName is "crate::unit" or "crate::unit#value").ReturnContract!);
            AssertEx.Equal("move", forwardedDocument.CallContracts.Single(contract =>
                RustSharpMetadataConsumer.FindFunction(forwardedDocument, contract.FunctionId)?.SourceQualifiedName is "crate::make_unit" or "crate::make_unit#value").ReturnContract!);

            string consumer = Path.Combine(directory, "UnitPackageConsumer.dll");
            CompilationResult consumed = CompilerDriver.CompileWithMetadataReferences(
                "use StructuralProducer::Unit; use StructuralWrapper::{make_unit, read_unit}; " +
                "fn main() { println!(\"{}\", read_unit(make_unit())); println!(\"{}\", read_unit(Unit)); }",
                Path.Combine(directory, "consumer.rs"), consumer, "UnitPackageConsumer", CompilationProfile.SafeCoreMirV2,
                [wrapper, producer], cancellationToken: deadline.Token);
            AssertEx.True(consumed.Success, string.Join("; ", consumed.Diagnostics));
            AssertNoCopiedOriginalLayouts(wrapper, originalDocument, deadline.Token);
            AssertNoCopiedOriginalLayouts(consumer, originalDocument, deadline.Token);
            await RunUnitAssemblyAsync(consumer, directory, "42\n42\n", deadline.Token).ConfigureAwait(false);

            string rejected = Path.Combine(directory, "RejectedUnitPackageConsumer.dll");
            CompilationResult repeated = CompilerDriver.CompileWithMetadataReferences(
                "use StructuralWrapper::{make_unit, read_unit}; fn main() { let value = make_unit(); " +
                "read_unit(value); read_unit(value); }", Path.Combine(directory, "repeated.rs"), rejected,
                "RejectedUnitPackageConsumer", CompilationProfile.SafeCoreMirV2, [wrapper, producer], cancellationToken: deadline.Token);
            AssertEx.True(!repeated.Success && !File.Exists(rejected) && repeated.Diagnostics.Any(diagnostic =>
                diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.UseAfterMove),
                "An imported non-Copy unit must reject its second transfer before consumer publication: " + string.Join("; ", repeated.Diagnostics));
        }
        finally { DeleteDirectory(directory); }
    }

    private static async Task RunUnitAssemblyAsync(string output, string directory, string expected, CancellationToken cancellationToken)
    {
        BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(new("dotnet", [output], directory,
            TimeSpan.FromSeconds(5), started => Console.WriteLine("P1-09 non-Copy unit runtime PID=" + started.ProcessId +
                "; parent=" + started.ParentProcessId + "; started=" + started.StartedAt.ToString("O") + "; command=" + started.CommandLine)),
            cancellationToken).ConfigureAwait(false);
        AssertEx.True(run.Succeeded && !run.OutputTruncated && !run.OutputReadTimedOut && !run.ProcessTreeCleanupIncomplete, run.StandardError);
        AssertEx.Equal(expected, run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static async Task ExecuteAsync(string producerSource, string wrapperSource, string consumerSource, string expected,
        bool hasNominalComponent = false)
    {
        string directory = CreateDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try
        {
            (string producer, string wrapper) = CompilePair(directory, producerSource, wrapperSource, deadline.Token);
            RustSharpMetadataImportResult original = RustSharpMetadataConsumer.ReadAssembly(producer,
                dependencyPaths: [producer, wrapper], cancellationToken: deadline.Token);
            RustSharpMetadataImportResult forwarded = RustSharpMetadataConsumer.ReadAssembly(wrapper,
                dependencyPaths: [producer, wrapper], cancellationToken: deadline.Token);
            AssertEx.True(original.IsSuccessful && forwarded.IsSuccessful, string.Join("; ", original.Diagnostics.Concat(forwarded.Diagnostics)));
            AssertEx.True(original.Document!.SourceValueTypes.IsEmpty == !hasNominalComponent &&
                forwarded.Document!.SourceValueTypes.IsEmpty == !hasNominalComponent,
                "The fixture must keep its declared standalone or nominal-component shape.");
            AssertEx.True(forwarded.Document!.SourceStructuralTypes.Length > 0 &&
                forwarded.Document.SourceStructuralTypes.All(binding => binding.Owner.AssemblyName == "StructuralProducer" &&
                    binding.ClrName == "StructuralProducer::" + binding.Owner.ClrName && binding.Owner.ModuleVersionId == original.ModuleVersionId),
                "Every forwarded anonymous layout must retain the independently validated original producer.");
            AssertEx.Equal(producer, forwarded.ResolvedOwnerPaths["StructuralProducer"]);
            string consumer = Path.Combine(directory, "StructuralConsumer.dll");
            CompilationResult consumed = CompilerDriver.CompileWithMetadataReferences(consumerSource,
                Path.Combine(directory, "consumer.rs"), consumer, "StructuralConsumer", CompilationProfile.SafeCoreMirV2,
                [wrapper, producer], cancellationToken: deadline.Token);
            AssertEx.True(consumed.Success, string.Join("; ", consumed.Diagnostics));
            AssertNoCopiedOriginalLayouts(wrapper, original.Document, deadline.Token);
            AssertNoCopiedOriginalLayouts(consumer, original.Document, deadline.Token);
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(new("dotnet", [consumer], directory,
                TimeSpan.FromSeconds(5), started => Console.WriteLine("P1-09 structural owner runtime PID=" + started.ProcessId +
                    "; parent=" + started.ParentProcessId + "; started=" + started.StartedAt.ToString("O") + "; command=" + started.CommandLine)),
                deadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded && !run.OutputTruncated && !run.ProcessTreeCleanupIncomplete, run.StandardError);
            AssertEx.Equal(expected, run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally { DeleteDirectory(directory); }
    }

    private static Task StaleOwnerAsync()
    {
        string directory = CreateDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            (string producer, string wrapper) = CompilePair(directory, TupleProducer, TupleWrapper, deadline.Token);
            RustSharpMetadataImportResult original = RustSharpMetadataConsumer.ReadAssembly(producer, cancellationToken: deadline.Token);
            AssertEx.True(original.IsSuccessful, string.Join("; ", original.Diagnostics));
            CompilationResult replacement = CompilerDriver.Compile(TupleProducer.Replace("(42, true)", "(99, true)", StringComparison.Ordinal),
                Path.Combine(directory, "producer.rs"), producer, "StructuralProducer", CompilationProfile.SafeCoreMirV2, deadline.Token);
            AssertEx.True(replacement.Success, string.Join("; ", replacement.Diagnostics));
            RustSharpMetadataImportResult replaced = RustSharpMetadataConsumer.ReadAssembly(producer, cancellationToken: deadline.Token);
            AssertEx.True(replaced.IsSuccessful && replaced.ModuleVersionId != original.ModuleVersionId &&
                replaced.Document!.SourceSha256 != original.Document!.SourceSha256,
                "The negative fixture must replace the original PE with an independently valid but different actual producer.");
            RustSharpMetadataImportResult rejected = RustSharpMetadataConsumer.ReadAssembly(wrapper,
                dependencyPaths: [wrapper, producer], cancellationToken: deadline.Token);
            AssertEx.False(rejected.IsSuccessful, "A standalone anonymous owner proof cannot resolve against the replacement PE.");
            string consumer = Path.Combine(directory, "RejectedStructuralConsumer.dll");
            CompilationResult consumed = CompilerDriver.CompileWithMetadataReferences(TupleConsumer,
                Path.Combine(directory, "consumer.rs"), consumer, "RejectedStructuralConsumer", CompilationProfile.SafeCoreMirV2,
                [wrapper, producer], cancellationToken: deadline.Token);
            AssertEx.False(consumed.Success || File.Exists(consumer), "A stale structural owner must reject before consumer publication.");
            AssertEx.True(consumed.Diagnostics.Any(diagnostic => diagnostic.Code.StartsWith(RustSharpMetadataConsumer.InvalidMetadata, StringComparison.Ordinal)),
                "Structural owner rejection must retain the stable metadata boundary diagnostic: " + string.Join("; ", consumed.Diagnostics));
        }
        finally { DeleteDirectory(directory); }
        return Task.CompletedTask;
    }

    private static (string Producer, string Wrapper) CompilePair(string directory, string producerSource,
        string wrapperSource, CancellationToken cancellationToken)
    {
        string producer = Path.Combine(directory, "StructuralProducer.dll"), wrapper = Path.Combine(directory, "StructuralWrapper.dll");
        CompilationResult produced = CompilerDriver.Compile(producerSource, Path.Combine(directory, "producer.rs"), producer,
            "StructuralProducer", CompilationProfile.SafeCoreMirV2, cancellationToken);
        AssertEx.True(produced.Success, string.Join("; ", produced.Diagnostics));
        CompilationResult forwarded = CompilerDriver.CompileWithMetadataReferences(wrapperSource, Path.Combine(directory, "wrapper.rs"),
            wrapper, "StructuralWrapper", CompilationProfile.SafeCoreMirV2, [producer], cancellationToken: cancellationToken);
        AssertEx.True(forwarded.Success, string.Join("; ", forwarded.Diagnostics));
        return (producer, wrapper);
    }

    private static Task OwnerMutationsAsync()
    {
        string directory = CreateDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            (string producer, string wrapper) = CompilePair(directory, TupleProducer, TupleWrapper, deadline.Token);
            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(wrapper,
                dependencyPaths: [producer, wrapper], cancellationToken: deadline.Token);
            AssertEx.True(imported.IsSuccessful, string.Join("; ", imported.Diagnostics));
            byte[] originalImage = File.ReadAllBytes(wrapper);
            byte[] json = Encoding.UTF8.GetBytes(imported.Document!.Json);
            int offset = originalImage.AsSpan().IndexOf(json);
            AssertEx.True(offset >= 0, "The structural mutation must retain the actual wrapper PE and emitted MethodDefs.");
            string[] mutations = ["missing-binding", "owner-hash", "owner-mvid", "owner-source", "source-shape", "original-shape", "clr-name"];
            for (int index = 0; index < mutations.Length; index++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                string mutation = mutations[index];
                JsonObject changed = JsonNode.Parse(imported.Document.Json)!.AsObject();
                changed.Remove("cleanupSnapshot");
                JsonObject binding = changed["sourceStructuralTypes"]![0]!.AsObject();
                JsonObject owner = binding["owner"]!.AsObject();
                switch (mutation)
                {
                    case "missing-binding": changed.Remove("sourceStructuralTypes"); break;
                    case "owner-hash": owner["assemblySha256"] = new string('0', 64); break;
                    case "owner-mvid": owner["moduleVersionId"] = "11111111-1111-1111-1111-111111111111"; break;
                    case "owner-source": owner["sourceSha256"] = new string('0', 64); break;
                    case "source-shape": binding["type"] = "(i32, i32)"; break;
                    case "original-shape": owner["sourceName"] = "(i32, i32)"; break;
                    case "clr-name":
                        string clrName = owner["clrName"]!.GetValue<string>();
                        string changedClrName = clrName[..^1] + (clrName[^1] == '0' ? "1" : "0");
                        owner["clrName"] = changedClrName;
                        binding["clrName"] = "StructuralProducer::" + changedClrName;
                        break;
                    default: throw new InvalidOperationException("Unknown bounded structural owner mutation.");
                }
                byte[] replacement = Encoding.UTF8.GetBytes(changed.ToJsonString());
                AssertEx.True(replacement.Length <= json.Length, "The structural mutation must fit the original PE metadata attribute.");
                byte[] image = (byte[])originalImage.Clone();
                image.AsSpan(offset, json.Length).Fill((byte)' ');
                replacement.CopyTo(image.AsSpan(offset));
                File.WriteAllBytes(wrapper, image);
                string consumer = Path.Combine(directory, "RejectedStructuralMutationConsumer.dll");
                CompilationResult rejected = CompilerDriver.CompileWithMetadataReferences(TupleConsumer,
                    Path.Combine(directory, "consumer.rs"), consumer, "RejectedStructuralMutationConsumer", CompilationProfile.SafeCoreMirV2,
                    [wrapper, producer], cancellationToken: deadline.Token);
                AssertEx.False(rejected.Success || File.Exists(consumer), "Structural owner drift must reject before consumer publication: " + mutation);
                AssertEx.True(rejected.Diagnostics.Any(diagnostic => diagnostic.Code.StartsWith(RustSharpMetadataConsumer.InvalidMetadata, StringComparison.Ordinal)),
                    "Structural owner mutations must retain RSC0011 diagnostics: " + mutation + "; " + string.Join("; ", rejected.Diagnostics));
                Console.WriteLine("P1-09 structural owner PE mutations " + (index + 1) + "/" + mutations.Length + ": " + mutation);
            }
        }
        finally { DeleteDirectory(directory); }
        return Task.CompletedTask;
    }

    private static async Task NominalImpersonationAsync()
    {
        string directory = CreateDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try
        {
            string producer = Path.Combine(directory, "NominalProducer.dll");
            CompilationResult produced = CompilerDriver.Compile("pub struct Hidden { pub value: i32 } " +
                "pub fn make() -> Hidden { Hidden { value: 42 } } pub fn read(value: Hidden) -> i32 { value.value } fn main() {}",
                Path.Combine(directory, "producer.rs"), producer, "NominalProducer", CompilationProfile.SafeCoreMirV2, deadline.Token);
            AssertEx.True(produced.Success, string.Join("; ", produced.Diagnostics));
            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(producer, cancellationToken: deadline.Token);
            AssertEx.True(imported.IsSuccessful, string.Join("; ", imported.Diagnostics));
            string ownerModule = "__rsc_ext_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                "metadata:NominalProducer@" + imported.ModuleVersionId!.Value.ToString("D"))))[..32];
            string positive = Path.Combine(directory, "DistinctNominalConsumer.dll");
            CompilationResult distinct = CompilerDriver.CompileWithMetadataReferences("struct Hidden { value: bool } " +
                "use NominalProducer::{make, read}; fn main() { let local = Hidden { value: true }; " +
                "println!(\"{}\", local.value); println!(\"{}\", read(make())); }", Path.Combine(directory, "consumer.rs"),
                positive, "DistinctNominalConsumer", CompilationProfile.SafeCoreMirV2, [producer], cancellationToken: deadline.Token);
            AssertEx.True(distinct.Success, "Different local and foreign owners must coexist for the same source spelling: " + string.Join("; ", distinct.Diagnostics));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(new("dotnet", [positive], directory,
                TimeSpan.FromSeconds(5), started => Console.WriteLine("P1-09 nominal isolation runtime PID=" + started.ProcessId +
                    "; parent=" + started.ParentProcessId + "; started=" + started.StartedAt.ToString("O") + "; command=" + started.CommandLine)),
                deadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded && !run.OutputTruncated && !run.ProcessTreeCleanupIncomplete, run.StandardError);
            AssertEx.Equal("true\n42\n", run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
            string[] fields = ["bool", "i32"];
            foreach (string field in fields)
            {
                deadline.Token.ThrowIfCancellationRequested();
                string rejectedPath = Path.Combine(directory, "RejectedNominalImpersonator.dll");
                CompilationResult rejected = CompilerDriver.CompileWithMetadataReferences("mod " + ownerModule +
                    " { pub struct Hidden { pub value: " + field + " } } fn main() {}", Path.Combine(directory, "consumer.rs"),
                    rejectedPath, "RejectedNominalImpersonator", CompilationProfile.SafeCoreMirV2, [producer], cancellationToken: deadline.Token);
                AssertEx.False(rejected.Success || File.Exists(rejectedPath), "Matching the actual predictable producer scope cannot grant its nominal owner.");
                AssertEx.True(rejected.Diagnostics.Any(diagnostic => diagnostic.Code == "RST2002" &&
                    diagnostic.Message.Contains("collides with a verified foreign type owner", StringComparison.Ordinal)), string.Join("; ", rejected.Diagnostics));
            }
        }
        finally { DeleteDirectory(directory); }
    }

    private static void AssertNoCopiedOriginalLayouts(string assemblyPath, RustSharpMetadataDocument original,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        AssertEx.True(metadata.TypeDefinitions.Count <= 64, "The structural fixture must have a fixed TypeDef inspection budget.");
        string[] actualNames = metadata.TypeDefinitions.Select(handle => metadata.GetString(metadata.GetTypeDefinition(handle).Name)).ToArray();
        AssertEx.True(original.ValueTypes.Length > 0, "The original producer must contain actual anonymous CLR layouts.");
        AssertEx.False(original.ValueTypes.Any(layout => actualNames.Contains(layout.Name, StringComparer.Ordinal)),
            "A wrapper and consumer must use original producer TypeRefs instead of copied anonymous TypeDefs.");
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rustsharp-p1-structural-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        string resolved = Path.GetFullPath(directory);
        AssertEx.True(Path.GetDirectoryName(resolved) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
            Path.GetFileName(resolved).StartsWith("rustsharp-p1-structural-owner-", StringComparison.Ordinal),
            "Cleanup must target the exact task-owned structural fixture directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        AssertEx.False(Directory.Exists(resolved), "The structural fixture must reclaim its task-owned directory.");
    }
}
