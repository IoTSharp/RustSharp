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
        new("RustSharp metadata is embedded in generated PE", EmbeddedAsync),
        new("CompilerDriver embeds safe-core metadata", CompilerDriverAsync),
    ];

    private static Task CanonicalAsync()
    {
        var first = new RustSharpMetadataDocument(
            "safe-core-mir-v1",
            new string('a', 64),
            [new("z", "i32"), new("a", "()")],
            [new("crate::f", "i32"), new("crate::f", "bool")],
            ["Display", "Copy", "Display"]);
        var second = new RustSharpMetadataDocument(
            "safe-core-mir-v1",
            new string('a', 64),
            [new("a", "()"), new("z", "i32")],
            [new("crate::f", "bool"), new("crate::f", "i32")],
            ["Copy", "Display"]);
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
}
