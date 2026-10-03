using System.Text;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;

namespace RustSharp.Tests;

/// <summary>
/// Source-package producer/consumer contract probes kept separate from the
/// metadata unit tests. These cases exercise the P1-09 boundary where a
/// consumer must reconcile producer evidence before it can emit or execute.
/// </summary>
internal static class P1SourcePackageContractTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1-09 rejects stale producer aggregate layout metadata", RejectsStaleAggregateMetadataAsync),
        new("P1-09 rejects unsupported source aggregate imports before emission", RejectsUnsupportedSourceAggregateImportAsync),
        new("P1-09 emits identical consumer artifacts for reordered metadata references", ReorderedMetadataReferencesAreDeterministicAsync),
        new("P1-09 executes deterministic scalar source packages with positional Copy contracts", ScalarSourcePackagesAsync),
        new("P1-09 keeps generic source specialization call contracts distinct", GenericSourceContractsAsync),
        new("P1-09 rejects missing scalar source parameter contracts", () => RejectsScalarContractAsync("missing-parameter")),
        new("P1-09 rejects scalar source contract type substitution", () => RejectsScalarContractAsync("type-substitution")),
        new("P1-09 rejects unknown source call contract schema", () => RejectsScalarContractAsync("schema")),
        new("P1-09 rejects duplicate source and CLR call contract aliases", () => RejectsScalarContractAsync("alias")),
        new("P1-09 rejects source call panic and ownership disagreement", () => RejectsScalarContractAsync("panic")),
        new("P1-09 rejects missing scalar source return contracts", () => RejectsScalarContractAsync("missing-return")),
    ];

    private static async Task GenericSourceContractsAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rustsharp-p1-source-contract-generics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string source = "pub fn identity<T>(value: T) -> T { value } " +
            "fn unit(value: ()) -> () { identity(value) } " +
            "fn main() { println!(\"{}\", identity::<i32>(42)); println!(\"{}\", identity::<bool>(true)); " +
            "unit(identity::<()>(())); println!(\"{}\", identity(identity(9))); }";
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string sourcePath = Path.Combine(directory, "producer.rs");
            string assemblyPath = Path.Combine(directory, "GenericContractProducer.dll");
            File.WriteAllText(sourcePath, source);
            CompilationResult compilation = CompilerDriver.CompileFile(sourcePath, assemblyPath, "GenericContractProducer",
                CompilationProfile.SafeCoreGenerics, deadline.Token);
            AssertEx.True(compilation.Success, "Concrete source generic instances must compile: " + string.Join("; ", compilation.Diagnostics));
            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(assemblyPath, "safe-core-generics-v1");
            AssertEx.True(imported.IsSuccessful, "Generic source metadata must reconcile: " + string.Join("; ", imported.Diagnostics));
            RustSharpMetadataDocument document = imported.Document!;
            RustSharpMetadataFunction[] scalarInstances = document.Functions.Where(value =>
                value.SourceQualifiedName == "crate::identity" && value.Signature is "I32->I32" or "Bool->Bool").ToArray();
            AssertEx.Equal(2, scalarInstances.Length, "The shared source declaration must retain two distinct scalar MethodDefs.");
            foreach (RustSharpMetadataFunction instance in scalarInstances)
            {
                deadline.Token.ThrowIfCancellationRequested();
                RustSharpMetadataCallContract contract = document.CallContracts.Single(value => value.FunctionId == instance.Name);
                AssertEx.Equal(RustSharpMetadataCallContract.ScalarSchema, contract.Schema!);
                AssertEx.True(contract.ParameterContracts!.SequenceEqual(["copy"]), "Each closed scalar parameter must retain its Copy term.");
                AssertEx.Equal(instance.Name, RustSharpMetadataConsumer.FindFunction(document, contract.FunctionId)!.Name);
            }
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(new("dotnet", [assemblyPath], directory,
                TimeSpan.FromSeconds(5)), deadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded && !run.ProcessTreeCleanupIncomplete && !run.OutputTruncated,
                "Generic source specialization execution must exit cleanly: " + run.StandardError);
            AssertEx.Equal("42\ntrue\n9\n", run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally { TryDelete(directory); }
    }

    private static async Task ScalarSourcePackagesAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rustsharp-p1-source-contract-execute-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string producerSource = "pub fn add(left: i32, middle: i32, right: i32) -> i32 { left + middle + right } fn main() {}";
        const string consumerSource = "use ContractProducer::add; fn main() { println!(\"{}\", add(20, 21, 1)); }";
        string producerPath = Path.Combine(directory, "producer.rs");
        string consumerPath = Path.Combine(directory, "consumer.rs");
        try
        {
            File.WriteAllText(producerPath, producerSource);
            File.WriteAllText(consumerPath, consumerSource);
            string[] producers = [Path.Combine(directory, "first", "ContractProducer.dll"), Path.Combine(directory, "second", "ContractProducer.dll")];
            string[] consumers = [Path.Combine(directory, "first", "ContractConsumer.dll"), Path.Combine(directory, "second", "ContractConsumer.dll")];
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            for (int index = 0; index < 2; index++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                CompilationResult producer = CompilerDriver.CompileFile(producerPath, producers[index], "ContractProducer",
                    CompilationProfile.SafeCorePrimitives, deadline.Token);
                AssertEx.True(producer.Success, "The real source producer must compile: " + string.Join("; ", producer.Diagnostics));
                RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(producers[index], "safe-core-primitives-v1", ["crate::add"]);
                AssertEx.True(imported.IsSuccessful, string.Join("; ", imported.Diagnostics));
                RustSharpMetadataDocument metadata = imported.Document!;
                AssertEx.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(producerSource))), metadata.SourceSha256);
                RustSharpMetadataCallContract contract = metadata.CallContracts.Single(value => value.FunctionId == "crate::add");
                AssertEx.Equal(RustSharpMetadataCallContract.ScalarSchema, contract.Schema!);
                AssertEx.True(contract.ParameterContracts!.SequenceEqual(["copy", "copy", "copy"]),
                    "Repeated Copy arguments must preserve all three source positions.");
                AssertEx.Equal("copy", contract.ReturnContract!);
                AssertEx.Equal("unwind", contract.PanicStrategy);
                AssertEx.Equal(metadata.Json, RustSharpMetadataDocument.Parse(metadata.Json).Json);
                CompilationResult consumer = CompilerDriver.CompileWithMetadataReferences(consumerSource, consumerPath,
                    consumers[index], "ContractConsumer", CompilationProfile.SafeCorePrimitives, [producers[index]],
                    ["crate::add"], deadline.Token);
                AssertEx.True(consumer.Success, "The real source consumer must compile: " + string.Join("; ", consumer.Diagnostics));
                AssertExternalMemberRef(consumers[index], "ContractProducer", metadata.Functions.Single(value => value.SourceQualifiedName == "crate::add").Name,
                    deadline.Token);
            }

            foreach (string extension in new[] { ".dll", ".pdb" })
            {
                AssertEx.True(File.ReadAllBytes(Path.ChangeExtension(producers[0], extension)).AsSpan()
                    .SequenceEqual(File.ReadAllBytes(Path.ChangeExtension(producers[1], extension))), "Independent producer " + extension + " bytes must match.");
                AssertEx.True(File.ReadAllBytes(Path.ChangeExtension(consumers[0], extension)).AsSpan()
                    .SequenceEqual(File.ReadAllBytes(Path.ChangeExtension(consumers[1], extension))), "Independent consumer " + extension + " bytes must match.");
            }
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(new("dotnet", [consumers[0]],
                Path.GetDirectoryName(consumers[0])!, TimeSpan.FromSeconds(5)), deadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded && !run.ProcessTreeCleanupIncomplete && !run.OutputTruncated,
                "The source package process must exit cleanly: " + run.StandardError);
            AssertEx.Equal("42\n", run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
            Console.WriteLine("Source package process: " + run.StartedProcess.CommandLine + "; PID=" + run.StartedProcess.ProcessId +
                "; parent=" + run.StartedProcess.ParentProcessId + "; started=" + run.StartedProcess.StartedAt.ToString("O") +
                "; exit=" + run.ExitCode + "; cleanupIncomplete=" + run.ProcessTreeCleanupIncomplete);
        }
        finally { TryDelete(directory); }
    }

    private static Task RejectsScalarContractAsync(string mutation)
    {
        string directory = Path.Combine(Path.GetTempPath(), "rustsharp-p1-source-contract-negative-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string source = "pub fn add(left: i32, right: i32) -> i32 { left + right } fn main() {}";
        string producerPath = Path.Combine(directory, "ContractProducer.dll");
        try
        {
            string sourcePath = Path.Combine(directory, "producer.rs");
            File.WriteAllText(sourcePath, source);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            CompilationResult producer = CompilerDriver.CompileFile(sourcePath, producerPath, "ContractProducer",
                CompilationProfile.SafeCorePrimitives, deadline.Token);
            AssertEx.True(producer.Success, string.Join("; ", producer.Diagnostics));
            RustSharpMetadataImportResult original = RustSharpMetadataConsumer.ReadAssembly(producerPath, "safe-core-primitives-v1");
            AssertEx.True(original.IsSuccessful, string.Join("; ", original.Diagnostics));
            RustSharpMetadataDocument document = original.Document!;
            RustSharpMetadataCallContract target = document.CallContracts.Single(value => value.FunctionId == "crate::add");
            RustSharpMetadataCallContract changed = mutation switch
            {
                "missing-parameter" => target with { ParameterContracts = [] },
                "type-substitution" => target with { ParameterContracts = ["borrow:shared", "copy"] },
                "schema" => target with { Schema = "rustsharp-scalar-call-v9" },
                "panic" => target with { PanicStrategy = "abort" },
                "missing-return" => target with { ReturnContract = null },
                "alias" => target,
                _ => throw new InvalidOperationException("Unknown bounded source contract mutation."),
            };
            var contracts = document.CallContracts.Select(value => value.FunctionId == target.FunctionId ? changed : value).ToList();
            if (mutation == "alias")
                contracts.Add(target with { FunctionId = document.Functions.Single(value => value.SourceQualifiedName == "crate::add").Name });
            // Keep MethodDef and ownership evidence from the real source producer.
            // Removing optional snapshots reserves enough space for a changed
            // JSON attribute without rebuilding hand-written LIR as source proof.
            var forged = new RustSharpMetadataDocument(document.Profile, document.SourceSha256, document.Functions,
                document.GenericInstances, document.TraitImplementations, ownership: document.Ownership,
                callContracts: contracts, valueTypes: document.ValueTypes);
            byte[] image = File.ReadAllBytes(producerPath);
            byte[] oldBytes = Encoding.UTF8.GetBytes(document.Json);
            byte[] newBytes = Encoding.UTF8.GetBytes(forged.Json);
            int offset = image.AsSpan().IndexOf(oldBytes);
            AssertEx.True(offset >= 0 && newBytes.Length <= oldBytes.Length, "The source PE must have space for the bounded metadata mutation.");
            image.AsSpan(offset, oldBytes.Length).Fill((byte)' ');
            newBytes.CopyTo(image.AsSpan(offset));
            File.WriteAllBytes(producerPath, image);
            RustSharpMetadataImportResult rejected = RustSharpMetadataConsumer.ReadAssembly(producerPath, "safe-core-primitives-v1");
            AssertEx.False(rejected.IsSuccessful, "The source producer mutation must be rejected: " + mutation);
            AssertEx.True(rejected.Diagnostics.Any(value => value.StartsWith(RustSharpMetadataConsumer.InvalidMetadata, StringComparison.Ordinal)),
                "A malformed contract must have the stable import diagnostic.");
            string output = Path.Combine(directory, "RejectedConsumer.dll");
            const string consumer = "use ContractProducer::add; fn main() { println!(\"{}\", add(20, 22)); }";
            CompilationResult result = CompilerDriver.CompileWithMetadataReferences(consumer, Path.Combine(directory, "consumer.rs"),
                output, "RejectedConsumer", CompilationProfile.SafeCorePrimitives, [producerPath], cancellationToken: deadline.Token);
            AssertEx.False(result.Success, "The consumer must reject the contract before emission.");
            AssertEx.False(File.Exists(output), "A rejected source contract must not publish a PE.");
        }
        finally { TryDelete(directory); }
        return Task.CompletedTask;
    }

    private static void AssertExternalMemberRef(string path, string producer, string method, CancellationToken cancellationToken)
    {
        using FileStream stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        bool found = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int count = 0;
        foreach (MemberReferenceHandle handle in metadata.MemberReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssertEx.True(++count <= 256 && clock.Elapsed < TimeSpan.FromSeconds(2),
                "The source MemberRef inspection must stay inside its fixed table/time budget.");
            MemberReference reference = metadata.GetMemberReference(handle);
            if (metadata.GetString(reference.Name) != method || reference.Parent.Kind != HandleKind.TypeReference) continue;
            TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)reference.Parent);
            if (type.ResolutionScope.Kind != HandleKind.AssemblyReference) continue;
            AssemblyReference assembly = metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope);
            found |= metadata.GetString(assembly.Name) == producer;
        }
        AssertEx.True(found, "The source consumer must contain a MemberRef scoped to the independent producer AssemblyRef.");
    }

    private static Task ReorderedMetadataReferencesAreDeterministicAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "rustsharp-p1-source-contract-deterministic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string producerOneSourcePath = Path.Combine(directory, "one.rs");
        string producerTwoSourcePath = Path.Combine(directory, "two.rs");
        string producerOneOutput = Path.Combine(directory, "One.dll");
        string producerTwoOutput = Path.Combine(directory, "Two.dll");
        string consumerSourcePath = Path.Combine(directory, "consumer.rs");
        string firstOutput = Path.Combine(directory, "first", "Consumer.dll");
        string secondOutput = Path.Combine(directory, "second", "Consumer.dll");
        const string producerOneSource = "pub fn one(value: i32) -> i32 { value + 1 } fn main() {}";
        const string producerTwoSource = "pub fn two(value: bool) -> bool { value } fn main() {}";
        const string consumerSource = "fn main() { println!(\"stable\"); }";
        try
        {
            File.WriteAllText(producerOneSourcePath, producerOneSource);
            File.WriteAllText(producerTwoSourcePath, producerTwoSource);
            File.WriteAllText(consumerSourcePath, consumerSource);
            Directory.CreateDirectory(Path.GetDirectoryName(firstOutput)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondOutput)!);
            CompilationResult producerOne = CompilerDriver.CompileFile(
                producerOneSourcePath, producerOneOutput, "One", CompilationProfile.SafeCorePrimitives);
            CompilationResult producerTwo = CompilerDriver.CompileFile(
                producerTwoSourcePath, producerTwoOutput, "Two", CompilationProfile.SafeCorePrimitives);
            AssertEx.True(producerOne.Success && producerTwo.Success,
                "The deterministic producer fixtures must compile: " +
                string.Join("; ", producerOne.Diagnostics.Concat(producerTwo.Diagnostics)));

            CompilationResult first = CompilerDriver.CompileWithMetadataReferences(
                consumerSource, consumerSourcePath, firstOutput, "Consumer", CompilationProfile.SafeCorePrimitives,
                [producerTwoOutput, producerOneOutput]);
            CompilationResult second = CompilerDriver.CompileWithMetadataReferences(
                consumerSource, consumerSourcePath, secondOutput, "Consumer", CompilationProfile.SafeCorePrimitives,
                [producerOneOutput, producerTwoOutput]);
            AssertEx.True(first.Success && second.Success,
                "Both consumer reference orders must compile: " +
                string.Join("; ", first.Diagnostics.Concat(second.Diagnostics)));
            AssertEx.True(first.Output is not null && second.Output is not null,
                "Both deterministic consumer compilations must publish outputs.");
            CompilationOutput firstArtifact = first.Output!;
            CompilationOutput secondArtifact = second.Output!;

            AssertEx.True(File.ReadAllBytes(firstArtifact.AssemblyPath).AsSpan()
                .SequenceEqual(File.ReadAllBytes(secondArtifact.AssemblyPath)),
                "Reordering equivalent metadata references must preserve PE bytes.");
            AssertEx.True(File.ReadAllBytes(firstArtifact.PdbPath!).AsSpan()
                .SequenceEqual(File.ReadAllBytes(secondArtifact.PdbPath!)),
                "Reordering equivalent metadata references must preserve PDB bytes.");
        }
        finally
        {
            TryDelete(directory);
        }

        return Task.CompletedTask;
    }

    private static Task RejectsStaleAggregateMetadataAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "rustsharp-p1-source-contract-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string assemblyPath = Path.Combine(directory, "StaleProducer.dll");
        try
        {
            var pair = new ClrLirValueType("Pair",
            [
                new ClrLirField("left", ClrLirType.I32),
                new ClrLirField("right", ClrLirType.Bool),
            ]);
            var main = new ClrLirMethod("Main", ClrLirType.Void, [], [],
                [new ClrLirBlock("entry", [new ClrLirReturn()])]);
            var makePair = new ClrLirMethod("MakePair", pair.Type, [], [],
                [new ClrLirBlock("entry",
                [
                    new ClrLirLoadInt32(7),
                    new ClrLirLoadBoolean(true),
                    new ClrLirConstructValue(pair),
                    new ClrLirReturn(),
                ])]);
            const string source = "pub fn make_pair() -> Pair { Pair { left: 7, right: true } } fn main() {}";
            var program = new SafeCoreClrResult(
                [main, makePair],
                [new(0, source.Length), new(0, source.Length)],
                [])
            {
                ValueTypes = [pair],
            };

            // The emitter reconciles a supplied document with the generated
            // layout before embedding it. Patch only the embedded JSON bytes
            // afterwards to model a stale producer artifact from an older
            // build, while keeping the actual Field table unchanged.
            RustSharpMetadataDocument stale = RustSharpMetadataDocument.ForProgram(
                "safe-core-mir-v1",
                Encoding.UTF8.GetBytes(source),
                program.Methods,
                valueTypes:
                [new RustSharpMetadataValueType("Pair",
                [
                    new RustSharpMetadataField("left", "I32"),
                    new RustSharpMetadataField("right", "Bool"),
                ])]);
            GeneratedAssembly generated = ClrLirAssemblyEmitter.EmitProgram(
                program,
                "StaleProducer",
                source,
                Path.Combine(directory, "producer.rs"),
                "StaleProducer.pdb",
                Encoding.UTF8.GetBytes(source),
                sourceMap: null,
                metadataDocument: stale);
            byte[] image = generated.PeImage.ToArray();
            byte[] current = Encoding.UTF8.GetBytes("\"name\":\"right\",\"type\":\"Bool\"");
            byte[] staleName = Encoding.UTF8.GetBytes("\"name\":\"other\",\"type\":\"Bool\"");
            int metadataOffset = FindBytes(image, current);
            AssertEx.True(metadataOffset >= 0,
                "The generated PE must carry the expected value layout metadata bytes.");
            Buffer.BlockCopy(staleName, 0, image, metadataOffset, staleName.Length);
            File.WriteAllBytes(assemblyPath, image);

            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(
                assemblyPath,
                "safe-core-mir-v1",
                ["MakePair"]);
            AssertEx.False(imported.IsSuccessful,
                "A stale producer layout must be rejected before a consumer can import it.");
            AssertEx.True(imported.Diagnostics.Any(diagnostic =>
                    diagnostic.Contains("value layout 'Pair' field order or type", StringComparison.Ordinal)),
                "The rejection must identify the generated Field table/layout mismatch: " +
                string.Join("; ", imported.Diagnostics));
        }
        finally
        {
            TryDelete(directory);
        }

        return Task.CompletedTask;
    }

    private static Task RejectsUnsupportedSourceAggregateImportAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "rustsharp-p1-source-contract-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string producerSourcePath = Path.Combine(directory, "producer.rs");
        string producerOutputPath = Path.Combine(directory, "Producer.dll");
        string consumerSourcePath = Path.Combine(directory, "consumer.rs");
        string consumerOutputPath = Path.Combine(directory, "Consumer.dll");
        const string producerSource =
            "pub struct Pair { pub left: i32, pub right: bool } " +
            "pub fn make_pair() -> Pair { Pair { left: 7, right: true } } fn main() {}";
        const string consumerSource =
            "use Producer::make_pair; fn main() { let pair = make_pair(); println!(\"{}\", pair.left); }";
        try
        {
            File.WriteAllText(producerSourcePath, producerSource);
            CompilationResult producer = CompilerDriver.CompileFile(
                producerSourcePath,
                producerOutputPath,
                assemblyName: "Producer",
                profile: CompilationProfile.SafeCoreMirV2);
            AssertEx.True(producer.Success,
                "The source producer fixture must compile before probing the import boundary: " +
                string.Join("; ", producer.Diagnostics));

            File.WriteAllText(consumerSourcePath, consumerSource);
            CompilationResult consumer = CompilerDriver.CompileWithMetadataReferences(
                consumerSource,
                consumerSourcePath,
                consumerOutputPath,
                "Consumer",
                CompilationProfile.SafeCoreMirV2,
                [producerOutputPath],
                ["crate::make_pair"]);
            AssertEx.False(consumer.Success,
                "Unsupported source aggregate imports must fail before CLR emission.");
            AssertEx.False(File.Exists(consumerOutputPath),
                "A rejected source aggregate import must not publish a consumer assembly.");
            AssertEx.True(consumer.Diagnostics.Any(diagnostic =>
                    string.Equals(diagnostic.Code, "RSN1003", StringComparison.Ordinal)),
                "The source import rejection must carry a stable compiler diagnostic: " +
                string.Join("; ", consumer.Diagnostics));
        }
        finally
        {
            TryDelete(directory);
        }

        return Task.CompletedTask;
    }

    private static void TryDelete(string directory)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(fullPath), tempRoot, comparison) ||
            !Path.GetFileName(fullPath).StartsWith("rustsharp-p1-source-contract-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside this test's exclusive temporary directory.");
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
    }

    private static int FindBytes(byte[] haystack, byte[] needle)
    {
        for (int offset = 0; offset <= haystack.Length - needle.Length; offset++)
        {
            bool match = true;
            for (int index = 0; index < needle.Length; index++)
            {
                if (haystack[offset + index] != needle[index])
                {
                    match = false;
                    break;
                }
            }

            if (match) return offset;
        }

        return -1;
    }
}
