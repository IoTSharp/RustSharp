using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        return Task.CompletedTask;
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
            "safe-core-mir-cleanup-p1-v1\n");
        RustSharpMetadataDocument parsed = RustSharpMetadataDocument.Parse(original.Json);
        AssertEx.Equal(original.Json, parsed.Json, "Metadata parse must re-canonicalize to the producer bytes.");
        AssertEx.Equal("()", parsed.GenericInstances.Single(instance => instance.FunctionId == "crate::main").Arguments);
        AssertEx.Equal(1, parsed.Ownership.Length);
        AssertEx.Equal("safe-core-mir-cleanup-p1-v1\n", parsed.CleanupSnapshot!);
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
