using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Runtime.Loader;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;

namespace RustSharp.Tests;

internal static class RustSharpMetadataTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("RustSharp metadata is canonical and bounded", CanonicalAsync),
        new("RustSharp metadata parses ownership and zero-argument evidence", ParseAsync),
        new("RustSharp metadata is embedded in generated PE", EmbeddedAsync),
        new("CompilerDriver embeds safe-core metadata", CompilerDriverAsync),
        new("RustSharp metadata consumer validates independent assemblies", ConsumerAsync),
        new("RustSharp metadata consumer executes a real external scalar call", CrossAssemblyCallAsync),
        new("RustSharp metadata consumer executes an external aggregate MemberRef", CrossAssemblyAggregateCallAsync),
        new("RustSharp metadata consumer executes an external by-reference MemberRef", CrossAssemblyReferenceCallAsync),
        new("RustSharp metadata preserves repeated positional parameter contracts", RepeatedParameterContractsAsync),
        new("RustSharp metadata consumer rejects an unlinked ownership contract", RejectsUnlinkedCallContractAsync),
        new("RustSharp metadata consumer rejects MethodDef signature drift", ConsumerMethodContractAsync),
    ];

    private static Task CanonicalAsync()
    {
        var first = new RustSharpMetadataDocument(
            "safe-core-mir-v1",
            new string('a', 64),
            [new("z", "i32"), new("a", "()")],
            [new("crate::f", "i32"), new("crate::f", "bool")],
            ["Display", "Copy", "Display"],
            ownership:
            [
                new("crate::f", "unwind", ["drop"], ["unwound"], ["borrow:b"]),
                new("crate::f", "unwind", ["drop"], ["returned"], ["borrow:a"]),
            ],
            cleanupSnapshot: "safe-core-mir-cleanup-p1-v1\n");
        var second = new RustSharpMetadataDocument(
            "safe-core-mir-v1",
            new string('a', 64),
            [new("a", "()"), new("z", "i32")],
            [new("crate::f", "bool"), new("crate::f", "i32")],
            ["Copy", "Display"],
            ownership:
            [
                new("crate::f", "unwind", ["drop"], ["returned"], ["borrow:a"]),
                new("crate::f", "unwind", ["drop"], ["unwound"], ["borrow:b"]),
            ],
            cleanupSnapshot: "safe-core-mir-cleanup-p1-v1\n");
        AssertEx.Equal(first.Json, second.Json, "Metadata ordering must be independent of insertion order.");
        AssertEx.True(first.Json.Contains("rustsharp-metadata-v1", StringComparison.Ordinal), "The schema must be explicit.");
        using (JsonDocument json = JsonDocument.Parse(first.Json))
        {
            AssertEx.False(json.RootElement.TryGetProperty("json", out _),
                "The internal serialized JSON cache must not be part of the schema.");
        }
        AssertEx.Throws<ArgumentException>(() => _ = new RustSharpMetadataDocument("profile", new string('b', 129)));

        int yielded = 0;
        IEnumerable<RustSharpMetadataFunction> UnboundedFunctions()
        {
            while (true)
            {
                yielded++;
                yield return new RustSharpMetadataFunction("f", "() -> ()");
            }
        }

        AssertEx.Throws<ArgumentException>(() => _ = new RustSharpMetadataDocument(
            "profile", new string('c', 64), functions: UnboundedFunctions()));
        AssertEx.True(yielded <= RustSharpMetadataDocument.MaximumFunctions + 1,
            "Metadata collection enumeration must stop at its configured bound.");
        return Task.CompletedTask;
    }

    private static Task EmbeddedAsync()
    {
        var method = new ClrLirMethod(
            "Main",
            ClrLirType.Void,
            [],
            [],
            [new ClrLirBlock("entry", [new ClrLirReturn()])]);
        RustSharpMetadataDocument document = RustSharpMetadataDocument.ForProgram(
            "safe-core-ownership-v1", [1, 2, 3], [method],
            [new RustSharpMetadataGenericInstance("crate::id", "i32")], ["Copy"]);
        GeneratedAssembly generated = ClrLirAssemblyEmitter.Emit(method, "MetadataProbe", document);
        using var reader = new PEReader(new MemoryStream(generated.PeImage, writable: false));
        MetadataReader metadata = reader.GetMetadataReader();
        string json = AssertEx.NotNull(
            RustSharpMetadataReader.FindJson(metadata),
            "The generated PE must carry RustSharp metadata.");
        AssertEx.Equal(document.Json, json, "The embedded metadata must round-trip byte-for-byte.");
        string generatedJson = AssertEx.NotNull(
            generated.RustSharpMetadataJson,
            "The emitter result must expose the embedded RustSharp metadata.");
        AssertEx.Equal(document.Json, generatedJson);
        CustomAttribute attribute = metadata.GetCustomAttribute(metadata.CustomAttributes.Single());
        CustomAttributeValue<string> decoded = attribute.DecodeValue(new AttributeTypes());
        AssertEx.Equal(2, decoded.FixedArguments.Length);
        AssertEx.Equal(RustSharpMetadataReader.AttributeKey, (string)decoded.FixedArguments[0].Value!);
        AssertEx.Equal(document.Json, (string)decoded.FixedArguments[1].Value!);
        AssertEx.Equal(0, decoded.NamedArguments.Length,
            "Standard CLR attribute decoding must consume the required zero named-argument count.");
        return Task.CompletedTask;
    }

    private sealed class AttributeTypes : ICustomAttributeTypeProvider<string>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => "System.Type";
        public bool IsSystemType(string type) => type == "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            reader.GetString(reader.GetTypeDefinition(handle).Name);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            reader.GetString(reader.GetTypeReference(handle).Name);
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
    }

    private static Task ParseAsync()
    {
        var original = new RustSharpMetadataDocument(
            "safe-core-generics-v1",
            new string('d', 64),
            [new("Main", "()->()")],
            [new("crate::main", "()"), new("crate::id", "i32")],
            ["Copy"],
            "{\"mir\":1}",
            [new("crate::main", "unwind", ["drop:1"], ["returned"], ["shared:1"]) ],
            "safe-core-mir-cleanup-p1-v1\n",
            [new("crate::main", "unwind", ["borrow:shared"], "unit")]);
        RustSharpMetadataDocument parsed = RustSharpMetadataDocument.Parse(original.Json);
        AssertEx.Equal(original.Json, parsed.Json, "Metadata parse must re-canonicalize to the producer bytes.");
        AssertEx.Equal("()", parsed.GenericInstances.Single(instance => instance.FunctionId == "crate::main").Arguments);
        AssertEx.Equal(1, parsed.Ownership.Length);
        AssertEx.Equal("safe-core-mir-cleanup-p1-v1\n", parsed.CleanupSnapshot!);
        AssertEx.Equal(1, parsed.CallContracts.Length);
        AssertEx.Equal("borrow:shared", parsed.CallContracts[0].ParameterContracts!.Single());
        AssertEx.Equal("unit", parsed.CallContracts[0].ReturnContract!);
        IEnumerable<string> dropOrder = AssertEx.NotNull(parsed.Ownership[0].DropOrder, "Drop-order evidence must be materialized.");
        AssertEx.Equal("drop:1", dropOrder.Single());
        return Task.CompletedTask;
    }

    private static Task CompilerDriverAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rustsharp-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "metadata.rs");
        string outputPath = Path.Combine(directory, "metadata.dll");
        const string source = "fn helper(x: i32) -> i32 { x + 1 } fn main() { println!(\"{}\", helper(4)); }";
        try
        {
            File.WriteAllText(sourcePath, source);
            CompilationResult result = CompilerDriver.CompileFile(
                sourcePath,
                outputPath,
                profile: CompilationProfile.SafeCorePrimitives);
            AssertEx.True(result.Success, string.Join("; ", result.Diagnostics));
            using (FileStream stream = File.OpenRead(outputPath))
            using (var pe = new PEReader(stream))
            {
                string json = AssertEx.NotNull(
                    RustSharpMetadataReader.FindJson(pe.GetMetadataReader()),
                    "CompilerDriver output must carry RustSharp metadata.");
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                AssertEx.Equal("rustsharp-metadata-v1", AssertEx.NotNull(root.GetProperty("schema").GetString(), "Metadata schema must be a string."));
                AssertEx.Equal("safe-core-primitives-v1", AssertEx.NotNull(root.GetProperty("profile").GetString(), "Metadata profile must be a string."));
                string expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
                AssertEx.Equal(expectedHash, AssertEx.NotNull(root.GetProperty("sourceSha256").GetString(), "Metadata source hash must be a string."));
                AssertEx.Equal(2, root.GetProperty("functions").GetArrayLength());
            }
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return Task.CompletedTask;
    }

    private static async Task ConsumerAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rustsharp-metadata-consumer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "producer.rs");
        string outputPath = Path.Combine(directory, "producer.dll");
        string consumerSourcePath = Path.Combine(directory, "consumer.rs");
        string consumerOutputPath = Path.Combine(directory, "consumer.dll");
        const string source = "pub fn identity<T>(value: T) -> T { value } fn hidden() -> i32 { 7 } fn main() { println!(\"{}\", identity::<i32>(42) + hidden()); }";
        const string consumerSource = "fn main() { println!(\"consumer\"); }";
        try
        {
            File.WriteAllText(sourcePath, source);
            CompilationResult result = CompilerDriver.CompileFile(
                sourcePath,
                outputPath,
                profile: CompilationProfile.SafeCoreGenerics);
            AssertEx.True(result.Success, string.Join("; ", result.Diagnostics));

            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(
                outputPath,
                "safe-core-generics-v1",
                ["Main"]);
            AssertEx.True(imported.IsSuccessful, string.Join("; ", imported.Diagnostics));
            AssertEx.NotNull(imported.Document, "The consumer must return the validated document.");
            AssertEx.True(!string.IsNullOrWhiteSpace(imported.GenericMetadataJson),
                "The consumer must read the embedded generic resource.");
            AssertEx.True(imported.Document!.GenericInstances.Any(instance => instance.Arguments == "()"),
                "The root zero-argument instance must use the canonical () spelling.");
            string requiredExport = imported.Document.Functions
                .FirstOrDefault(function => function.IsPublic &&
                    !string.Equals(function.Name, "Main", StringComparison.Ordinal))?.Name
                ?? "Main";
            string privateExport = imported.Document.Functions
                .FirstOrDefault(static function => !function.IsPublic)?.Name
                ?? throw new InvalidOperationException("The producer must retain a private function export for rejection testing.");

            File.WriteAllText(consumerSourcePath, consumerSource);
            CompilationResult consumerCompilation = CompilerDriver.CompileWithMetadataReferences(
                consumerSource,
                consumerSourcePath,
                consumerOutputPath,
                "MetadataConsumer",
                CompilationProfile.SafeCoreGenerics,
                [outputPath],
                [requiredExport]);
            AssertEx.True(
                consumerCompilation.Success,
                "An independent consumer must compile after validating the producer metadata: " +
                string.Join("; ", consumerCompilation.Diagnostics));
            AssertEx.True(
                consumerCompilation.Output is not null && File.Exists(consumerOutputPath),
                "The independent consumer compilation must publish its assembly.");
            RustSharpMetadataImportResult consumerImported = RustSharpMetadataConsumer.ReadAssembly(
                consumerOutputPath,
                "safe-core-generics-v1",
                ["Main"]);
            AssertEx.True(
                consumerImported.IsSuccessful,
                "The compiled consumer must carry readable Rust# metadata: " +
                string.Join("; ", consumerImported.Diagnostics));
            AssertEx.NotNull(consumerImported.Document,
                "The compiled consumer must expose a metadata document.");

            CompilationResult privateReference = CompilerDriver.CompileWithMetadataReferences(
                consumerSource,
                consumerSourcePath,
                Path.Combine(directory, "private-consumer.dll"),
                "PrivateMetadataConsumer",
                CompilationProfile.SafeCoreGenerics,
                [outputPath],
                [privateExport]);
            AssertEx.False(
                privateReference.Success,
                "A consumer must not import a private producer function.");
            AssertEx.True(
                privateReference.Diagnostics.Any(diagnostic =>
                    string.Equals(diagnostic.Code, RustSharpMetadataConsumer.ExportMissing, StringComparison.Ordinal)),
                "Private metadata imports must produce the bounded export-missing diagnostic.");
            using var runDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            BoundedProcessResult consumerRun = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [consumerOutputPath], directory, TimeSpan.FromSeconds(5)),
                runDeadline.Token).ConfigureAwait(false);
            AssertEx.True(
                consumerRun.Succeeded,
                "The independently compiled consumer must execute: " + consumerRun.StandardError);
            AssertEx.Equal(
                "consumer\n",
                consumerRun.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));

            RustSharpMetadataImportResult mismatch = RustSharpMetadataConsumer.ReadAssembly(
                outputPath, "safe-core-primitives-v1");
            AssertEx.True(mismatch.Diagnostics.Any(diagnostic => diagnostic.StartsWith(
                RustSharpMetadataConsumer.ProfileMismatch, StringComparison.Ordinal)),
                "A profile mismatch must be diagnosed.");

            RustSharpMetadataImportResult missing = RustSharpMetadataConsumer.ReadAssembly(
                outputPath, "safe-core-generics-v1", ["NotExported"]);
            AssertEx.True(missing.Diagnostics.Any(diagnostic => diagnostic.StartsWith(
                RustSharpMetadataConsumer.ExportMissing, StringComparison.Ordinal)),
                "A missing required export must be diagnosed.");
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task CrossAssemblyCallAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rustsharp-metadata-call-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string producerSourcePath = Path.Combine(directory, "producer.rs");
        string producerOutputPath = Path.Combine(directory, "Producer.dll");
        string consumerSourcePath = Path.Combine(directory, "consumer.rs");
        string consumerOutputPath = Path.Combine(directory, "Consumer.dll");
        const string producerSource =
            "pub fn add(value: i32) -> i32 { value + 1 } pub fn negate(value: bool) -> bool { !value } fn main() { println!(\"{}\", add(4)); }";
        const string consumerSource =
            "use Producer::add; use Producer::negate; fn main() { println!(\"{}\", add(4)); println!(\"{}\", negate(true)); }";
        try
        {
            File.WriteAllText(producerSourcePath, producerSource);
            CompilationResult producer = CompilerDriver.CompileFile(
                producerSourcePath,
                producerOutputPath,
                assemblyName: "Producer",
                profile: CompilationProfile.SafeCorePrimitives);
            AssertEx.True(producer.Success, string.Join("; ", producer.Diagnostics));
            RustSharpMetadataImportResult producerMetadata = RustSharpMetadataConsumer.ReadAssembly(
                producerOutputPath, "safe-core-primitives-v1", ["crate::add", "crate::negate"]);
            AssertEx.True(producerMetadata.IsSuccessful, string.Join("; ", producerMetadata.Diagnostics));
            AssertEx.Equal("Producer", AssertEx.NotNull(producerMetadata.AssemblyName,
                "Metadata consumers must retain the CLR AssemblyDef identity."));

            File.WriteAllText(consumerSourcePath, consumerSource);
            CompilationResult consumer = CompilerDriver.CompileWithMetadataReferences(
                consumerSource,
                consumerSourcePath,
                consumerOutputPath,
                "Consumer",
                CompilationProfile.SafeCorePrimitives,
                [producerOutputPath],
                ["crate::add", "crate::negate"]);
            AssertEx.True(consumer.Success,
                "The consumer must resolve and emit an external call: " +
                string.Join("; ", consumer.Diagnostics));
            AssertEx.True(consumer.Output is not null && File.Exists(consumerOutputPath),
                "The consumer assembly must be emitted.");

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [consumerOutputPath], directory, TimeSpan.FromSeconds(5)),
                deadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded, "The external call process failed: " + run.StandardError);
            AssertEx.Equal("5\nfalse\n", run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task CrossAssemblyAggregateCallAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "rustsharp-metadata-aggregate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string producerPath = Path.Combine(directory, "Producer.dll");
        string consumerPath = Path.Combine(directory, "Consumer.dll");
        string sourcePath = Path.Combine(directory, "aggregate.rs");
        var pair = new ClrLirValueType("Pair", [
            new ClrLirField("left", ClrLirType.I32),
            new ClrLirField("right", ClrLirType.Bool),
        ]);
        var makePair = new ClrLirMethod(
            "MakePair",
            pair.Type,
            [],
            [],
            [new ClrLirBlock("entry", [
                new ClrLirLoadInt32(7),
                new ClrLirLoadBoolean(true),
                new ClrLirConstructValue(pair),
                new ClrLirReturn(),
            ])]);
        var producerMain = new ClrLirMethod(
            "Main",
            ClrLirType.Void,
            [],
            [],
            [new ClrLirBlock("entry", [
                new ClrLirCall(new ClrLirCallSite("MakePair", pair.Type, [])),
                new ClrLirDiscard(pair.Type),
                new ClrLirReturn(),
            ])]);
        var consumerMain = new ClrLirMethod(
            "Main",
            ClrLirType.Void,
            [],
            [],
            [new ClrLirBlock("entry", [
                new ClrLirCall(new ClrLirCallSite("MakePair", pair.Type, [])
                {
                    ExternalCall = new ClrLirExternalCall(
                        "Producer", "RustSharp.Generated", "Program", "MakePair"),
                }),
                new ClrLirDiscard(pair.Type),
                new ClrLirReturn(),
            ])]);
        try
        {
            const string source = "pub fn make_pair() -> (i32, bool) { (7, true) } fn main() {}";
            File.WriteAllText(sourcePath, source);
            SafeCoreClrResult producerProgram = new(
                [producerMain, makePair],
                [new(0, source.Length), new(0, source.Length)],
                []) { ValueTypes = [pair] };
            RustSharpMetadataDocument producerMetadata = RustSharpMetadataDocument.ForProgram(
                "safe-core-mir-v1", Encoding.UTF8.GetBytes(source), producerProgram.Methods);
            GeneratedAssembly producer = ClrLirAssemblyEmitter.EmitProgram(
                producerProgram, "Producer", source, sourcePath, "Producer.pdb",
                Encoding.UTF8.GetBytes(source), null, producerMetadata);
            File.WriteAllBytes(producerPath, producer.PeImage);
            File.WriteAllText(Path.ChangeExtension(producerPath, ".runtimeconfig.json"), producer.RuntimeConfigJson);

            const string consumerSource = "fn main() {}";
            SafeCoreClrResult consumerProgram = new(
                [consumerMain], [new(0, consumerSource.Length)], []) { ValueTypes = [pair] };
            RustSharpMetadataDocument consumerMetadata = RustSharpMetadataDocument.ForProgram(
                "safe-core-mir-v1", Encoding.UTF8.GetBytes(consumerSource), consumerProgram.Methods);
            GeneratedAssembly consumer = ClrLirAssemblyEmitter.EmitProgram(
                consumerProgram, "Consumer", consumerSource,
                Path.Combine(directory, "consumer.rs"), "Consumer.pdb",
                Encoding.UTF8.GetBytes(consumerSource), null, consumerMetadata);
            File.WriteAllBytes(consumerPath, consumer.PeImage);
            File.WriteAllText(Path.ChangeExtension(consumerPath, ".runtimeconfig.json"), consumer.RuntimeConfigJson);

            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(
                producerPath, "safe-core-mir-v1", ["MakePair"]);
            AssertEx.True(imported.IsSuccessful,
                "The aggregate producer MethodDef must be importable: " +
                string.Join("; ", imported.Diagnostics));
            AssertEx.True(imported.Document!.Functions.Any(function =>
                function.Name == "MakePair" && function.Signature == "->Value(Pair)"),
                "The producer metadata must retain the aggregate CLR signature.");

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [consumerPath], directory, TimeSpan.FromSeconds(5)),
                deadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded,
                "The aggregate MemberRef must execute under CoreCLR: " + run.StandardError);
            AssertEx.Equal(string.Empty, run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The test host intentionally loads the generated producer and consumer assemblies dynamically; Native AOT validation runs through the platform harness.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The test host intentionally reflects the generated public forwarding method.")]
    private static Task CrossAssemblyReferenceCallAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "rustsharp-metadata-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string producerPath = Path.Combine(directory, "RefProducer.dll");
        string consumerPath = Path.Combine(directory, "RefConsumer.dll");
        string sourcePath = Path.Combine(directory, "reference.rs");
        ClrLirType reference = ClrLirType.ByReference(ClrLirType.I32);
        var producerIdentity = new ClrLirMethod(
            "RefIdentity", reference, [reference], [],
            [new ClrLirBlock("entry", [
                new ClrLirLoadArgument(0),
                new ClrLirReturn(),
            ])]);
        var producerMain = new ClrLirMethod(
            "Main", ClrLirType.Void, [], [],
            [new ClrLirBlock("entry", [new ClrLirReturn()])]);
        var consumerForward = new ClrLirMethod(
            "Forward", reference, [reference], [],
            [new ClrLirBlock("entry", [
                new ClrLirLoadArgument(0),
                new ClrLirCall(new ClrLirCallSite("RefIdentity", reference, [reference])
                {
                    ExternalCall = new ClrLirExternalCall(
                        "RefProducer", "RustSharp.Generated", "Program", "RefIdentity"),
                }),
                new ClrLirReturn(),
            ])]);
        var consumerMain = new ClrLirMethod(
            "Main", ClrLirType.Void, [], [],
            [new ClrLirBlock("entry", [new ClrLirReturn()])]);
        try
        {
            const string source = "pub fn ref_identity(value: &i32) -> &i32 { value } fn main() {}";
            File.WriteAllText(sourcePath, source);
            SafeCoreClrResult producerProgram = new(
                [producerMain, producerIdentity],
                [new(0, source.Length), new(0, source.Length)],
                []);
            RustSharpMetadataDocument producerMetadata = RustSharpMetadataDocument.ForProgram(
                "safe-core-mir-v1", Encoding.UTF8.GetBytes(source), producerProgram.Methods,
                callContracts: [new RustSharpMetadataCallContract(
                    "RefIdentity", "unwind", ["borrow:shared"], "borrow:shared")]);
            GeneratedAssembly producer = ClrLirAssemblyEmitter.EmitProgram(
                producerProgram, "RefProducer", source, sourcePath, "RefProducer.pdb",
                Encoding.UTF8.GetBytes(source), null, producerMetadata);
            File.WriteAllBytes(producerPath, producer.PeImage);
            File.WriteAllText(Path.ChangeExtension(producerPath, ".runtimeconfig.json"), producer.RuntimeConfigJson);

            const string consumerSource = "fn main() {}";
            SafeCoreClrResult consumerProgram = new(
                [consumerMain, consumerForward],
                [new(0, consumerSource.Length), new(0, consumerSource.Length)],
                []);
            RustSharpMetadataDocument consumerMetadata = RustSharpMetadataDocument.ForProgram(
                "safe-core-mir-v1", Encoding.UTF8.GetBytes(consumerSource), consumerProgram.Methods);
            GeneratedAssembly consumer = ClrLirAssemblyEmitter.EmitProgram(
                consumerProgram, "RefConsumer", consumerSource,
                Path.Combine(directory, "consumer.rs"), "RefConsumer.pdb",
                Encoding.UTF8.GetBytes(consumerSource), null, consumerMetadata);
            File.WriteAllBytes(consumerPath, consumer.PeImage);
            File.WriteAllText(Path.ChangeExtension(consumerPath, ".runtimeconfig.json"), consumer.RuntimeConfigJson);

            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(
                producerPath, "safe-core-mir-v1", ["RefIdentity"]);
            AssertEx.True(imported.IsSuccessful,
                "The by-reference producer MethodDef must be importable: " +
                string.Join("; ", imported.Diagnostics));
            AssertEx.True(imported.Document!.Functions.Any(function =>
                function.Name == "RefIdentity" && function.Signature == "&I32->&I32"),
                "The producer metadata must retain the managed by-reference signature.");
            AssertEx.Equal(1, imported.Document.CallContracts.Length);

            // Load both images in the default CoreCLR context so the consumer's
            // AssemblyRef can resolve to the producer, then invoke the public
            // forwarding method with a ref argument. This exercises the emitted
            // MemberRef and its stack signature, rather than only parsing PE.
            using var producerImage = new MemoryStream(producer.PeImage, writable: false);
            using var consumerImage = new MemoryStream(consumer.PeImage, writable: false);
            _ = AssemblyLoadContext.Default.LoadFromStream(producerImage);
            Assembly consumerAssembly = AssemblyLoadContext.Default.LoadFromStream(consumerImage);
            Type programType = AssertEx.NotNull(consumerAssembly.GetType(
                "RustSharp.Generated.Program"), "The generated consumer type must load.");
            MethodInfo forward = AssertEx.NotNull(programType.GetMethod(
                "Forward", BindingFlags.Public | BindingFlags.Static),
                "The generated forwarding method must be public.");
            object?[] arguments = [41];
            object? result = forward.Invoke(null, arguments);
            AssertEx.Equal(41, AssertEx.NotNull(result,
                "A by-reference return must survive the producer/consumer call."));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static Task RepeatedParameterContractsAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "rustsharp-metadata-positional-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string assemblyPath = Path.Combine(directory, "PositionalContract.dll");
        try
        {
            ClrLirType reference = ClrLirType.ByReference(ClrLirType.I32);
            var main = new ClrLirMethod("Main", ClrLirType.Void, [], [],
                [new ClrLirBlock("entry", [new ClrLirReturn()])]);
            var consume = new ClrLirMethod("Consume", ClrLirType.Void,
                [reference, ClrLirType.ByReference(ClrLirType.I32, mutable: true), reference], [],
                [new ClrLirBlock("entry", [new ClrLirReturn()])]);
            string[] expected = ["borrow:shared", "borrow:mutable", "borrow:shared"];
            const string source = "fn main() {}";
            RustSharpMetadataDocument document = RustSharpMetadataDocument.ForProgram(
                "safe-core-mir-v1", Encoding.UTF8.GetBytes(source), [main, consume],
                callContracts: [new RustSharpMetadataCallContract(
                    "Consume", "unwind", expected, "unit")]);
            AssertEx.True(expected.SequenceEqual(document.CallContracts.Single().ParameterContracts!),
                "Equal parameter contracts must preserve their count and declaration order.");
            RustSharpMetadataDocument parsed = RustSharpMetadataDocument.Parse(document.Json);
            AssertEx.Equal(document.Json, parsed.Json,
                "Repeated parameter contracts must round-trip to identical JSON.");
            AssertEx.True(expected.SequenceEqual(parsed.CallContracts.Single().ParameterContracts!),
                "JSON parsing must retain every positional parameter contract.");

            SafeCoreClrResult program = new([main, consume],
                [new(0, source.Length), new(0, source.Length)], []);
            GeneratedAssembly generated = ClrLirAssemblyEmitter.EmitProgram(program,
                "PositionalContract", source, Path.Combine(directory, "contracts.rs"),
                "PositionalContract.pdb", Encoding.UTF8.GetBytes(source), null, document);
            File.WriteAllBytes(assemblyPath, generated.PeImage);
            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(
                assemblyPath, "safe-core-mir-v1", ["Consume"]);
            AssertEx.True(imported.IsSuccessful,
                "The PE consumer must accept repeated positional contracts: " +
                string.Join("; ", imported.Diagnostics));
            AssertEx.True(expected.SequenceEqual(
                imported.Document!.CallContracts.Single().ParameterContracts!),
                "The PE consumer must retain all three parameter positions.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static Task RejectsUnlinkedCallContractAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "rustsharp-metadata-call-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string assemblyPath = Path.Combine(directory, "Contract.dll");
        try
        {
            var method = new ClrLirMethod(
                "Main", ClrLirType.Void, [], [],
                [new ClrLirBlock("entry", [new ClrLirReturn()])]);
            RustSharpMetadataDocument forged = RustSharpMetadataDocument.ForProgram(
                "safe-core-mir-v1", [1, 2, 3], [method],
                callContracts: [new RustSharpMetadataCallContract(
                    "crate::missing", "unwind", ["borrow:shared"], "unit")]);
            GeneratedAssembly generated = ClrLirAssemblyEmitter.Emit(method, "Contract", forged);
            File.WriteAllBytes(assemblyPath, generated.PeImage);
            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(
                assemblyPath, "safe-core-mir-v1");
            AssertEx.False(imported.IsSuccessful,
                "An ownership contract must resolve to a declared producer function.");
            AssertEx.True(imported.Diagnostics.Any(diagnostic =>
                diagnostic.StartsWith(RustSharpMetadataConsumer.InvalidMetadata, StringComparison.Ordinal) &&
                diagnostic.Contains("call contract", StringComparison.OrdinalIgnoreCase)),
                "Unlinked ownership contracts must produce a stable metadata diagnostic.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static Task ConsumerMethodContractAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "rustsharp-metadata-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string assemblyPath = Path.Combine(directory, "ForgedMetadata.dll");
        try
        {
            var method = new ClrLirMethod(
                "Main",
                ClrLirType.Void,
                [],
                [],
                [new ClrLirBlock("entry", [new ClrLirReturn()])]);
            // The PE contains a public static Main()->Void, while the
            // embedded document deliberately claims a different signature and
            // visibility. A consumer must trust the MethodDef table, not this
            // forged JSON attribute.
            var forged = new RustSharpMetadataDocument(
                "safe-core-primitives-v1",
                new string('e', 64),
                [new RustSharpMetadataFunction("Main", "I32->I32", "crate::main", false)]);
            GeneratedAssembly generated = ClrLirAssemblyEmitter.Emit(
                method, "ForgedMetadata", forged);
            File.WriteAllBytes(assemblyPath, generated.PeImage);

            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(
                assemblyPath, "safe-core-primitives-v1", ["Main"]);
            AssertEx.False(imported.IsSuccessful,
                "Forged metadata must not pass MethodDef correlation.");
            AssertEx.True(imported.Diagnostics.Any(diagnostic =>
                    diagnostic.StartsWith(RustSharpMetadataConsumer.InvalidMetadata, StringComparison.Ordinal) &&
                    diagnostic.Contains("signature", StringComparison.OrdinalIgnoreCase)),
                "Signature drift must produce a bounded invalid-metadata diagnostic.");
            AssertEx.True(imported.Diagnostics.Any(diagnostic =>
                    diagnostic.StartsWith(RustSharpMetadataConsumer.InvalidMetadata, StringComparison.Ordinal) &&
                    diagnostic.Contains("visibility", StringComparison.OrdinalIgnoreCase)),
                "Visibility drift must produce a bounded invalid-metadata diagnostic.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return Task.CompletedTask;
    }

}
