using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;

namespace RustSharp.Tests;

internal static class ClrLirTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("CLR LIR validates locals calls branches and returns", ValidatesControlFlowAsync),
        new("CLR LIR emits deterministic branch bytecode", EmitsDeterministicBytecodeAsync),
        new("CLR LIR emits and runs a branch PE", EmitsAndRunsBranchPeAsync),
        new("CLR LIR rejects stack type mismatches", RejectsStackTypeMismatchAsync),
        new("CLR LIR rejects invalid call-site types", RejectsInvalidCallSiteTypesAsync),
        new("CLR LIR rejects invalid branch targets", RejectsInvalidBranchTargetAsync),
        new("CLR LIR rejects inconsistent merge stacks", RejectsInconsistentMergeAsync),
        new("CLR LIR rejects unreachable blocks", RejectsUnreachableBlockAsync),
    ];

    private static Task ValidatesControlFlowAsync()
    {
        var writeLine = new ClrLirCallSite("Console.WriteLine", ClrLirType.Void, [ClrLirType.Text]);
        var method = new ClrLirMethod(
            "Main",
            ClrLirType.I32,
            [],
            [new ClrLirLocal("result", ClrLirType.I32)],
            [
                new ClrLirBlock("entry", [
                    new ClrLirLoadBoolean(true),
                    new ClrLirBranchTrue("then"),
                ]),
                new ClrLirBlock("else", [
                    new ClrLirLoadInt32(0),
                    new ClrLirStoreLocal(0),
                    new ClrLirBranch("join"),
                ]),
                new ClrLirBlock("then", [
                    new ClrLirLoadInt32(1),
                    new ClrLirStoreLocal(0),
                    new ClrLirLoadString("branch"),
                    new ClrLirCall(writeLine),
                    new ClrLirBranch("join"),
                ]),
                new ClrLirBlock("join", [
                    new ClrLirLoadLocal(0),
                    new ClrLirReturn(),
                ]),
            ]);

        ClrLirValidationResult result = method.Validate();
        AssertEx.True(result.IsValid, string.Join(Environment.NewLine, result.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task RejectsStackTypeMismatchAsync()
    {
        var method = new ClrLirMethod(
            "BadStore",
            ClrLirType.Void,
            [],
            [new ClrLirLocal("value", ClrLirType.I32)],
            [new ClrLirBlock("entry", [
                new ClrLirLoadString("wrong"),
                new ClrLirStoreLocal(0),
                new ClrLirReturn(),
            ])]);

        ClrLirValidationResult result = method.Validate();
        AssertEx.False(result.IsValid, "Storing a string into an Int32 local must fail validation.");
        AssertEx.Equal("LIR009", result.Diagnostics[0].Code);
        return Task.CompletedTask;
    }

    private static Task EmitsDeterministicBytecodeAsync()
    {
        var method = new ClrLirMethod(
            "Branch",
            ClrLirType.I32,
            [],
            [],
            [
                new ClrLirBlock("entry", [
                    new ClrLirLoadBoolean(true),
                    new ClrLirBranchTrue("target"),
                ]),
                new ClrLirBlock("fallthrough", [
                    new ClrLirLoadInt32(0),
                    new ClrLirReturn(),
                ]),
                new ClrLirBlock("target", [
                    new ClrLirLoadInt32(1),
                    new ClrLirReturn(),
                ]),
            ]);

        ClrLirMethodBody first = ClrLirEmitter.EmitMethodBody(
            method,
            new MetadataBuilder(),
            static _ => MetadataTokens.MemberReferenceHandle(1));
        ClrLirMethodBody second = ClrLirEmitter.EmitMethodBody(
            method,
            new MetadataBuilder(),
            static _ => MetadataTokens.MemberReferenceHandle(1));

        AssertEx.True(first.IlBytes.AsSpan().SequenceEqual(second.IlBytes.AsSpan()), "LIR lowering must be deterministic.");
        AssertEx.True(first.IlBytes.Contains((byte)ILOpCode.Brtrue), "Lowered IL must contain a conditional branch.");
        AssertEx.True(first.IlBytes.Contains((byte)ILOpCode.Ret), "Lowered IL must contain returns.");
        AssertEx.True(first.MaxStack >= 1, "Lowered IL must report a positive max stack.");
        return Task.CompletedTask;
    }

    private static Task RejectsInvalidBranchTargetAsync()
    {
        var method = new ClrLirMethod(
            "BadBranch",
            ClrLirType.Void,
            [],
            [],
            [new ClrLirBlock("entry", [new ClrLirBranch("missing")])]);

        ClrLirValidationResult result = method.Validate();
        AssertEx.False(result.IsValid, "A branch to an unknown block must fail validation.");
        AssertEx.Equal("LIR010", result.Diagnostics[0].Code);
        bool emissionRejected = false;
        try
        {
            _ = ClrLirEmitter.EmitMethodBody(method, new MetadataBuilder(), static _ => MetadataTokens.MemberReferenceHandle(1));
        }
        catch (InvalidOperationException)
        {
            emissionRejected = true;
        }

        AssertEx.True(emissionRejected, "The emitter must reject invalid LIR before producing bytes.");
        return Task.CompletedTask;
    }

    private static Task RejectsInvalidCallSiteTypesAsync()
    {
        var invalidCall = new ClrLirCallSite("invalid", ClrLirType.Void, [ClrLirType.Void]);
        var method = new ClrLirMethod(
            "BadCall",
            ClrLirType.Void,
            [],
            [],
            [new ClrLirBlock("entry", [new ClrLirCall(invalidCall), new ClrLirReturn()])]);

        ClrLirValidationResult result = method.Validate();
        AssertEx.False(result.IsValid, "A call-site Void parameter must fail validation.");
        AssertEx.Equal("LIR013", result.Diagnostics[0].Code);
        return Task.CompletedTask;
    }

    private static async Task EmitsAndRunsBranchPeAsync()
    {
        var writeLine = new ClrLirCallSite("System.Console.WriteLine", ClrLirType.Void, [ClrLirType.Text]);
        var method = new ClrLirMethod(
            "Main",
            ClrLirType.I32,
            [],
            [new ClrLirLocal("result", ClrLirType.I32)],
            [
                new ClrLirBlock("entry", [
                    new ClrLirLoadBoolean(true),
                    new ClrLirBranchTrue("then"),
                ]),
                new ClrLirBlock("else", [
                    new ClrLirLoadString("else"),
                    new ClrLirCall(writeLine),
                    new ClrLirLoadInt32(0),
                    new ClrLirStoreLocal(0),
                    new ClrLirBranch("join"),
                ]),
                new ClrLirBlock("then", [
                    new ClrLirLoadString("then"),
                    new ClrLirCall(writeLine),
                    new ClrLirLoadInt32(1),
                    new ClrLirStoreLocal(0),
                    new ClrLirBranch("join"),
                ]),
                new ClrLirBlock("join", [
                    new ClrLirLoadLocal(0),
                    new ClrLirReturn(),
                ]),
            ]);

        GeneratedAssembly generated = ClrLirAssemblyEmitter.Emit(method, "ClrLir.Branch");
        string repositoryRoot = GetRepositoryRoot();
        string artifactsRoot = Path.Combine(repositoryRoot, "artifacts", "tests");
        string directoryName = $"clr-lir-pe-{Environment.ProcessId}-{Guid.NewGuid():N}";
        string outputDirectory = Path.Combine(artifactsRoot, directoryName);
        ValidateTaskDirectory(artifactsRoot, outputDirectory, directoryName);
        AssertEx.False(Directory.Exists(outputDirectory), "The LIR PE test directory must be unique.");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            string assemblyPath = Path.Combine(outputDirectory, "ClrLir.Branch.dll");
            File.WriteAllBytes(assemblyPath, generated.PeImage);
            File.WriteAllText(
                Path.ChangeExtension(assemblyPath, ".runtimeconfig.json"),
                generated.RuntimeConfigJson,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            using (var peStream = File.OpenRead(assemblyPath))
            using (var peReader = new PEReader(peStream))
            {
                AssertEx.True(peReader.HasMetadata, "The LIR PE must contain CLR metadata.");
                MetadataReader metadata = peReader.GetMetadataReader();
                CorHeader corHeader = AssertEx.NotNull(
                    peReader.PEHeaders.CorHeader,
                    "The LIR PE must contain a CLR header.");
                int token = corHeader.EntryPointTokenOrRelativeVirtualAddress;
                AssertEx.Equal(0x06000000, token & unchecked((int)0xff000000));
                MethodDefinitionHandle entryPoint =
                    MetadataTokens.MethodDefinitionHandle(token & 0x00ffffff);
                MethodDefinition definition = metadata.GetMethodDefinition(entryPoint);
                AssertEx.True(definition.RelativeVirtualAddress > 0, "The LIR entry point must have a body.");
                MethodBodyBlock body = peReader.GetMethodBody(definition.RelativeVirtualAddress);
                AssertEx.False(body.LocalSignature.IsNil, "The LIR branch sample must carry its local signature.");
                AssertEx.True(body.GetILBytes() is not null, "The LIR branch sample must carry IL bytes.");
            }

            var process = await new BoundedProcessRunner(TimeSpan.FromSeconds(2)).RunAsync(
                new BoundedProcessRequest(
                    "dotnet",
                    [assemblyPath],
                    outputDirectory,
                    TimeSpan.FromSeconds(30))).ConfigureAwait(false);
            AssertEx.Equal(BoundedProcessTermination.Exited, process.Termination);
            AssertEx.True(process.ExitCode == 1, "The selected then branch must return exit code 1.");
            AssertEx.Equal("then" + Environment.NewLine, process.StandardOutput);
            AssertEx.True(!process.OutputTruncated, "The branch sample output must remain bounded.");
            AssertEx.False(process.ProcessTreeCleanupIncomplete, "The branch sample must clean its process tree.");
        }
        finally
        {
            ValidateTaskDirectory(artifactsRoot, outputDirectory, directoryName);
            await DeleteDirectoryBoundedAsync(outputDirectory).ConfigureAwait(false);
        }
    }

    private static Task RejectsInconsistentMergeAsync()
    {
        var method = new ClrLirMethod(
            "BadMerge",
            ClrLirType.Void,
            [],
            [],
            [
                new ClrLirBlock("entry", [
                    new ClrLirLoadBoolean(true),
                    new ClrLirBranchTrue("right"),
                ]),
                new ClrLirBlock("left", [
                    new ClrLirLoadInt32(1),
                    new ClrLirBranch("join"),
                ]),
                new ClrLirBlock("right", [new ClrLirBranch("join")]),
                new ClrLirBlock("join", [new ClrLirReturn()]),
            ]);

        ClrLirValidationResult result = method.Validate();
        AssertEx.False(result.IsValid, "Different incoming stack shapes must fail validation.");
        AssertEx.Equal("LIR011", result.Diagnostics[0].Code);
        return Task.CompletedTask;
    }

    private static Task RejectsUnreachableBlockAsync()
    {
        var method = new ClrLirMethod(
            "DeadBlock",
            ClrLirType.Void,
            [],
            [],
            [
                new ClrLirBlock("entry", [new ClrLirReturn()]),
                new ClrLirBlock("dead", [new ClrLirLoadInt32(1), new ClrLirReturn()]),
            ]);

        ClrLirValidationResult result = method.Validate();
        AssertEx.False(result.IsValid, "Unreachable blocks must be rejected before PE emission.");
        AssertEx.Equal("LIR014", result.Diagnostics[0].Code);
        return Task.CompletedTask;
    }

    private static string GetRepositoryRoot()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        AssertEx.True(Directory.Exists(Path.Combine(root, "tests", "RustSharp.Tests")), "The repository root must be validated.");
        return root;
    }

    private static void ValidateTaskDirectory(string artifactsRoot, string target, string expectedName)
    {
        string root = Path.GetFullPath(artifactsRoot);
        string resolvedTarget = Path.GetFullPath(target);
        string expectedTarget = Path.Combine(root, expectedName);
        AssertEx.Equal(expectedTarget, resolvedTarget, "The test must use its own direct artifact directory.");
        string relative = Path.GetRelativePath(root, resolvedTarget);
        AssertEx.False(Path.IsPathRooted(relative), "The test directory must remain below artifacts/tests.");
        AssertEx.False(relative.StartsWith("..", StringComparison.Ordinal), "The test directory must remain below artifacts/tests.");
        AssertEx.False(relative.Contains(Path.DirectorySeparatorChar) || relative.Contains(Path.AltDirectorySeparatorChar), "The test directory must be a direct child.");
    }

    private static async Task DeleteDirectoryBoundedAsync(string directory)
    {
        var clock = Stopwatch.StartNew();
        Exception? last = null;
        for (var attempt = 0; attempt < 40 && clock.Elapsed < TimeSpan.FromSeconds(5); attempt++)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException exception)
            {
                last = exception;
            }
            catch (UnauthorizedAccessException exception)
            {
                last = exception;
            }

            if (!Directory.Exists(directory))
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"Could not clean LIR PE test directory '{directory}'.", last);
    }
}
