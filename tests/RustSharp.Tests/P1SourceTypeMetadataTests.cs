using System.Text.Json.Nodes;
using System.Text;
using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Semantics;

namespace RustSharp.Tests;

internal static class P1SourceTypeMetadataTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1-09 source type codec round-trips exact closed structural identities", RoundTripAsync),
        new("P1-09 source type codec rejects malformed shapes and exhausted budgets", RejectsAsync),
        new("P1-09 source metadata preserves field parameter and origin positions", PositionalMetadataAsync),
        new("P1-09 source metadata rejects unknown members and invalid origin terms", RejectsMetadataAsync),
        new("P1-09 structural owner metadata rejects incomplete ambiguous and exhausted evidence", RejectStructuralMetadataAsync),
        new("P1-09 source reference contracts execute in separately compiled source packages", SourceReferencePackagesAsync),
        new("P1-09 source contracts reject fifteen real producer PE mutations before emission", SourceSchemaMutationsAsync),
        new("P1-09 source contracts reject actual MemberRef and user string drift with unchanged IL and metadata JSON", MemberReferenceDriftAsync),
        new("P1-09 source static lifetime fields preserve and reconcile import evidence", StaticFieldsAsync),
        new("P1-09 static mutable source parameter origins preserve checked lifetime evidence", StaticMutableAsync),
        new("P1-09 borrowed enum payload origins preserve typed physical projections across source packages", EnumProjectionAsync),
        new("P1-09 source re-export owner proofs reject five real wrapper PE mutations", OwnerBindingsAsync),
    ];

    private static Task RoundTripAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string[] types = ["i32", "bool", "usize", "()", "&i32", "&mut i32", "&[i32]", "[i32; 3]",
            "(i32,)", "(bool, [i32; 2])", "crate::Point", "&mut crate::Point", "crate::nested::Record"];
        foreach (string text in types)
        {
            deadline.Token.ThrowIfCancellationRequested();
            SafeCoreType first = SafeCoreSourceTypeCodec.Parse(text, deadline.Token);
            AssertEx.Equal(text, SafeCoreSourceTypeCodec.Format(first, deadline.Token));
            AssertEx.Equal(first, SafeCoreSourceTypeCodec.Parse(first.ToString(), deadline.Token));
        }
        return Task.CompletedTask;
    }

    private static Task RejectsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string[] unsupported = ["i64", "fn(i32) -> i32", "?0", "<error>", "crate::Box<i32>",
            "crate::", "::Point", "Point", "&'static i32", "[i32; -1]", "[i32; 03]", "(i32)", "(i32,bool)"];
        foreach (string text in unsupported)
        {
            deadline.Token.ThrowIfCancellationRequested();
            AssertEx.Throws<ArgumentException>(() => SafeCoreSourceTypeCodec.Parse(text, deadline.Token));
        }
        AssertEx.Throws<ArgumentException>(() => SafeCoreSourceTypeCodec.Parse(
            new string('&', SafeCoreSourceTypeCodec.MaximumDepth) + "i32", deadline.Token));
        AssertEx.Throws<ArgumentException>(() => SafeCoreSourceTypeCodec.Parse(
            "(" + string.Join(", ", Enumerable.Repeat("i32", SafeCoreSourceTypeCodec.MaximumNodes)) + ")", deadline.Token));
        AssertEx.Throws<ArgumentException>(() => SafeCoreSourceTypeCodec.Format(SafeCoreType.Adt("Point"), deadline.Token));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreSourceTypeCodec.Parse("&i32", cancelled.Token));
        return Task.CompletedTask;
    }

    private static RustSharpMetadataDocument SourceDocument() => new("safe-core-mir-p1-v2", new string('a', 64),
        [new("borrow", "Any,Any->Any", "crate::borrow")], callContracts:
        [new("crate::borrow", "unwind", ["borrow:shared", "borrow:shared"], "borrow:shared")
        {
            Schema = RustSharpMetadataCallContract.SourceSchema,
            SourceParameterTypes = ["&i32", "&i32"],
            SourceParameterStaticLifetimes = [false, false],
            SourceReturnType = "&i32",
            ReturnOrigins = ["parameter:0", "parameter:0", "parameter:1"],
        }], sourceValueTypes:
        [new("crate::Point", "point", [new("z", "i32"), new("a", "bool", false)], true)]);

    private static Task PositionalMetadataAsync()
    {
        RustSharpMetadataDocument document = SourceDocument();
        RustSharpMetadataDocument parsed = RustSharpMetadataDocument.Parse(document.Json);
        AssertEx.Equal(document.Json, parsed.Json);
        RustSharpMetadataCallContract contract = parsed.CallContracts.Single();
        AssertEx.True(contract.SourceParameterTypes!.SequenceEqual(["&i32", "&i32"]),
            "Repeated source parameter types must remain in separate positions.");
        AssertEx.True(contract.ReturnOrigins!.SequenceEqual(["parameter:0", "parameter:0", "parameter:1"]),
            "Repeated return origin evidence must not be deduplicated.");
        AssertEx.True(parsed.SourceValueTypes.Single().Fields.Select(value => value.Name).SequenceEqual(["z", "a"]),
            "Source fields must retain their declaration order.");
        RustSharpMetadataSourceOwner owner = new("Producer", "crate::Point", "point",
            Guid.Parse("11111111-1111-1111-1111-111111111111"), new string('b', 64), new string('c', 64));
        RustSharpMetadataDocument owned = new("safe-core-mir-p1-v2", new string('a', 64),
            sourceValueTypes: [new("crate::Point", "point", [new("z", "i32")], true) { Owner = owner }]);
        RustSharpMetadataSourceOwner parsedOwner = RustSharpMetadataDocument.Parse(owned.Json).SourceValueTypes.Single().Owner!;
        AssertEx.Equal(owner, parsedOwner);
        RustSharpMetadataSourceOwner structuralOwner = owner with { SourceName = "(i32, bool)", ClrName = "tuple" };
        RustSharpMetadataDocument structural = new("safe-core-mir-p1-v2", new string('a', 64),
            sourceStructuralTypes: [new("(i32, bool)", "Producer::tuple", structuralOwner)]);
        AssertEx.Equal(structural.Json, RustSharpMetadataDocument.Parse(structural.Json).Json);
        AssertEx.Equal(structuralOwner, RustSharpMetadataDocument.Parse(structural.Json).SourceStructuralTypes.Single().Owner);
        AssertEx.Throws<ArgumentException>(() => _ = new RustSharpMetadataDocument("safe-core-mir-p1-v2", new string('a', 64),
            sourceValueTypes: [new("crate::Point", "point", [], true)
            {
                Owner = owner with { AssemblySha256 = "bad" },
            }]));
        var legacy = new RustSharpMetadataDocument("safe-core-primitives-v1", new string('a', 64),
            [new("identity", "I32->I32")], callContracts: [new("identity", "unwind", ["copy"], "copy")]);
        AssertEx.False(legacy.Json.Contains("sourceValueTypes", StringComparison.Ordinal) ||
            legacy.Json.Contains("sourceParameterTypes", StringComparison.Ordinal) ||
            legacy.Json.Contains("sourceStructuralTypes", StringComparison.Ordinal) ||
            legacy.Json.Contains("returnOrigins", StringComparison.Ordinal),
            "Optional source extensions must leave the legacy scalar JSON shape intact.");
        return Task.CompletedTask;
    }

    private static Task RejectsMetadataAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string[] invalidOrigins = ["parameter:-1", "parameter:01", "parameter:256", "local:0", "static "];
        foreach (string origin in invalidOrigins)
        {
            deadline.Token.ThrowIfCancellationRequested();
            AssertEx.Throws<ArgumentException>(() => _ = new RustSharpMetadataDocument("safe-core-mir-p1-v2", new string('a', 64),
                callContracts: [new("borrow", "unwind") { ReturnOrigins = [origin] }]));
        }
        JsonObject unknown = JsonNode.Parse(SourceDocument().Json)!.AsObject();
        unknown["callContracts"]![0]!["sourceParamterTypes"] = new JsonArray("&i32");
        AssertEx.Throws<System.Text.Json.JsonException>(() => RustSharpMetadataDocument.Parse(unknown.ToJsonString()));
        JsonObject variants = JsonNode.Parse(SourceDocument().Json)!.AsObject();
        variants["sourceValueTypes"]![0]!["variants"] = new JsonArray();
        AssertEx.Throws<ArgumentException>(() => RustSharpMetadataDocument.Parse(variants.ToJsonString()));
        AssertEx.Throws<ArgumentException>(() => _ = new RustSharpMetadataDocument("safe-core-mir-p1-v2", new string('a', 64),
            sourceValueTypes: [new("crate::Point", "point", [], true, "crate::drop")]));
        return Task.CompletedTask;
    }

    private static Task RejectStructuralMetadataAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var owner = new RustSharpMetadataSourceOwner("Producer", "(i32, bool)", "tuple",
            Guid.Parse("11111111-1111-1111-1111-111111111111"), new string('a', 64), new string('b', 64));
        var binding = new RustSharpMetadataSourceStructuralType("(i32, bool)", "Producer::tuple", owner);
        var document = new RustSharpMetadataDocument("safe-core-mir-p1-v2", new string('a', 64), sourceStructuralTypes: [binding]);
        JsonObject unknown = JsonNode.Parse(document.Json)!.AsObject();
        unknown["sourceStructuralTypes"]![0]!["sourceTyp"] = "(i32, bool)";
        AssertEx.Throws<System.Text.Json.JsonException>(() => RustSharpMetadataDocument.Parse(unknown.ToJsonString()));
        JsonObject missing = JsonNode.Parse(document.Json)!.AsObject();
        missing["sourceStructuralTypes"]![0]!.AsObject().Remove("owner");
        AssertEx.Throws<System.Text.Json.JsonException>(() => RustSharpMetadataDocument.Parse(missing.ToJsonString()));
        JsonObject unknownOwner = JsonNode.Parse(document.Json)!.AsObject();
        unknownOwner["sourceStructuralTypes"]![0]!["owner"]!["moduleVerisonId"] = "11111111-1111-1111-1111-111111111111";
        AssertEx.Throws<System.Text.Json.JsonException>(() => RustSharpMetadataDocument.Parse(unknownOwner.ToJsonString()));
        AssertEx.Throws<System.Text.Json.JsonException>(() => RustSharpMetadataDocument.Parse(document.Json.Replace(
            "\"type\":\"(i32, bool)\"", "\"type\":\"(i32, bool)\",\"type\":\"(i32, bool)\"", StringComparison.Ordinal)));
        AssertEx.Throws<ArgumentException>(() => _ = new RustSharpMetadataDocument("safe-core-mir-p1-v2", new string('a', 64),
            sourceStructuralTypes: [binding, binding]));
        AssertEx.Throws<ArgumentException>(() => _ = new RustSharpMetadataDocument("safe-core-mir-p1-v2", new string('a', 64),
            sourceStructuralTypes: Enumerable.Repeat(binding, RustSharpMetadataDocument.MaximumValueTypes + 1)));
        AssertEx.Throws<ArgumentException>(() => _ = new RustSharpMetadataDocument("safe-core-mir-p1-v2", new string('a', 64),
            sourceStructuralTypes: [binding with { ClrName = "Other::tuple" }]));
        AssertEx.Throws<ArgumentException>(() => _ = new RustSharpMetadataDocument("safe-core-mir-p1-v2", new string('a', 64),
            sourceStructuralTypes: [binding with { Owner = owner with { SourceName = "[i32; 2]" } }]));
        AssertEx.Throws<ArgumentException>(() => _ = new RustSharpMetadataDocument("safe-core-mir-p1-v2", new string('a', 64),
            sourceStructuralTypes: [binding with { Owner = owner with { AssemblyName = "..\\Producer" } }]));
        deadline.Token.ThrowIfCancellationRequested();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        AssertEx.Throws<OperationCanceledException>(() => RustSharpMetadataConsumer.ReadAssembly(
            Path.Combine(Path.GetTempPath(), "rustsharp-p1-cancelled-owner-read.dll"), cancellationToken: cancelled.Token));
        AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(5), "Cancellation must be observed before opening a producer or computing body fingerprints.");
        return Task.CompletedTask;
    }

    private const string ProducerSource = "pub struct Pair { pub left: i32, pub right: i32 } " +
        "pub fn make_pair(left: i32, right: i32) -> Pair { Pair { left, right } } " +
        "pub fn consume_pair(value: Pair) -> i32 { value.left + value.right } " +
        "pub fn ref_identity(value: &i32) -> &i32 { value } fn main() {}";
    private const string ReferenceConsumer = "use SourceContractProducer::ref_identity; " +
        "fn main() { let value = 42; let borrowed = ref_identity(&value); println!(\"{}\", *borrowed); }";

    private static async Task SourceReferencePackagesAsync()
    {
        string directory = NewDirectory();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            string producer = CompileProducer(directory, deadline.Token);
            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(producer, "safe-core-mir-p1-v2");
            AssertEx.True(imported.IsSuccessful, string.Join("; ", imported.Diagnostics));
            RustSharpMetadataCallContract contract = imported.Document!.CallContracts.Single(value =>
                value.FunctionId.Contains("ref_identity", StringComparison.Ordinal));
            AssertEx.Equal(RustSharpMetadataCallContract.SourceSchema, contract.Schema!);
            AssertEx.True(contract.SourceParameterTypes!.SequenceEqual(["&i32"]), "The ref parameter must keep its erased source identity.");
            AssertEx.Equal("&i32", contract.SourceReturnType!);
            AssertEx.True(contract.ReturnOrigins!.SequenceEqual(["parameter:0"]), "The returned borrow must name its source parameter.");
            string output = Path.Combine(directory, "SourceContractConsumer.dll");
            CompilationResult consumer = CompilerDriver.CompileWithMetadataReferences(ReferenceConsumer,
                Path.Combine(directory, "consumer.rs"), output, "SourceContractConsumer", CompilationProfile.SafeCoreMirV2,
                [producer], cancellationToken: deadline.Token);
            AssertEx.True(consumer.Success, string.Join("; ", consumer.Diagnostics));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(new("dotnet", [output], directory,
                TimeSpan.FromSeconds(5)), deadline.Token).ConfigureAwait(false);
            Console.WriteLine("P1-09 source reference process PID=" + run.StartedProcess.ProcessId + "; parent=" +
                run.StartedProcess.ParentProcessId + "; started=" + run.StartedProcess.StartedAt.ToString("O") +
                "; command=dotnet " + output + "; cleanupIncomplete=" + run.ProcessTreeCleanupIncomplete);
            AssertEx.True(run.Succeeded && !run.ProcessTreeCleanupIncomplete && !run.OutputTruncated,
                "The real source consumer must exit cleanly: " + run.StandardError);
            AssertEx.Equal("42\n", run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally { DeleteDirectory(directory); }
    }

    private static Task SourceSchemaMutationsAsync()
    {
        string directory = NewDirectory();
        string[] mutations = ["missing-types", "missing-return-type", "missing-origins", "schema-downgrade",
            "invalid-origin", "duplicate-terms", "unknown-term", "field-type-drift", "field-private", "copy-drop", "erased-ref-type-drift",
            "missing-body-evidence", "stale-body-evidence", "stale-helper-evidence", "method-static-flag"];
        AssertEx.True(mutations.Length <= 16, "The source mutation denominator must remain bounded.");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            string producer = CompileProducer(directory, deadline.Token);
            RustSharpMetadataImportResult original = RustSharpMetadataConsumer.ReadAssembly(producer, "safe-core-mir-p1-v2");
            AssertEx.True(original.IsSuccessful, string.Join("; ", original.Diagnostics));
            byte[] image = File.ReadAllBytes(producer);
            byte[] originalJson = Encoding.UTF8.GetBytes(original.Document!.Json);
            string referenceMethodName = RustSharpMetadataConsumer.FindFunction(original.Document, "crate::ref_identity")!.Name;
            int offset = image.AsSpan().IndexOf(originalJson);
            AssertEx.True(offset >= 0, "The producer PE must retain its canonical metadata JSON.");
            for (int index = 0; index < mutations.Length; index++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                string mutation = mutations[index];
                JsonObject changed = JsonNode.Parse(original.Document.Json)!.AsObject();
                changed.Remove("cleanupSnapshot");
                JsonObject reference = changed["callContracts"]!.AsArray().Select(value => value!.AsObject()).Single(value =>
                    value["functionId"]!.GetValue<string>().Contains("ref_identity", StringComparison.Ordinal));
                JsonObject pair = changed["sourceValueTypes"]!.AsArray().Select(value => value!.AsObject()).Single(value =>
                    value["name"]!.GetValue<string>().EndsWith("::Pair", StringComparison.Ordinal));
                switch (mutation)
                {
                    case "missing-types": reference.Remove("sourceParameterTypes"); break;
                    case "missing-return-type": reference.Remove("sourceReturnType"); break;
                    case "missing-origins": reference.Remove("returnOrigins"); break;
                    case "schema-downgrade": reference["schema"] = RustSharpMetadataCallContract.ScalarSchema; break;
                    case "invalid-origin": reference["returnOrigins"] = new JsonArray("parameter:1"); break;
                    case "duplicate-terms": reference["parameterContracts"] = new JsonArray("borrow:shared", "borrow:shared"); break;
                    case "unknown-term": reference["returnContract"] = "borrow:unknown"; break;
                    case "field-type-drift": pair["fields"]![0]!["type"] = "bool"; break;
                    case "field-private": pair["fields"]![0]!["isPublic"] = false; break;
                    case "copy-drop": pair["isCopy"] = true; pair["dropFunctionId"] = "crate::ref_identity"; break;
                    case "erased-ref-type-drift": reference["sourceParameterTypes"] = new JsonArray("&bool"); break;
                    case "missing-body-evidence": changed.Remove("emittedMethodBodies"); break;
                    case "stale-body-evidence":
                    case "stale-helper-evidence": break;
                    case "method-static-flag": break;
                    default: throw new InvalidOperationException("Unknown fixed source metadata mutation.");
                }
                byte[] replacement = Encoding.UTF8.GetBytes(changed.ToJsonString());
                AssertEx.True(replacement.Length <= originalJson.Length, "The mutation must fit without modifying producer MethodDefs.");
                byte[] patched = (byte[])image.Clone();
                patched.AsSpan(offset, originalJson.Length).Fill((byte)' ');
                replacement.CopyTo(patched.AsSpan(offset));
                if (mutation is "stale-body-evidence" or "stale-helper-evidence")
                    MutateActualBody(patched, referenceMethodName, helper: mutation == "stale-helper-evidence", deadline.Token);
                if (mutation == "method-static-flag") MutateActualStaticFlag(patched, referenceMethodName, deadline.Token);
                File.WriteAllBytes(producer, patched);
                string output = Path.Combine(directory, "RejectedSourceConsumer.dll");
                string source = mutation == "field-private"
                    ? "use SourceContractProducer::make_pair; fn main() { let pair = make_pair(20, 22); println!(\"{}\", pair.left); }"
                    : ReferenceConsumer;
                CompilationResult consumer = CompilerDriver.CompileWithMetadataReferences(source,
                    Path.Combine(directory, "consumer.rs"), output, "RejectedSourceConsumer", CompilationProfile.SafeCoreMirV2,
                    [producer], cancellationToken: deadline.Token);
                AssertEx.False(consumer.Success || File.Exists(output), "The real source consumer must reject before PE emission: " + mutation);
                if (mutation != "field-private")
                    AssertEx.True(consumer.Diagnostics.Any(value => value.Code.StartsWith(RustSharpMetadataConsumer.InvalidMetadata, StringComparison.Ordinal)),
                        "The mutation must carry the stable metadata diagnostic: " + mutation);
                Console.WriteLine("P1-09 source contract PE mutations " + (index + 1) + "/" + mutations.Length + ": " + mutation);
            }
        }
        finally { DeleteDirectory(directory); }
        return Task.CompletedTask;
    }

    private static string CompileProducer(string directory, CancellationToken cancellationToken)
    {
        string source = Path.Combine(directory, "producer.rs");
        string output = Path.Combine(directory, "SourceContractProducer.dll");
        File.WriteAllText(source, ProducerSource);
        CompilationResult result = CompilerDriver.CompileFile(source, output, "SourceContractProducer",
            CompilationProfile.SafeCoreMirV2, cancellationToken);
        AssertEx.True(result.Success, string.Join("; ", result.Diagnostics));
        return output;
    }

    private static async Task MemberReferenceDriftAsync()
    {
        string directory = NewDirectory();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            const string producerSource = "fn dummy0() {} fn dummy1() {} fn dummy2() {} fn dummy3() {} " +
                "pub fn first(value: &(i32, i32)) -> &i32 { &value.0 } " +
                "pub fn second(value: &(i32, i32)) -> &i32 { &value.1 } fn main() {}";
            string producerFile = Path.Combine(directory, "producer.rs");
            string producerPath = Path.Combine(directory, "MemberTargetProducer.dll");
            File.WriteAllText(producerFile, producerSource);
            CompilationResult producer = CompilerDriver.CompileFile(producerFile, producerPath, "MemberTargetProducer",
                CompilationProfile.SafeCoreMirV2, deadline.Token);
            AssertEx.True(producer.Success, string.Join("; ", producer.Diagnostics));
            RustSharpMetadataImportResult producerMetadata = RustSharpMetadataConsumer.ReadAssembly(producerPath,
                "safe-core-mir-p1-v2", cancellationToken: deadline.Token);
            AssertEx.True(producerMetadata.IsSuccessful, string.Join("; ", producerMetadata.Diagnostics));
            RustSharpMetadataFunction first = RustSharpMetadataConsumer.FindFunction(producerMetadata.Document!, "crate::first")!;
            RustSharpMetadataFunction second = RustSharpMetadataConsumer.FindFunction(producerMetadata.Document!, "crate::second")!;
            AssertEx.True(first.Name != second.Name && first.Signature == second.Signature,
                "The producer targets must have distinct actual CLR names with the same checked erased signature.");
            RustSharpMetadataCallContract firstContract = producerMetadata.Document!.CallContracts.Single(value =>
                RustSharpMetadataConsumer.FindFunction(producerMetadata.Document, value.FunctionId)?.Name == first.Name);
            RustSharpMetadataCallContract secondContract = producerMetadata.Document.CallContracts.Single(value =>
                RustSharpMetadataConsumer.FindFunction(producerMetadata.Document, value.FunctionId)?.Name == second.Name);
            AssertEx.False(firstContract.ReturnOrigins!.SequenceEqual(secondContract.ReturnOrigins!),
                "The two actual producer targets must return different projected parameter fields.");

            const string wrapperSource = "use MemberTargetProducer::first; use MemberTargetProducer::second; " +
                "pub fn from_first(value: &(i32, i32)) -> &i32 { first(value) } " +
                "pub fn from_second(value: &(i32, i32)) -> &i32 { second(value) } " +
                "pub fn message() { println!(\"before\"); } fn main() {}";
            string wrapperPath = Path.Combine(directory, "MemberTargetWrapper.dll");
            CompilationResult wrapper = CompilerDriver.CompileWithMetadataReferences(wrapperSource,
                Path.Combine(directory, "wrapper.rs"), wrapperPath, "MemberTargetWrapper", CompilationProfile.SafeCoreMirV2,
                [producerPath], cancellationToken: deadline.Token);
            AssertEx.True(wrapper.Success, string.Join("; ", wrapper.Diagnostics));
            RustSharpMetadataImportResult original = RustSharpMetadataConsumer.ReadAssembly(wrapperPath,
                "safe-core-mir-p1-v2", dependencyPaths: [producerPath], cancellationToken: deadline.Token);
            AssertEx.True(original.IsSuccessful, string.Join("; ", original.Diagnostics));
            const string consumerSource = "use MemberTargetWrapper::from_first; use MemberTargetWrapper::message; " +
                "fn main() { let pair = (42, 7); let borrowed = from_first(&pair); message(); println!(\"{}\", *borrowed); }";
            string consumerPath = Path.Combine(directory, "MemberTargetConsumer.dll");
            CompilationResult consumer = CompilerDriver.CompileWithMetadataReferences(consumerSource,
                Path.Combine(directory, "consumer.rs"), consumerPath, "MemberTargetConsumer", CompilationProfile.SafeCoreMirV2,
                [wrapperPath, producerPath], cancellationToken: deadline.Token);
            AssertEx.True(consumer.Success, string.Join("; ", consumer.Diagnostics));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(new("dotnet", [consumerPath], directory,
                TimeSpan.FromSeconds(5)), deadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded && !run.ProcessTreeCleanupIncomplete && !run.OutputTruncated, run.StandardError);
            AssertEx.Equal("before\n42\n", run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
            Console.WriteLine("P1-09 MemberRef baseline process PID=" + run.StartedProcess.ProcessId + "; parent=" +
                run.StartedProcess.ParentProcessId + "; started=" + run.StartedProcess.StartedAt.ToString("O") +
                "; command=dotnet " + consumerPath + "; cleanupIncomplete=" + run.ProcessTreeCleanupIncomplete);

            byte[] originalImage = File.ReadAllBytes(wrapperPath);
            string[] mutations = ["member-reference", "user-string"];
            foreach (string mutation in mutations)
            {
                deadline.Token.ThrowIfCancellationRequested();
                byte[] image = (byte[])originalImage.Clone();
                if (mutation == "member-reference") MutateActualMemberReference(image, first.Name, second.Name, deadline.Token);
                else MutateActualUserString(image, "before", "after!", deadline.Token);
                File.WriteAllBytes(wrapperPath, image);
                RustSharpMetadataImportResult changed = RustSharpMetadataConsumer.ReadAssembly(wrapperPath,
                    "safe-core-mir-p1-v2", dependencyPaths: [producerPath], cancellationToken: deadline.Token);
                AssertEx.Equal(original.Document!.Json, changed.Document!.Json);
                AssertEx.False(changed.IsSuccessful, "Changed actual token semantics must invalidate unchanged body stamps: " + mutation);
                AssertEx.True(changed.Diagnostics.Any(value => value.StartsWith(RustSharpMetadataConsumer.InvalidMetadata, StringComparison.Ordinal) &&
                    value.Contains("method body evidence", StringComparison.Ordinal)), string.Join("; ", changed.Diagnostics));
                string rejectedPath = Path.Combine(directory, "RejectedMemberTargetConsumer.dll");
                CompilationResult rejected = CompilerDriver.CompileWithMetadataReferences(consumerSource,
                    Path.Combine(directory, "rejected-consumer.rs"), rejectedPath, "RejectedMemberTargetConsumer",
                    CompilationProfile.SafeCoreMirV2, [wrapperPath, producerPath], cancellationToken: deadline.Token);
                AssertEx.False(rejected.Success || File.Exists(rejectedPath),
                    "The source consumer must reject the stale token contract before emitting its PE: " + mutation);
                AssertEx.True(rejected.Diagnostics.Any(value => value.Code == RustSharpMetadataConsumer.InvalidMetadata),
                    string.Join("; ", rejected.Diagnostics));
            }
        }
        finally { DeleteDirectory(directory); }
    }

    private static void MutateActualMemberReference(byte[] image, string fromName, string toName,
        CancellationToken cancellationToken)
    {
        byte[] original = (byte[])image.Clone();
        using var stream = new MemoryStream(image, writable: false);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        AssertEx.True(metadata.MemberReferences.Count <= RustSharpMetadataDocument.MaximumMethodBodies,
            "The actual MemberRef mutation must have a fixed row budget.");
        MemberReferenceHandle from = default;
        MemberReferenceHandle to = default;
        foreach (MemberReferenceHandle handle in metadata.MemberReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(5), "The MemberRef mutation must have a wall-clock budget.");
            string name = metadata.GetString(metadata.GetMemberReference(handle).Name);
            if (name == fromName) { AssertEx.True(from.IsNil, "The original target must identify one exact MemberRef."); from = handle; }
            if (name == toName) { AssertEx.True(to.IsNil, "The replacement target must identify one exact MemberRef."); to = handle; }
        }
        AssertEx.True(!from.IsNil && !to.IsNil, "The wrapper must invoke both independently built producer targets.");
        MemberReference first = metadata.GetMemberReference(from);
        MemberReference second = metadata.GetMemberReference(to);
        AssertEx.Equal(first.Parent, second.Parent);
        AssertEx.True(metadata.GetBlobBytes(first.Signature).SequenceEqual(metadata.GetBlobBytes(second.Signature)),
            "The mutation must preserve a valid call signature and parent while changing only the actual target name.");
        int strings = metadata.GetHeapSize(HeapIndex.String) > ushort.MaxValue ? sizeof(int) : sizeof(ushort);
        int blobs = metadata.GetHeapSize(HeapIndex.Blob) > ushort.MaxValue ? sizeof(int) : sizeof(ushort);
        int rowSize = metadata.GetTableRowSize(TableIndex.MemberRef);
        int offset = checked(pe.PEHeaders.MetadataStartOffset + metadata.GetTableMetadataOffset(TableIndex.MemberRef) +
            (MetadataTokens.GetRowNumber(from) - 1) * rowSize + rowSize - strings - blobs);
        int replacement = MetadataTokens.GetHeapOffset(second.Name);
        if (strings == sizeof(ushort)) BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset, strings), checked((ushort)replacement));
        else BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(offset, strings), replacement);
        AssertEx.True(original.AsSpan(0, offset).SequenceEqual(image.AsSpan(0, offset)) &&
            original.AsSpan(offset + strings).SequenceEqual(image.AsSpan(offset + strings)),
            "Every IL byte, MethodDef, owner reference and metadata JSON byte must stay unchanged outside the exact MemberRef Name index.");
    }

    private static void MutateActualUserString(byte[] image, string fromValue, string toValue,
        CancellationToken cancellationToken)
    {
        AssertEx.Equal(fromValue.Length, toValue.Length);
        using var stream = new MemoryStream(image, writable: false);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        int heapSize = metadata.GetHeapSize(HeapIndex.UserString);
        AssertEx.True(heapSize <= 64 * 1024 * 1024, "The actual user string mutation has a fixed heap-byte budget.");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        UserStringHandle handle = MetadataTokens.UserStringHandle(0);
        for (int index = 0; index < RustSharpMetadataDocument.MaximumMethodBodies; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(5), "The user string mutation has a wall-clock budget.");
            handle = metadata.GetNextHandle(handle);
            if (handle.IsNil) break;
            if (metadata.GetUserString(handle) != fromValue) continue;
            int heapOffset = MetadataTokens.GetHeapOffset(handle);
            int absolute = checked(pe.PEHeaders.MetadataStartOffset + metadata.GetHeapMetadataOffset(HeapIndex.UserString) + heapOffset);
            byte prefix = image[absolute];
            int prefixLength = (prefix & 0x80) == 0 ? 1 : (prefix & 0xc0) == 0x80 ? 2 : 4;
            byte[] original = (byte[])image.Clone();
            byte[] replacement = Encoding.Unicode.GetBytes(toValue);
            int valueOffset = checked(absolute + prefixLength);
            replacement.CopyTo(image.AsSpan(valueOffset));
            AssertEx.True(original.AsSpan(0, valueOffset).SequenceEqual(image.AsSpan(0, valueOffset)) &&
                original.AsSpan(valueOffset + replacement.Length).SequenceEqual(image.AsSpan(valueOffset + replacement.Length)),
                "The actual ldstr payload must be the only changed PE bytes; its IL token and JSON evidence must stay unchanged.");
            return;
        }
        throw new InvalidOperationException("The wrapper must contain its actual exported message's ldstr literal.");
    }

    private static void MutateActualBody(byte[] image, string referenceMethodName, bool helper, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(image, writable: false);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        AssertEx.True(metadata.MethodDefinitions.Count <= RustSharpMetadataDocument.MaximumMethodBodies,
            "The actual-body mutation search must have a fixed method-row bound.");
        AssertEx.True(pe.PEHeaders.SectionHeaders.Length <= 96, "The actual-body mutation search must have a fixed section bound.");
        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(5), "The actual-body mutation search must have a wall-clock bound.");
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            TypeDefinition owner = metadata.GetTypeDefinition(method.GetDeclaringType());
            string name = metadata.GetString(method.Name);
            bool selected = helper
                ? metadata.GetString(owner.Namespace) == "RustSharp.Generated.Values" && method.RelativeVirtualAddress != 0
                : name == referenceMethodName;
            if (!selected) continue;
            int rva = method.RelativeVirtualAddress;
            foreach (SectionHeader section in pe.PEHeaders.SectionHeaders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(5), "The section search must share the wall-clock bound.");
                if (rva < section.VirtualAddress || rva >= section.VirtualAddress + section.SizeOfRawData) continue;
                int start = checked(section.PointerToRawData + rva - section.VirtualAddress);
                int headerSize = (image[start] & 3) == 2 ? 1 :
                    checked((BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(start, 2)) >> 12) * 4);
                int instruction = checked(start + headerSize);
                AssertEx.True(pe.GetMethodBody(rva).GetILContent().Length > 0, "A body drift must modify a real IL instruction.");
                image[instruction] = image[instruction] == 0 ? (byte)1 : (byte)0;
                return;
            }
            throw new InvalidOperationException("The selected body must belong to one actual PE section.");
        }
        throw new InvalidOperationException("The fixture must contain the selected source or generated helper MethodDef.");
    }

    private static void MutateActualStaticFlag(byte[] image, string methodName, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(image, writable: false);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        AssertEx.True(metadata.MethodDefinitions.Count <= RustSharpMetadataDocument.MaximumMethodBodies,
            "The static flag mutation must have a fixed method-row budget.");
        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(5), "The static flag mutation must have a wall-clock budget.");
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if (metadata.GetString(method.Name) != methodName) continue;
            int offset = checked(pe.PEHeaders.MetadataStartOffset + metadata.GetTableMetadataOffset(TableIndex.MethodDef) +
                (MetadataTokens.GetRowNumber(handle) - 1) * metadata.GetTableRowSize(TableIndex.MethodDef) + 6);
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset, 2));
            AssertEx.True((flags & (ushort)System.Reflection.MethodAttributes.Static) != 0,
                "The fixture must expose an actual static MethodDef before the mutation.");
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset, 2), checked((ushort)(flags & ~(ushort)System.Reflection.MethodAttributes.Static)));
            return;
        }
        throw new InvalidOperationException("The producer fixture must contain the selected actual static method.");
    }

    private static Task StaticFieldsAsync()
    {
        string directory = NewDirectory();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            string source = Path.Combine(directory, "producer.rs");
            string output = Path.Combine(directory, "StaticSourceProducer.dll");
            File.WriteAllText(source, "pub struct Holder { pub value: &'static i32 } " +
                "pub fn make(value: &'static i32) -> Holder { Holder { value } } fn main() {}");
            CompilationResult compiled = CompilerDriver.CompileFile(source, output, "StaticSourceProducer",
                CompilationProfile.SafeCoreMirV2, deadline.Token);
            AssertEx.True(compiled.Success, "Local static field compilation must keep its checked behavior: " + string.Join("; ", compiled.Diagnostics));
            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(output, "safe-core-mir-p1-v2");
            AssertEx.True(imported.IsSuccessful, "Supported static requirements must survive source import: " + string.Join("; ", imported.Diagnostics));
            AssertEx.True(imported.Document!.SourceValueTypes.Single().Fields.Single().RequiresStaticLifetime,
                "The source field must retain its independently checked static lifetime.");
            RustSharpMetadataCallContract contract = imported.Document.CallContracts.Single(value => value.FunctionId.Contains("make", StringComparison.Ordinal));
            AssertEx.True(contract.SourceParameterStaticLifetimes!.SequenceEqual([true]),
                "The source parameter's static requirement must retain its position.");
            byte[] original = File.ReadAllBytes(output);
            byte[] json = Encoding.UTF8.GetBytes(imported.Document.Json);
            int offset = original.AsSpan().IndexOf(json);
            AssertEx.True(offset >= 0, "The static producer must embed its canonical metadata.");
            string[] mutations = ["field-static", "parameter-static"];
            foreach (string mutation in mutations)
            {
                deadline.Token.ThrowIfCancellationRequested();
                JsonObject changed = JsonNode.Parse(imported.Document.Json)!.AsObject();
                if (mutation == "field-static") changed["sourceValueTypes"]![0]!["fields"]![0]!["requiresStaticLifetime"] = false;
                else changed["callContracts"]!.AsArray().Single(value => value!["functionId"]!.GetValue<string>() == contract.FunctionId)!["sourceParameterStaticLifetimes"] = new JsonArray(false);
                changed.Remove("cleanupSnapshot");
                byte[] replacement = Encoding.UTF8.GetBytes(changed.ToJsonString());
                AssertEx.True(replacement.Length <= json.Length, "The static drift must fit the original PE metadata attribute.");
                byte[] image = (byte[])original.Clone();
                image.AsSpan(offset, json.Length).Fill((byte)' ');
                replacement.CopyTo(image.AsSpan(offset));
                File.WriteAllBytes(output, image);
                RustSharpMetadataImportResult rejected = RustSharpMetadataConsumer.ReadAssembly(output, "safe-core-mir-p1-v2");
                AssertEx.False(rejected.IsSuccessful, "Static source requirement drift must reject against independent MIR evidence: " + mutation);
                AssertEx.True(rejected.Diagnostics.Any(value => value.StartsWith(RustSharpMetadataConsumer.InvalidMetadata, StringComparison.Ordinal)),
                    "Static lifetime drift must retain its stable metadata diagnostic.");
            }
            File.WriteAllBytes(output, original);
            string consumerPath = Path.Combine(directory, "RejectedStaticConsumer.dll");
            CompilationResult consumer = CompilerDriver.CompileWithMetadataReferences(
                "use StaticSourceProducer::make; fn main() { let value = 42; let holder = make(&value); }",
                Path.Combine(directory, "consumer.rs"), consumerPath, "RejectedStaticConsumer", CompilationProfile.SafeCoreMirV2,
                [output], cancellationToken: deadline.Token);
            AssertEx.False(consumer.Success || File.Exists(consumerPath), "A short local borrow cannot satisfy an imported static input contract.");
        }
        finally { DeleteDirectory(directory); }
        return Task.CompletedTask;
    }

    private static async Task OwnerBindingsAsync()
    {
        string directory = NewDirectory();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            string producer = CompileProducer(directory, deadline.Token);
            string wrapper = Path.Combine(directory, "SourceOwnerWrapper.dll");
            CompilationResult wrapped = CompilerDriver.CompileWithMetadataReferences(
                "use SourceContractProducer::Pair; pub fn pass(value: Pair) -> Pair { value } fn main() {}",
                Path.Combine(directory, "wrapper.rs"), wrapper, "SourceOwnerWrapper", CompilationProfile.SafeCoreMirV2,
                [producer], cancellationToken: deadline.Token);
            AssertEx.True(wrapped.Success, "The independent source wrapper must compile with its original nominal owner: " + string.Join("; ", wrapped.Diagnostics));
            RustSharpMetadataImportResult original = RustSharpMetadataConsumer.ReadAssembly(wrapper,
                "safe-core-mir-p1-v2", dependencyPaths: [producer], cancellationToken: deadline.Token);
            AssertEx.True(original.IsSuccessful, "The source wrapper must reconcile its original owner PE: " + string.Join("; ", original.Diagnostics));
            RustSharpMetadataSourceValueType owned = original.Document!.SourceValueTypes.Single(value => value.Owner is not null);
            AssertEx.Equal(Path.GetFullPath(producer), original.ResolvedOwnerPaths[owned.Owner!.AssemblyName]);
            using (var stream = new FileStream(wrapper, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var pe = new PEReader(stream))
            {
                MetadataReader metadata = pe.GetMetadataReader();
                AssertEx.True(metadata.TypeDefinitions.Count <= RustSharpMetadataDocument.MaximumValueTypes + 2,
                    "The wrapper TypeDef witness must have a fixed table-row budget.");
                AssertEx.False(metadata.TypeDefinitions.Any(handle => metadata.GetString(metadata.GetTypeDefinition(handle).Name) == owned.Owner.ClrName),
                    "The wrapper must reference the original producer value type without creating a duplicate nominal TypeDef.");
            }
            string successfulConsumer = Path.Combine(directory, "SourceOwnerConsumer.dll");
            string consumerSource = "use SourceContractProducer::make_pair; use SourceOwnerWrapper::pass; " +
                "fn main() { let value = pass(make_pair(20, 22)); println!(\"{}\", value.left + value.right); }";
            CompilationResult accepted = CompilerDriver.CompileWithMetadataReferences(consumerSource,
                Path.Combine(directory, "owner-consumer.rs"), successfulConsumer, "SourceOwnerConsumer", CompilationProfile.SafeCoreMirV2,
                [producer, wrapper], cancellationToken: deadline.Token);
            AssertEx.True(accepted.Success, "A real three-package consumer must retain the original nominal identity: " + string.Join("; ", accepted.Diagnostics));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(new("dotnet", [successfulConsumer], directory,
                TimeSpan.FromSeconds(5)), deadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded && !run.ProcessTreeCleanupIncomplete && !run.OutputTruncated,
                "The three-package source consumer must execute and reclaim its process tree: " + run.StandardError);
            AssertEx.Equal("42\n", run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
            Console.WriteLine("P1-09 owner consumer process PID=" + run.StartedProcess.ProcessId + "; parent=" + run.StartedProcess.ParentProcessId +
                "; started=" + run.StartedProcess.StartedAt.ToString("O") + "; command=dotnet " + successfulConsumer + "; cleanupIncomplete=" + run.ProcessTreeCleanupIncomplete);
            byte[] image = File.ReadAllBytes(wrapper);
            byte[] json = Encoding.UTF8.GetBytes(original.Document.Json);
            int offset = image.AsSpan().IndexOf(json);
            AssertEx.True(offset >= 0, "The wrapper must embed its canonical owner proof.");
            string[] mutations = ["missing-owner", "owner-hash", "owner-mvid", "owner-source", "owner-field"];
            for (int index = 0; index < mutations.Length; index++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                string mutation = mutations[index];
                JsonObject changed = JsonNode.Parse(original.Document.Json)!.AsObject();
                changed.Remove("cleanupSnapshot");
                JsonObject layout = changed["sourceValueTypes"]!.AsArray().Select(value => value!.AsObject())
                    .Single(value => value["name"]!.GetValue<string>() == owned.Name);
                JsonObject owner = layout["owner"]!.AsObject();
                switch (mutation)
                {
                    case "missing-owner": layout.Remove("owner"); break;
                    case "owner-hash": owner["assemblySha256"] = new string('0', 64); break;
                    case "owner-mvid": owner["moduleVersionId"] = "11111111-1111-1111-1111-111111111111"; break;
                    case "owner-source": owner["sourceSha256"] = new string('0', 64); break;
                    case "owner-field": layout["fields"]![0]!["type"] = "bool"; break;
                    default: throw new InvalidOperationException("Unknown bounded owner proof mutation.");
                }
                byte[] replacement = Encoding.UTF8.GetBytes(changed.ToJsonString());
                AssertEx.True(replacement.Length <= json.Length, "The owner proof mutation must preserve actual wrapper MethodDefs.");
                byte[] patched = (byte[])image.Clone();
                patched.AsSpan(offset, json.Length).Fill((byte)' ');
                replacement.CopyTo(patched.AsSpan(offset));
                File.WriteAllBytes(wrapper, patched);
                string output = Path.Combine(directory, "RejectedOwnerConsumer.dll");
                CompilationResult rejected = CompilerDriver.CompileWithMetadataReferences(
                    consumerSource,
                    Path.Combine(directory, "consumer.rs"), output, "RejectedOwnerConsumer", CompilationProfile.SafeCoreMirV2,
                    [producer, wrapper], cancellationToken: deadline.Token);
                AssertEx.False(rejected.Success || File.Exists(output), "Source consumers must reject unbound owner facts before emission: " + mutation);
                AssertEx.True(rejected.Diagnostics.Any(value => value.Code.StartsWith(RustSharpMetadataConsumer.InvalidMetadata, StringComparison.Ordinal)),
                    "Owner proof drift must retain stable RSC0011 diagnostics: " + mutation);
                Console.WriteLine("P1-09 source owner PE mutations " + (index + 1) + "/" + mutations.Length + ": " + mutation);
            }
        }
        finally { DeleteDirectory(directory); }
    }

    private static Task StaticMutableAsync()
    {
        string directory = NewDirectory();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            string source = Path.Combine(directory, "producer.rs");
            string output = Path.Combine(directory, "StaticMutableProducer.dll");
            File.WriteAllText(source, "pub fn relay(value: &'static mut i32) -> &'static mut i32 { value } " +
                "pub fn relay_second(first: &'static i32, value: &'static mut i32) -> &'static mut i32 { relay(value) } fn main() {}");
            CompilationResult compiled = CompilerDriver.CompileFile(source, output, "StaticMutableProducer",
                CompilationProfile.SafeCoreMirV2, deadline.Token);
            AssertEx.True(compiled.Success, "A checked static mutable parameter relay must compile: " + string.Join("; ", compiled.Diagnostics));
            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(output,
                "safe-core-mir-p1-v2", cancellationToken: deadline.Token);
            AssertEx.True(imported.IsSuccessful, "Static mutable parameter roots must round-trip through actual source metadata: " + string.Join("; ", imported.Diagnostics));
            RustSharpMetadataCallContract contract = imported.Document!.CallContracts.Single(value =>
                value.FunctionId.Contains("relay", StringComparison.Ordinal) && !value.FunctionId.Contains("relay_second", StringComparison.Ordinal));
            AssertEx.True(contract.SourceParameterStaticLifetimes!.SequenceEqual([true]), "The mutable parameter's static requirement must remain explicit.");
            SafeCoreMirReferenceOrigin origin = SafeCoreSourceOriginCodec.Parse(contract.ReturnOrigins!.Single(), deadline.Token);
            AssertEx.True(origin.IsParameter && origin.IsStatic && origin.IsMutable,
                "A static mutable return must preserve its checked mutable parameter origin.");
            RustSharpMetadataCallContract forwarded = imported.Document.CallContracts.Single(value => value.FunctionId.Contains("relay_second", StringComparison.Ordinal));
            SafeCoreMirReferenceOrigin forwardedOrigin = SafeCoreSourceOriginCodec.Parse(forwarded.ReturnOrigins!.Single(), deadline.Token);
            AssertEx.True(forwardedOrigin.IsParameter && forwardedOrigin.IsStatic && forwardedOrigin.IsMutable && forwardedOrigin.LocalId == 1,
                "Static mutable callee origins must substitute the caller's exact parameter index.");
            string consumerOutput = Path.Combine(directory, "RejectedStaticMutableConsumer.dll");
            CompilationResult consumer = CompilerDriver.CompileWithMetadataReferences(
                "use StaticMutableProducer::relay; fn main() { let mut value = 42; let result = relay(&mut value); }",
                Path.Combine(directory, "consumer.rs"), consumerOutput, "RejectedStaticMutableConsumer", CompilationProfile.SafeCoreMirV2,
                [output], cancellationToken: deadline.Token);
            AssertEx.False(consumer.Success || File.Exists(consumerOutput), "A mutable stack borrow cannot satisfy an imported static mutable parameter.");
        }
        finally { DeleteDirectory(directory); }
        return Task.CompletedTask;
    }

    private static async Task EnumProjectionAsync()
    {
        string directory = NewDirectory();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            string source = Path.Combine(directory, "producer.rs");
            string producer = Path.Combine(directory, "EnumProjectionProducer.dll");
            File.WriteAllText(source, "pub enum Value { Empty, Number(i32) } " +
                "pub fn project(value: &Value) -> &i32 { match value { Value::Empty => &0, Value::Number(n) => n } } fn main() {}");
            CompilationResult compiled = CompilerDriver.CompileFile(source, producer, "EnumProjectionProducer",
                CompilationProfile.SafeCoreMirV2, deadline.Token);
            AssertEx.True(compiled.Success, "The borrowed enum producer must compile with its typed variant projection: " + string.Join("; ", compiled.Diagnostics));
            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(producer,
                "safe-core-mir-p1-v2", cancellationToken: deadline.Token);
            AssertEx.True(imported.IsSuccessful, "The borrowed enum source origins must import losslessly: " + string.Join("; ", imported.Diagnostics));
            RustSharpMetadataCallContract contract = imported.Document!.CallContracts.Single(value => value.FunctionId.Contains("project", StringComparison.Ordinal));
            SafeCoreMirReferenceOrigin projected = contract.ReturnOrigins!.Select(value => SafeCoreSourceOriginCodec.Parse(value, deadline.Token))
                .Single(value => value.IsParameter);
            AssertEx.True(projected.Projections.SequenceEqual([SafeCoreMirProjection.Field("$v1$0")]),
                "Typed enum downcasts must normalize to the original physical payload field in exported origins.");
            string consumer = Path.Combine(directory, "EnumProjectionConsumer.dll");
            CompilationResult consumed = CompilerDriver.CompileWithMetadataReferences(
                "use EnumProjectionProducer::{Value, project}; fn main() { let value = Value::Number(42); println!(\"{}\", *project(&value)); }",
                Path.Combine(directory, "consumer.rs"), consumer, "EnumProjectionConsumer", CompilationProfile.SafeCoreMirV2,
                [producer], cancellationToken: deadline.Token);
            AssertEx.True(consumed.Success, "The source consumer must map original enum payload provenance: " + string.Join("; ", consumed.Diagnostics));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(new("dotnet", [consumer], directory,
                TimeSpan.FromSeconds(5)), deadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded && !run.ProcessTreeCleanupIncomplete && !run.OutputTruncated,
                "The enum projection runtime must finish and reclaim its task-owned process tree: " + run.StandardError);
            AssertEx.Equal("42\n", run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
            Console.WriteLine("P1-09 enum projection process PID=" + run.StartedProcess.ProcessId + "; parent=" +
                run.StartedProcess.ParentProcessId + "; started=" + run.StartedProcess.StartedAt.ToString("O") + "; command=dotnet " + consumer +
                "; cleanupIncomplete=" + run.ProcessTreeCleanupIncomplete);
        }
        finally { DeleteDirectory(directory); }
    }

    private static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rustsharp-p1-source-type-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        string resolved = Path.GetFullPath(directory);
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        AssertEx.True(resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(resolved).StartsWith("rustsharp-p1-source-type-contract-", StringComparison.Ordinal),
            "Cleanup must target the exact task-owned temporary fixture directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        AssertEx.False(Directory.Exists(resolved), "The source contract fixture directory must be reclaimed.");
    }
}
