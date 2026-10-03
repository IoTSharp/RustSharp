using System.Text;
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
    ];

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
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // The test owns this bounded temporary directory; a locked file is
            // reported by the test process rather than deleting user content.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the same cleanup boundary if a child process still owns
            // a generated file.
        }
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
