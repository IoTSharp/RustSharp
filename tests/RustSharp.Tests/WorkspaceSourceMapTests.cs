using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using RustSharp.Compiler;

namespace RustSharp.Tests;

internal static class WorkspaceSourceMapTests
{
    private const string RootSource = "mod math;\nmod empty;\n\nfn main() { println!(\"{}\", math::answer()); }\n";
    private const string MathSource = "// 😀 CRLF and BOM\r\n  pub fn answer() -> i32 {\r\n      42\r\n  }\r\n";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Guid Sha256DocumentHashAlgorithm = new("8829d00f-11b8-4213-878b-770e8597ac16");

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("workspace PDB preserves every source hash and original function location", EmitsOriginalDocumentsAsync),
        new("workspace emission is deterministic and tracks child bytes and paths", PreservesSourceIdentityAsync),
    ];

    private static Task EmitsOriginalDocumentsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var workspace = new TestWorkspace();
        SourceFiles files = WriteSources(Path.Combine(workspace.DirectoryPath, "src"), "// empty module without final newline");
        string output = Path.Combine(workspace.DirectoryPath, "out", "program.dll");
        Compile(files.RootPath, output, deadline.Token);

        using var provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(Path.ChangeExtension(output, ".pdb")));
        MetadataReader reader = provider.GetMetadataReader();
        AssertEx.Equal(3, reader.Documents.Count, "Even a module without functions must have an original PDB document.");
        string[] expectedPaths = [files.RootPath, files.MathPath, files.EmptyPath];
        int documentIndex = 0;
        foreach (DocumentHandle handle in reader.Documents)
        {
            deadline.Token.ThrowIfCancellationRequested();
            AssertEx.True(documentIndex < expectedPaths.Length, "Unexpected PDB document.");
            Document document = reader.GetDocument(handle);
            string expectedPath = expectedPaths[documentIndex++];
            AssertEx.Equal(expectedPath, reader.GetString(document.Name), "Documents must retain deterministic loading order.");
            AssertEx.Equal(Sha256DocumentHashAlgorithm, reader.GetGuid(document.HashAlgorithm));
            AssertEx.True(SHA256.HashData(File.ReadAllBytes(expectedPath)).AsSpan().SequenceEqual(reader.GetBlobBytes(document.Hash)),
                "PDB checksums must hash original file bytes, including a UTF-8 BOM.");
        }

        using var pe = new PEReader(File.OpenRead(output));
        MetadataReader peReader = pe.GetMetadataReader();
        AssertEx.Equal(peReader.MethodDefinitions.Count, reader.MethodDebugInformation.Count,
            "Every source or generated method must retain a corresponding PDB row.");
        AssertEx.True(reader.MethodDebugInformation.Count <= 16, "The source-map fixture has a bounded method table.");
        int mappedMethods = 0;
        bool sawRoot = false;
        bool sawMath = false;
        foreach (MethodDebugInformationHandle handle in reader.MethodDebugInformation)
        {
            deadline.Token.ThrowIfCancellationRequested();
            MethodDebugInformation method = reader.GetMethodDebugInformation(handle);
            SequencePoint[] points = method.GetSequencePoints().Take(17).ToArray();
            AssertEx.True(points.Length <= 16, "The source-map fixture has a bounded sequence-point table.");
            MethodDefinition definition = peReader.GetMethodDefinition(
                MetadataTokens.MethodDefinitionHandle(MetadataTokens.GetRowNumber(handle)));
            TypeDefinition declaringType = peReader.GetTypeDefinition(definition.GetDeclaringType());
            bool sourceMethod = peReader.GetString(declaringType.Name) == "Program" &&
                peReader.GetString(declaringType.Namespace) == "RustSharp.Generated";
            if (!sourceMethod)
            {
                AssertEx.True(method.Document.IsNil && points.All(static point => point.IsHidden),
                    "Generated value constructors and runtime adapters must not invent source locations.");
                continue;
            }
            AssertEx.True(mappedMethods++ < 2, "Unexpected source method mapping.");
            AssertEx.False(method.Document.IsNil, "Each source method must identify its original source document.");
            SequencePoint[] visible = points.Where(static point => !point.IsHidden).ToArray();
            AssertEx.True(visible.Length > 0, "Each original function must retain a visible source mapping.");
            foreach (SequencePoint visiblePoint in visible)
                AssertEx.True(visiblePoint.Document.IsNil || visiblePoint.Document == method.Document,
                    "All visible function mappings must belong to the original function document.");
            SequencePoint point = visible[0];
            AssertEx.Equal(0, point.Offset);
            string path = reader.GetString(reader.GetDocument(method.Document).Name);
            if (path == files.RootPath)
            {
                AssertEx.False(sawRoot, "The root method must appear once.");
                sawRoot = true;
                AssertEx.Equal(4, point.StartLine);
                AssertEx.Equal(0, point.StartColumn);
                AssertEx.Equal(4, point.EndLine);
                AssertEx.Equal("fn main() { println!(\"{}\", math::answer()); }".Length, point.EndColumn);
            }
            else
            {
                AssertEx.Equal(files.MathPath, path);
                AssertEx.False(sawMath, "The child method must appear once.");
                sawMath = true;
                AssertEx.Equal(2, point.StartLine);
                AssertEx.Equal(6, point.StartColumn);
                AssertEx.Equal(4, point.EndLine);
                AssertEx.Equal(3, point.EndColumn);
            }
        }
        AssertEx.Equal(2, mappedMethods, "Both original functions must retain their own visible mappings.");
        AssertEx.True(sawRoot && sawMath, "Both root and child functions must map to their own original files.");
        return Task.CompletedTask;
    }

    private static Task PreservesSourceIdentityAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var workspace = new TestWorkspace();
        SourceFiles files = WriteSources(Path.Combine(workspace.DirectoryPath, "src"), "// first comment");
        string output = Path.Combine(workspace.DirectoryPath, "out", "program.dll");
        Compile(files.RootPath, output, deadline.Token);
        byte[] firstPe = File.ReadAllBytes(output);
        byte[] firstPdb = File.ReadAllBytes(Path.ChangeExtension(output, ".pdb"));
        Compile(files.RootPath, output, deadline.Token);
        AssertEx.True(firstPe.AsSpan().SequenceEqual(File.ReadAllBytes(output)), "Repeated workspace PE emission must be deterministic.");
        AssertEx.True(firstPdb.AsSpan().SequenceEqual(File.ReadAllBytes(Path.ChangeExtension(output, ".pdb"))),
            "Repeated workspace PDB emission must be deterministic.");

        File.WriteAllText(files.EmptyPath, "// other comment", Utf8);
        Compile(files.RootPath, output, deadline.Token);
        byte[] changedPe = File.ReadAllBytes(output);
        AssertEx.False(GetModuleVersionId(firstPe) == GetModuleVersionId(changedPe),
            "Changing only comments in a child with no methods must change the source identity.");

        SourceFiles relocated = WriteSources(Path.Combine(workspace.DirectoryPath, "relocated"), "// other comment");
        Compile(relocated.RootPath, output, deadline.Token);
        AssertEx.False(GetModuleVersionId(changedPe) == GetModuleVersionId(File.ReadAllBytes(output)),
            "Changing only source file paths must change the source identity.");
        return Task.CompletedTask;
    }

    private static SourceFiles WriteSources(string directory, string emptySource)
    {
        Directory.CreateDirectory(directory);
        var files = new SourceFiles(Path.Combine(directory, "main.rs"), Path.Combine(directory, "math.rs"), Path.Combine(directory, "empty.rs"));
        File.WriteAllText(files.RootPath, RootSource, Utf8);
        File.WriteAllBytes(files.MathPath, [0xef, 0xbb, 0xbf, .. Utf8.GetBytes(MathSource)]);
        File.WriteAllText(files.EmptyPath, emptySource, Utf8);
        return files;
    }

    private static void Compile(string sourcePath, string outputPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CompilationResult result = CompilerDriver.CompileFile(sourcePath, outputPath, "Workspace.SourceMap",
            CompilationProfile.SafeCorePrimitives, cancellationToken);
        AssertEx.True(result.Success, string.Join("; ", result.Diagnostics));
        AssertEx.NotNull(result.Output, "Successful workspace compilation must write artifacts.");
    }

    private static Guid GetModuleVersionId(byte[] image)
    {
        using var stream = new MemoryStream(image, writable: false);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
    }

    private sealed record SourceFiles(string RootPath, string MathPath, string EmptyPath);

    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "tests"));
        private readonly string _name = $"workspace-pdb-{Environment.ProcessId}-{Guid.NewGuid():N}";

        public TestWorkspace()
        {
            DirectoryPath = Path.Combine(_parent, _name);
            AssertEx.False(Directory.Exists(DirectoryPath), "A test workspace must be exclusively owned.");
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }

        public void Dispose()
        {
            string resolved = Path.GetFullPath(DirectoryPath);
            AssertEx.Equal(Path.Combine(_parent, _name), resolved, "Only this test's exact directory may be removed.");
            AssertEx.Equal(_name, Path.GetRelativePath(_parent, resolved), "Cleanup must stay within artifacts/tests.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
