using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class EmissionTests
{
    private const int MaximumMetadataItems = 128;
    private const int ConcurrentCompilationCount = 4;
    private const int MaximumResidualTransactionDirectories = 32;
    private const int MaximumConcurrentCleanupAttempts = 6;
    private const string ValidSource = "fn main() { println!(\"Hello from emitted IL\"); }";
    private static readonly Guid Sha256DocumentHashAlgorithm =
        new("8829d00f-11b8-4213-878b-770e8597ac16");
    private static readonly TimeSpan ConcurrentCompilationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConcurrentCleanupTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ConcurrentCleanupRetryDelay = TimeSpan.FromMilliseconds(50);

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("compiler driver reports a missing source file", ReportsMissingSourceFileAsync),
        new("compiler driver reports invalid syntax", ReportsInvalidSyntaxAsync),
        new("compiler driver bounds in-memory source text", ReportsOversizedInMemorySourceAsync),
        new("IL emission is byte-for-byte deterministic", EmitsDeterministicallyAsync),
        new("emitted PE has the expected entry point metadata", EmitsExpectedPeMetadataAsync),
        new("emitted portable PDB contains the source document", EmitsPortablePdbDocumentAsync),
        new("emitted portable PDB maps sequence points to source spans", EmitsPortablePdbSequencePointsAsync),
        new("emitted portable PDB hashes the original source bytes", EmitsPortablePdbSourceHashAsync),
        new("compiler writes PE, PDB, and runtime config", WritesCompilationArtifactsAsync),
        new("concurrent compiler writes produce readable artifacts", ConcurrentWritesProduceReadableArtifactsAsync),
    ];

    private static Task ReportsMissingSourceFileAsync()
    {
        string repositoryRoot = GetRepositoryRoot();
        string missingPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "tests",
            $"missing-{Environment.ProcessId}.rs");

        AssertEx.False(File.Exists(missingPath), "The missing-source test path must not exist.");

        CompilationResult result = CompilerDriver.CheckFile(missingPath);

        AssertEx.False(result.Success, "Checking a missing source file must fail.");
        AssertEx.Equal(1, result.Diagnostics.Count);
        AssertEx.Equal("RSC0001", result.Diagnostics[0].Code);
        AssertEx.True(result.Output is null, "A failed check must not produce compiler output.");
        return Task.CompletedTask;
    }

    private static Task ReportsInvalidSyntaxAsync()
    {
        const string invalidSource = "fn main() { println!(\"missing semicolon\") }";

        CompilationResult result = CompilerDriver.Check(invalidSource, "invalid.rs");

        AssertEx.False(result.Success, "Checking invalid syntax must fail.");
        AssertEx.True(result.Diagnostics.Count > 0, "Invalid syntax must produce a diagnostic.");
        AssertEx.Equal("RSC1001", result.Diagnostics[0].Code);
        AssertEx.True(result.Output is null, "A failed check must not produce compiler output.");
        return Task.CompletedTask;
    }

    private static Task ReportsOversizedInMemorySourceAsync()
    {
        string oversizedSource = new('x', (16 * 1024 * 1024) + 1);
        string outputPath = Path.Combine(
            GetRepositoryRoot(),
            "artifacts",
            "tests",
            $"oversized-{Environment.ProcessId}.dll");

        CompilationResult result = CompilerDriver.Compile(
            oversizedSource,
            "oversized.rs",
            outputPath,
            "Emission.Oversized");

        AssertEx.False(result.Success, "An oversized in-memory source must be rejected.");
        AssertEx.Equal(1, result.Diagnostics.Count);
        AssertEx.Equal("RSC0004", result.Diagnostics[0].Code);
        AssertEx.False(File.Exists(outputPath), "Rejected source must not write compiler output.");
        return Task.CompletedTask;
    }

    private static Task EmitsDeterministicallyAsync()
    {
        string sourcePath = Path.Combine(GetRepositoryRoot(), "samples", "deterministic.rs");
        GeneratedAssembly first = Emit(ValidSource, sourcePath, "Emission.Deterministic");
        GeneratedAssembly second = Emit(ValidSource, sourcePath, "Emission.Deterministic");
        byte[] firstPdb = AssertEx.NotNull(first.PdbImage, "The first emission must contain a PDB.");
        byte[] secondPdb = AssertEx.NotNull(second.PdbImage, "The second emission must contain a PDB.");

        AssertEx.True(
            first.PeImage.AsSpan().SequenceEqual(second.PeImage),
            "Identical compiler input must produce byte-identical PE images.");
        AssertEx.True(
            firstPdb.AsSpan().SequenceEqual(secondPdb),
            "Identical compiler input must produce byte-identical PDB images.");
        return Task.CompletedTask;
    }

    private static Task EmitsExpectedPeMetadataAsync()
    {
        const string assemblyName = "Emission.Metadata";
        string sourcePath = Path.Combine(GetRepositoryRoot(), "samples", "metadata.rs");
        GeneratedAssembly generated = Emit(ValidSource, sourcePath, assemblyName);

        using var peStream = new MemoryStream(generated.PeImage, writable: false);
        using var peReader = new PEReader(peStream);
        AssertEx.True(peReader.HasMetadata, "The emitted PE must contain CLR metadata.");

        MetadataReader metadata = peReader.GetMetadataReader();
        AssertEx.Equal(assemblyName, metadata.GetString(metadata.GetAssemblyDefinition().Name));

        CorHeader corHeader = AssertEx.NotNull(
            peReader.PEHeaders.CorHeader,
            "The emitted PE must contain a CLR header.");
        int entryPointToken = corHeader.EntryPointTokenOrRelativeVirtualAddress;
        AssertEx.Equal(0x06000000, entryPointToken & unchecked((int)0xff000000));

        MethodDefinitionHandle mainHandle = MetadataTokens.MethodDefinitionHandle(entryPointToken & 0x00ffffff);
        MethodDefinition main = metadata.GetMethodDefinition(mainHandle);
        AssertEx.Equal("Main", metadata.GetString(main.Name));
        AssertEx.Equal(
            MethodAttributes.Public,
            main.Attributes & MethodAttributes.MemberAccessMask,
            "The generated entry point must be public.");
        AssertEx.True(
            (main.Attributes & MethodAttributes.Static) != 0,
            "The generated entry point must be static.");

        TypeDefinition declaringType = metadata.GetTypeDefinition(main.GetDeclaringType());
        AssertEx.Equal("RustSharp.Generated", metadata.GetString(declaringType.Namespace));
        AssertEx.Equal("Program", metadata.GetString(declaringType.Name));
        return Task.CompletedTask;
    }

    private static Task EmitsPortablePdbDocumentAsync()
    {
        string sourcePath = Path.Combine(GetRepositoryRoot(), "samples", "portable-pdb.rs");
        GeneratedAssembly generated = Emit(ValidSource, sourcePath, "Emission.PortablePdb");
        byte[] pdbImage = AssertEx.NotNull(generated.PdbImage, "IL emission must produce a portable PDB.");
        AssertEx.True(pdbImage.Length > 0, "The portable PDB must not be empty.");

        using var pdbStream = new MemoryStream(pdbImage, writable: false);
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        MetadataReader metadata = provider.GetMetadataReader();
        AssertEx.True(metadata.Documents.Count > 0, "The portable PDB must contain a document.");
        AssertEx.True(
            metadata.Documents.Count <= MaximumMetadataItems,
            $"PDB document count exceeds the test safety limit {MaximumMetadataItems}.");

        var foundSourceDocument = false;
        foreach (DocumentHandle handle in metadata.Documents)
        {
            Document document = metadata.GetDocument(handle);
            string documentName = metadata.GetString(document.Name);
            if (documentName.EndsWith(sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                foundSourceDocument = true;
                break;
            }
        }

        AssertEx.True(foundSourceDocument, "The portable PDB document name must end with the source path.");
        return Task.CompletedTask;
    }

    private static Task EmitsPortablePdbSequencePointsAsync()
    {
        const string source =
            "fn main() {\n" +
            "    // sequence points should skip trivia\n" +
            "    println!(\"first\");\n" +
            "\n" +
            "      println!(\"second\");\n" +
            "}\n";
        string sourcePath = Path.Combine(GetRepositoryRoot(), "samples", "sequence-points.rs");
        SyntaxTree syntaxTree = SyntaxTree.Parse(source, sourcePath);
        AssertEx.Equal(0, syntaxTree.Diagnostics.Count);
        CompilationUnitSyntax root = AssertEx.NotNull(
            syntaxTree.Root,
            "The sequence-point source must produce a syntax root.");
        AssertEx.Equal(2, root.Statements.Count);

        GeneratedAssembly generated = Emit(source, sourcePath, "Emission.SequencePoints");
        byte[] pdbImage = AssertEx.NotNull(generated.PdbImage, "IL emission must produce a portable PDB.");

        MethodDefinitionHandle entryPoint = GetEntryPointMethod(generated.PeImage);
        using var pdbStream = new MemoryStream(pdbImage, writable: false);
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        MetadataReader metadata = provider.GetMetadataReader();

        AssertEx.Equal(1, metadata.Documents.Count, "The source must map to one PDB document.");
        DocumentHandle documentHandle = metadata.Documents.First();
        MethodDebugInformation debugInformation = metadata.GetMethodDebugInformation(entryPoint);
        AssertEx.Equal(
            documentHandle,
            debugInformation.Document,
            "Main's method debug information must reference the source document.");

        var sequencePoints = new List<SequencePoint>();
        foreach (SequencePoint sequencePoint in debugInformation.GetSequencePoints())
        {
            if (sequencePoints.Count >= MaximumMetadataItems)
            {
                throw new InvalidOperationException(
                    $"Sequence-point count exceeds the test safety limit {MaximumMetadataItems}.");
            }

            sequencePoints.Add(sequencePoint);
        }

        AssertEx.Equal(root.Statements.Count, sequencePoints.Count);
        int previousOffset = -1;
        for (int index = 0; index < sequencePoints.Count; index++)
        {
            SequencePoint sequencePoint = sequencePoints[index];
            PrintStatementSyntax statement = root.Statements[index];
            (int expectedStartLine, int expectedStartColumn) = GetLineAndColumn(source, statement.Span.Start);
            (int expectedEndLine, int expectedEndColumn) = GetLineAndColumn(source, statement.Span.End);

            AssertEx.False(sequencePoint.IsHidden, "A println statement must have a visible sequence point.");
            AssertEx.True(
                sequencePoint.Offset > previousOffset,
                "Sequence-point IL offsets must be strictly increasing.");
            AssertEx.Equal(documentHandle, sequencePoint.Document);
            AssertEx.Equal(expectedStartLine, sequencePoint.StartLine);
            AssertEx.Equal(expectedStartColumn, sequencePoint.StartColumn);
            AssertEx.Equal(expectedEndLine, sequencePoint.EndLine);
            AssertEx.Equal(expectedEndColumn, sequencePoint.EndColumn);
            previousOffset = sequencePoint.Offset;
        }

        return Task.CompletedTask;
    }

    private static Task EmitsPortablePdbSourceHashAsync()
    {
        const string source =
            "fn main() {\n" +
            "    println!(\"\u4F60\u597D Rust# \U0001F680\");\n" +
            "}\n";
        string sourcePath = Path.Combine(GetRepositoryRoot(), "samples", "source-hash.rs");
        SyntaxTree syntaxTree = SyntaxTree.Parse(source, sourcePath);
        AssertEx.Equal(0, syntaxTree.Diagnostics.Count);
        CompilationUnitSyntax root = AssertEx.NotNull(
            syntaxTree.Root,
            "The source-hash input must produce a syntax root.");
        AssertEx.Equal(
            "\u4F60\u597D Rust# \U0001F680",
            root.Statements[0].Value,
            "The BOM-free source text must parse its non-ASCII literal.");

        byte[] encodedSource = Encoding.UTF8.GetBytes(source);
        byte[] utf8Preamble = Encoding.UTF8.GetPreamble();
        byte[] originalSourceBytes = new byte[utf8Preamble.Length + encodedSource.Length];
        utf8Preamble.AsSpan().CopyTo(originalSourceBytes);
        encodedSource.AsSpan().CopyTo(originalSourceBytes.AsSpan(utf8Preamble.Length));
        AssertEx.True(
            originalSourceBytes.Length > encodedSource.Length,
            "The checksum fixture must include a UTF-8 BOM.");

        GeneratedAssembly generated = IlAssemblyEmitter.Emit(
            root,
            source,
            sourcePath,
            "Emission.SourceHash",
            sourceBytes: originalSourceBytes);
        byte[] pdbImage = AssertEx.NotNull(generated.PdbImage, "IL emission must produce a portable PDB.");

        using var pdbStream = new MemoryStream(pdbImage, writable: false);
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        MetadataReader metadata = provider.GetMetadataReader();
        AssertEx.Equal(1, metadata.Documents.Count, "The source must map to one PDB document.");

        DocumentHandle documentHandle = MetadataTokens.DocumentHandle(1);
        Document document = metadata.GetDocument(documentHandle);
        AssertEx.Equal(
            Sha256DocumentHashAlgorithm,
            metadata.GetGuid(document.HashAlgorithm),
            "The document checksum algorithm must be SHA-256.");

        byte[] expectedHash = SHA256.HashData(originalSourceBytes);
        byte[] actualHash = metadata.GetBlobBytes(document.Hash);
        AssertEx.True(
            expectedHash.AsSpan().SequenceEqual(actualHash),
            "The document checksum must cover the original source bytes, including the BOM.");
        return Task.CompletedTask;
    }

    private static Task WritesCompilationArtifactsAsync()
    {
        string repositoryRoot = GetRepositoryRoot();
        string artifactsTestsRoot = Path.GetFullPath(
            Path.Combine(repositoryRoot, "artifacts", "tests"));
        string taskDirectory = Path.GetFullPath(
            Path.Combine(artifactsTestsRoot, $"emission-{Environment.ProcessId}"));
        bool taskDirectoryCreated = false;

        ValidateTaskDirectory(artifactsTestsRoot, taskDirectory);
        AssertEx.False(
            Directory.Exists(taskDirectory),
            $"The test-owned output directory already exists: '{taskDirectory}'.");

        try
        {
            Directory.CreateDirectory(taskDirectory);
            taskDirectoryCreated = true;

            string sourcePath = Path.Combine(taskDirectory, "main.rs");
            string assemblyPath = Path.Combine(taskDirectory, "Emission.Artifacts.dll");
            CompilationResult result = CompilerDriver.Compile(
                ValidSource,
                sourcePath,
                assemblyPath,
                "Emission.Artifacts");

            AssertEx.True(result.Success, "Compilation to the test artifacts directory must succeed.");
            CompilationOutput output = AssertEx.NotNull(
                result.Output,
                "Successful compilation must describe its output files.");
            string pdbPath = AssertEx.NotNull(output.PdbPath, "Successful compilation must produce a PDB path.");

            AssertEx.True(File.Exists(output.AssemblyPath), "Compilation must write the DLL.");
            AssertEx.True(File.Exists(pdbPath), "Compilation must write the portable PDB.");
            AssertEx.True(File.Exists(output.RuntimeConfigPath), "Compilation must write the runtime config.");
        }
        finally
        {
            if (taskDirectoryCreated)
            {
                ValidateTaskDirectory(artifactsTestsRoot, taskDirectory);
                Directory.Delete(taskDirectory, recursive: true);
            }
        }

        return Task.CompletedTask;
    }

    private static async Task ConcurrentWritesProduceReadableArtifactsAsync()
    {
        string repositoryRoot = GetRepositoryRoot();
        string artifactsTestsRoot = Path.GetFullPath(
            Path.Combine(repositoryRoot, "artifacts", "tests"));
        string taskDirectoryName = $"emission-concurrent-{Environment.ProcessId}-{Guid.NewGuid():N}";
        string taskDirectory = Path.GetFullPath(
            Path.Combine(artifactsTestsRoot, taskDirectoryName));
        string sourcePath = Path.Combine(taskDirectory, "main.rs");
        string assemblyPath = Path.Combine(taskDirectory, "Emission.Concurrent.dll");
        var allTasks = new List<Task<CompilationResult>>(ConcurrentCompilationCount);
        Task<CompilationResult[]>? allCompilations = null;

        ValidateTaskDirectory(artifactsTestsRoot, taskDirectory, taskDirectoryName);
        AssertEx.False(
            Directory.Exists(taskDirectory),
            $"The concurrent test-owned output directory already exists: '{taskDirectory}'.");

        try
        {
            Directory.CreateDirectory(taskDirectory);

            for (var index = 0; index < ConcurrentCompilationCount; index++)
            {
                allTasks.Add(
                    Task.Run(
                        () => CompilerDriver.Compile(
                            ValidSource,
                            sourcePath,
                            assemblyPath,
                            "Emission.Concurrent")));
            }

            allCompilations = Task.WhenAll(allTasks);
            CompilationResult[] results = await allCompilations
                .WaitAsync(ConcurrentCompilationTimeout)
                .ConfigureAwait(false);

            AssertEx.Equal(ConcurrentCompilationCount, results.Length);
            foreach (CompilationResult result in results)
            {
                AssertEx.True(result.Success, "Every concurrent compilation must succeed.");
                CompilationOutput output = AssertEx.NotNull(
                    result.Output,
                    "Every successful concurrent compilation must describe its artifacts.");
                AssertEx.Equal(assemblyPath, output.AssemblyPath);
                string pdbPath = AssertEx.NotNull(
                    output.PdbPath,
                    "Every successful concurrent compilation must describe its PDB path.");
                AssertEx.Equal(Path.ChangeExtension(assemblyPath, ".pdb"), pdbPath);
                AssertEx.Equal(
                    Path.ChangeExtension(assemblyPath, ".runtimeconfig.json"),
                    output.RuntimeConfigPath);
            }

            AssertReadableCompilationArtifacts(assemblyPath);
            AssertNoResidualTransactionDirectories(taskDirectory);
        }
        finally
        {
            if (allCompilations is not null && !allCompilations.IsCompleted)
            {
                await ObserveCompilationTasksBoundedAsync(allCompilations).ConfigureAwait(false);
            }

            ValidateTaskDirectory(artifactsTestsRoot, taskDirectory, taskDirectoryName);
            await DeleteConcurrentTestDirectoryAsync(taskDirectory).ConfigureAwait(false);
        }
    }

    private static void AssertReadableCompilationArtifacts(string assemblyPath)
    {
        string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        string runtimeConfigPath = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");

        AssertEx.True(File.Exists(assemblyPath), "The concurrent compiler must write the DLL.");
        AssertEx.True(File.Exists(pdbPath), "The concurrent compiler must write the portable PDB.");
        AssertEx.True(
            File.Exists(runtimeConfigPath),
            "The concurrent compiler must write the runtime config.");

        byte[] peImage = File.ReadAllBytes(assemblyPath);
        AssertEx.True(peImage.Length > 0, "The concurrent DLL must not be empty.");
        using (var peStream = new MemoryStream(peImage, writable: false))
        using (var peReader = new PEReader(peStream))
        {
            AssertEx.True(peReader.HasMetadata, "The concurrent DLL must contain CLR metadata.");
            MetadataReader metadata = peReader.GetMetadataReader();
            AssertEx.Equal(
                "Emission.Concurrent",
                metadata.GetString(metadata.GetAssemblyDefinition().Name));
        }

        byte[] pdbImage = File.ReadAllBytes(pdbPath);
        AssertEx.True(pdbImage.Length > 0, "The concurrent PDB must not be empty.");
        using (var pdbStream = new MemoryStream(pdbImage, writable: false))
        using (MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream))
        {
            MetadataReader metadata = provider.GetMetadataReader();
            AssertEx.True(metadata.Documents.Count > 0, "The concurrent PDB must contain a document.");
            AssertEx.True(
                metadata.Documents.Count <= MaximumMetadataItems,
                $"Concurrent PDB document count exceeds the test safety limit {MaximumMetadataItems}.");
        }

        using JsonDocument runtimeConfig = JsonDocument.Parse(File.ReadAllBytes(runtimeConfigPath));
        AssertEx.Equal(
            JsonValueKind.Object,
            runtimeConfig.RootElement.ValueKind,
            "The concurrent runtime config must be valid JSON object data.");
    }

    private static void AssertNoResidualTransactionDirectories(string taskDirectory)
    {
        var residualDirectories = new List<string>(capacity: MaximumResidualTransactionDirectories);
        foreach (string directory in Directory.EnumerateDirectories(
                     taskDirectory,
                     ".rsc-transaction-*",
                     SearchOption.TopDirectoryOnly))
        {
            if (residualDirectories.Count == MaximumResidualTransactionDirectories)
            {
                break;
            }

            residualDirectories.Add(directory);
        }

        AssertEx.Equal(
            0,
            residualDirectories.Count,
            "Concurrent compilation must not leave transaction directories behind.");
    }

    private static async Task ObserveCompilationTasksBoundedAsync(
        Task<CompilationResult[]> allCompilations)
    {
        try
        {
            await allCompilations.WaitAsync(ConcurrentCleanupTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // CompilerDriver has its own bounded output-lock wait. Keep this test's
            // failure cleanup bounded if a task is unexpectedly still unwinding.
        }
        catch (IOException)
        {
            // The original compilation failure is reported by the test body.
        }
        catch (UnauthorizedAccessException)
        {
            // The original compilation failure is reported by the test body.
        }
    }

    private static async Task DeleteConcurrentTestDirectoryAsync(string taskDirectory)
    {
        var cleanupClock = Stopwatch.StartNew();
        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaximumConcurrentCleanupAttempts; attempt++)
        {
            if (cleanupClock.Elapsed >= ConcurrentCleanupTimeout)
            {
                break;
            }

            if (!Directory.Exists(taskDirectory))
            {
                return;
            }

            try
            {
                Directory.Delete(taskDirectory, recursive: true);
            }
            catch (IOException exception)
            {
                lastException = exception;
            }
            catch (UnauthorizedAccessException exception)
            {
                lastException = exception;
            }

            if (!Directory.Exists(taskDirectory))
            {
                return;
            }

            TimeSpan remaining = ConcurrentCleanupTimeout - cleanupClock.Elapsed;
            if (attempt >= MaximumConcurrentCleanupAttempts || remaining <= TimeSpan.Zero)
            {
                break;
            }

            TimeSpan delay = remaining < ConcurrentCleanupRetryDelay
                ? remaining
                : ConcurrentCleanupRetryDelay;
            await Task.Delay(delay).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"Could not remove concurrent test directory '{taskDirectory}' within " +
            $"{MaximumConcurrentCleanupAttempts} attempts or {ConcurrentCleanupTimeout.TotalSeconds:0.#} seconds.",
            lastException);
    }

    private static GeneratedAssembly Emit(
        string source,
        string sourcePath,
        string assemblyName,
        ReadOnlyMemory<byte> sourceBytes = default)
    {
        SyntaxTree syntaxTree = SyntaxTree.Parse(source, sourcePath);
        AssertEx.Equal(0, syntaxTree.Diagnostics.Count);
        var root = AssertEx.NotNull(syntaxTree.Root, "Valid source must produce a syntax root.");
        return IlAssemblyEmitter.Emit(root, source, sourcePath, assemblyName, sourceBytes: sourceBytes);
    }

    private static MethodDefinitionHandle GetEntryPointMethod(byte[] peImage)
    {
        using var peStream = new MemoryStream(peImage, writable: false);
        using var peReader = new PEReader(peStream);
        CorHeader corHeader = AssertEx.NotNull(
            peReader.PEHeaders.CorHeader,
            "The emitted PE must contain a CLR header.");
        int entryPointToken = corHeader.EntryPointTokenOrRelativeVirtualAddress;
        AssertEx.Equal(0x06000000, entryPointToken & unchecked((int)0xff000000));
        return MetadataTokens.MethodDefinitionHandle(entryPointToken & 0x00ffffff);
    }

    private static (int Line, int Column) GetLineAndColumn(string source, int offset)
    {
        AssertEx.True(
            offset >= 0 && offset <= source.Length,
            "A syntax span must be within the source text.");

        int line = 1;
        int column = 0;
        int position = 0;
        while (position < offset)
        {
            char current = source[position++];
            if (current == '\r')
            {
                if (position < offset && source[position] == '\n')
                {
                    position++;
                }

                line++;
                column = 0;
            }
            else if (current == '\n')
            {
                line++;
                column = 0;
            }
            else
            {
                column++;
            }
        }

        return (line, column);
    }

    private static string GetRepositoryRoot()
    {
        string repositoryRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        AssertEx.True(
            Directory.Exists(Path.Combine(repositoryRoot, "tests", "RustSharp.Tests")),
            $"Could not validate the repository root '{repositoryRoot}'.");
        return repositoryRoot;
    }

    private static void ValidateTaskDirectory(
        string artifactsTestsRoot,
        string taskDirectory,
        string? expectedDirectoryName = null)
    {
        string resolvedRoot = Path.GetFullPath(artifactsTestsRoot);
        string resolvedTarget = Path.GetFullPath(taskDirectory);
        string expectedTarget = Path.GetFullPath(
            Path.Combine(
                resolvedRoot,
                expectedDirectoryName ?? $"emission-{Environment.ProcessId}"));
        AssertEx.Equal(expectedTarget, resolvedTarget, "Refusing to operate on an unexpected test directory.");

        string relativeTarget = Path.GetRelativePath(resolvedRoot, resolvedTarget);
        AssertEx.False(Path.IsPathRooted(relativeTarget), "The test directory must be relative to artifacts/tests.");
        AssertEx.False(
            relativeTarget.StartsWith("..", StringComparison.Ordinal),
            "The test directory must remain inside artifacts/tests.");
        AssertEx.False(
            relativeTarget.Contains(Path.DirectorySeparatorChar) ||
            relativeTarget.Contains(Path.AltDirectorySeparatorChar),
            "The test directory must be a direct child of artifacts/tests.");
    }
}
