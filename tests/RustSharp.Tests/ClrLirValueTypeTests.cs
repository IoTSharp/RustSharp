using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class ClrLirValueTypeTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("CLR value types execute nested construction copying calls and field reads", ExecutesValuesAsync),
        new("CLR value layouts preserve deterministic closed metadata and declaration order", DeterministicLayoutsAsync),
        new("CLR value instructions reject incorrect stack types and field indices", InvalidInstructionsAsync),
        new("CLR value emission rejects unknown recursive and conflicting layouts and calls", InvalidLayoutsAsync),
        new("CLR value validation and emission enforce finite budgets and cancellation", BoundedValuesAsync),
        new("CLR generic metadata persists as a deterministic bounded manifest resource", GenericResourceAsync),
    ];

    private static readonly ClrLirValueType Unit = new("Unit", []);
    private static readonly ClrLirValueType Pair = new("Pair", [new("number", ClrLirType.I32), new("flag", ClrLirType.Bool)]);
    private static readonly ClrLirValueType Nested = new("Nested", [new("pair", Pair.Type), new("unit", Unit.Type)]);

    private static SafeCoreClrResult Program()
    {
        var make = new ClrLirMethod("Make", Nested.Type, [], [], [new("entry", [
            new ClrLirLoadInt32(7), new ClrLirLoadBoolean(true), new ClrLirConstructValue(Pair),
            new ClrLirConstructValue(Unit), new ClrLirConstructValue(Nested), new ClrLirReturn(),
        ])]);
        var echo = new ClrLirMethod("Echo", Nested.Type, [Nested.Type], [], [new("entry", [
            new ClrLirLoadArgument(0), new ClrLirReturn(),
        ])]);
        var printNumber = new ClrLirCallSite("Console.WriteLine", ClrLirType.Void, [ClrLirType.I32]);
        var main = new ClrLirMethod("Main", ClrLirType.I32, [], [new("original", Nested.Type), new("copy", Nested.Type)],
            [new("entry", [
                new ClrLirCall(new("Make", Nested.Type, [])), new ClrLirStoreLocal(0),
                new ClrLirLoadLocal(0), new ClrLirCall(new("Echo", Nested.Type, [Nested.Type])), new ClrLirStoreLocal(1),
                new ClrLirLoadInt32(42), new ClrLirLoadBoolean(false), new ClrLirConstructValue(Pair),
                new ClrLirConstructValue(Unit), new ClrLirConstructValue(Nested), new ClrLirStoreLocal(0),
                new ClrLirLoadLocal(1), new ClrLirReadField(Nested, 0), new ClrLirReadField(Pair, 0), new ClrLirCall(printNumber),
                new ClrLirLoadLocal(0), new ClrLirReadField(Nested, 0), new ClrLirReadField(Pair, 0), new ClrLirCall(printNumber),
                new ClrLirLoadLocal(1), new ClrLirReadField(Nested, 0), new ClrLirReadField(Pair, 1),
                new ClrLirCall(new("Console.WriteLine", ClrLirType.Void, [ClrLirType.Bool])),
                new ClrLirLoadLocal(1), new ClrLirReadField(Nested, 1), new ClrLirDiscard(Unit.Type),
                new ClrLirLoadInt32(0), new ClrLirReturn(),
            ])]);
        return new([main, make, echo], [new(0, 12), new(0, 12), new(0, 12)], [])
        { ValueTypes = [Unit, Pair, Nested] };
    }

    private static GeneratedAssembly Emit(SafeCoreClrResult program) =>
        ClrLirAssemblyEmitter.EmitProgram(program, "ClrLir.Values", "fn main() {}", "values.rs", "ClrLir.Values.pdb");

    private static async Task ExecutesValuesAsync()
    {
        GeneratedAssembly assembly = Emit(Program());
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "tests"));
        string name = "clr-values-" + Guid.NewGuid().ToString("N");
        string directory = Path.Combine(root, name);
        AssertEx.Equal(name, Path.GetRelativePath(root, directory), "The test must own a direct artifact directory.");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "ClrLir.Values.dll");
            await File.WriteAllBytesAsync(path, assembly.PeImage).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.ChangeExtension(path, ".runtimeconfig.json"), assembly.RuntimeConfigJson).ConfigureAwait(false);
            var result = await new BoundedProcessRunner(TimeSpan.FromSeconds(2)).RunAsync(
                new("dotnet", [path], directory, TimeSpan.FromSeconds(30))).ConfigureAwait(false);
            AssertEx.Equal(BoundedProcessTermination.Exited, result.Termination);
            AssertEx.Equal(0, result.ExitCode!.Value, result.StandardError);
            AssertEx.Equal(string.Join(Environment.NewLine, "7", "42", "True", ""), result.StandardOutput);
            AssertEx.False(result.OutputTruncated || result.ProcessTreeCleanupIncomplete, "Aggregate execution must remain bounded.");
        }
        finally
        {
            AssertEx.Equal(name, Path.GetRelativePath(root, Path.GetFullPath(directory)));
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Task DeterministicLayoutsAsync()
    {
        SafeCoreClrResult program = Program();
        GeneratedAssembly first = Emit(program);
        GeneratedAssembly second = Emit(program with { ValueTypes = [Nested, Unit, Pair] });
        AssertEx.True(first.PeImage.AsSpan().SequenceEqual(second.PeImage), "Layout registration order must not alter PE output.");
        AssertEx.True(first.PdbImage!.AsSpan().SequenceEqual(second.PdbImage), "Layout registration order must not alter PDB output.");
        using var stream = new MemoryStream(first.PeImage);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        AssertEx.Equal(0, metadata.GetTableRowCount(TableIndex.GenericParam), "All emitted aggregates and methods must be closed.");
        AssertEx.Equal(0, metadata.GetTableRowCount(TableIndex.MethodSpec), "Specialized calls must use direct method definitions.");
        string[] values = ["Nested", "Pair", "Unit"];
        string[][] expectedFields = [["pair", "unit"], ["number", "flag"], []];
        for (int index = 0; index < values.Length; index++)
        {
            TypeDefinition definition = metadata.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(index + 3));
            AssertEx.Equal(values[index], metadata.GetString(definition.Name));
            AssertEx.True((definition.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.SequentialLayout,
                "Closed aggregates must retain sequential value layout.");
            AssertEx.Equal("ValueType", metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)definition.BaseType).Name));
            AssertEx.Equal(string.Join(',', expectedFields[index]), string.Join(',', definition.GetFields().Select(field =>
                metadata.GetString(metadata.GetFieldDefinition(field).Name))));
            MethodDefinition constructor = metadata.GetMethodDefinition(definition.GetMethods().Single());
            AssertEx.Equal(".ctor", metadata.GetString(constructor.Name));
            AssertEx.False(constructor.Attributes.HasFlag(MethodAttributes.Static), "Value constructors must be instance methods.");
        }
        using var pdb = MetadataReaderProvider.FromPortablePdbImage([.. first.PdbImage!]);
        AssertEx.Equal(metadata.MethodDefinitions.Count, pdb.GetMetadataReader().MethodDebugInformation.Count);
        return Task.CompletedTask;
    }

    private static Task InvalidInstructionsAsync()
    {
        ClrLirMethod wrongFields = Method(ClrLirType.Void, [
            new ClrLirLoadBoolean(true), new ClrLirLoadInt32(1), new ClrLirConstructValue(Pair),
            new ClrLirDiscard(Pair.Type), new ClrLirReturn(),
        ]);
        AssertEx.True(wrongFields.Validate().Diagnostics.Any(static diagnostic => diagnostic.Code == "LIR009"),
            "Construction must preserve field declaration order and type checks.");
        ClrLirMethod badIndex = Method(ClrLirType.Void, [new ClrLirConstructValue(Unit), new ClrLirReadField(Unit, 0), new ClrLirReturn()]);
        AssertEx.True(badIndex.Validate().Diagnostics.Any(static diagnostic => diagnostic.Code == "LIR018"), "Out-of-range field reads must be diagnosed.");
        ClrLirMethod wrongReceiver = Method(ClrLirType.I32, [new ClrLirConstructValue(Unit), new ClrLirReadField(Pair, 0), new ClrLirReturn()]);
        AssertEx.False(wrongReceiver.Validate().IsValid, "Field receivers must preserve nominal type identity.");
        AssertEx.Throws<InvalidOperationException>(() => ClrLirAssemblyEmitter.Emit(wrongFields, "Invalid", [Pair]));
        return Task.CompletedTask;
    }

    private static Task InvalidLayoutsAsync()
    {
        ClrLirMethod empty = Method(ClrLirType.Void, [new ClrLirReturn()]);
        ClrLirValueType unknown = new("Unknown", [new("value", ClrLirType.Value("Missing"))]);
        ClrLirValueType recursive = new("Recursive", [new("value", ClrLirType.Value("Recursive"))]);
        ClrLirValueType left = new("Left", [new("right", ClrLirType.Value("Right"))]);
        ClrLirValueType right = new("Right", [new("left", left.Type)]);
        ClrLirValueType invalid = new("Invalid", [new("void", ClrLirType.Void)]);
        ClrLirValueType duplicate = new("Duplicate", [new("x", ClrLirType.I32), new("x", ClrLirType.Bool)]);
        ClrLirValueType[][] rejected = [[unknown], [recursive], [left, right], [invalid], [duplicate], [Unit, Unit]];
        foreach (ClrLirValueType[] layouts in rejected)
            AssertEx.Throws<InvalidOperationException>(() => ClrLirAssemblyEmitter.Emit(empty, "Invalid", layouts));

        ClrLirValueType changedUnit = new("Unit", [new("number", ClrLirType.I32)]);
        ClrLirMethod conflict = Method(ClrLirType.Void, [new ClrLirLoadInt32(1), new ClrLirConstructValue(changedUnit),
            new ClrLirDiscard(Unit.Type), new ClrLirReturn()]);
        AssertEx.Throws<InvalidOperationException>(() => ClrLirAssemblyEmitter.Emit(conflict, "Invalid", [Unit]));
        ClrLirValueType mirUnit = Unit with { ImplementsMirValue = true };
        ClrLirMethod mirConstruction = Method(ClrLirType.Void, [new ClrLirConstructValue(mirUnit),
            new ClrLirDiscard(Unit.Type), new ClrLirReturn()]);
        AssertEx.Throws<InvalidOperationException>(() => ClrLirAssemblyEmitter.Emit(mirConstruction, "Invalid", [Unit]));
        ClrLirMethod plainConstruction = Method(ClrLirType.Void, [new ClrLirConstructValue(Unit),
            new ClrLirDiscard(Unit.Type), new ClrLirReturn()]);
        AssertEx.Throws<InvalidOperationException>(() => ClrLirAssemblyEmitter.Emit(plainConstruction, "Invalid", [mirUnit]));
        ClrLirMethod unknownLocal = new("Main", ClrLirType.Void, [], [new("unknown", ClrLirType.Value("Missing"))], [new("entry", [new ClrLirReturn()])]);
        AssertEx.Throws<InvalidOperationException>(() => ClrLirAssemblyEmitter.Emit(unknownLocal, "Invalid"));

        ClrLirMethod wrongCall = Method(ClrLirType.I32, [new ClrLirConstructValue(Unit),
            new ClrLirCall(new("Echo", ClrLirType.I32, [Unit.Type])), new ClrLirReturn()]);
        ClrLirMethod echo = new("Echo", Unit.Type, [Unit.Type], [], [new("entry", [new ClrLirLoadArgument(0), new ClrLirReturn()])]);
        var program = new SafeCoreClrResult([wrongCall, echo], [new(0, 12), new(0, 12)], []) { ValueTypes = [Unit] };
        AssertEx.Throws<InvalidOperationException>(() => Emit(program));
        ClrLirMethod invalidConsole = Method(ClrLirType.Void, [new ClrLirConstructValue(Unit),
            new ClrLirCall(new("Console.WriteLine", ClrLirType.Void, [Unit.Type])), new ClrLirReturn()]);
        AssertEx.Throws<NotSupportedException>(() => ClrLirAssemblyEmitter.Emit(invalidConsole, "Invalid", [Unit]));
        return Task.CompletedTask;
    }

    private static Task BoundedValuesAsync()
    {
        ClrLirMethod empty = Method(ClrLirType.Void, [new ClrLirReturn()]);
        AssertEx.Throws<ArgumentException>(() => _ = new ClrLirValueType("TooMany", Enumerable.Range(0, 257).Select(index => new ClrLirField("f" + index, ClrLirType.I32))));
        AssertEx.Throws<ArgumentException>(() => ClrLirAssemblyEmitter.Emit(empty, "Invalid", Enumerable.Range(0, 4097).Select(index => new ClrLirValueType("T" + index, []))));
        var growing = new List<ClrLirValueType> { Unit };
        for (int index = 0; index < 22; index++)
        {
            ClrLirType previous = growing[^1].Type;
            growing.Add(new("Growing" + index, [new("left", previous), new("right", previous)]));
        }
        AssertEx.Throws<InvalidOperationException>(() => ClrLirAssemblyEmitter.Emit(empty, "Invalid", growing));
        var deep = new List<ClrLirValueType> { new("T000", []) };
        for (int index = 1; index <= 129; index++)
            deep.Add(new("T" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), [new("nested", deep[^1].Type)]));
        AssertEx.Throws<InvalidOperationException>(() => ClrLirAssemblyEmitter.Emit(empty, "Invalid", deep));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => empty.Validate(cancelled.Token));
        AssertEx.Throws<OperationCanceledException>(() => ClrLirAssemblyEmitter.Emit(empty, "Cancelled", [Unit], cancelled.Token));
        return Task.CompletedTask;
    }

    private static ClrLirMethod Method(ClrLirType result, IEnumerable<ClrLirInstruction> instructions) =>
        new("Main", result, [], [], [new("entry", instructions)]);

    private static Task GenericResourceAsync()
    {
        byte[] payload = "{\"version\":1,\"definitions\":[]}"u8.ToArray();
        SafeCoreClrResult program = Program() with { GenericMetadata = payload };
        GeneratedAssembly first = Emit(program);
        GeneratedAssembly second = Emit(program);
        AssertEx.True(first.PeImage.AsSpan().SequenceEqual(second.PeImage), "Generic resources must preserve deterministic PE output.");
        using var stream = new MemoryStream(first.PeImage);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        ManifestResource resource = metadata.GetManifestResource(metadata.ManifestResources.Single());
        AssertEx.Equal("RustSharp.Generics.v1.json", metadata.GetString(resource.Name));
        AssertEx.True(resource.Implementation.IsNil, "Generic metadata must be embedded directly in its assembly.");
        CorHeader cor = AssertEx.NotNull(pe.PEHeaders.CorHeader, "The generated image must contain a CLR header.");
        BlobReader blob = pe.GetSectionData(cor.ResourcesDirectory.RelativeVirtualAddress).GetReader();
        blob.Offset = checked((int)resource.Offset);
        int length = blob.ReadInt32();
        AssertEx.True(payload.AsSpan().SequenceEqual(blob.ReadBytes(length)), "The embedded bytes must round-trip exactly.");
        Guid firstId = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        GeneratedAssembly changed = Emit(program with { GenericMetadata = "{\"version\":2}"u8.ToArray() });
        using var changedStream = new MemoryStream(changed.PeImage);
        using var changedPe = new PEReader(changedStream);
        MetadataReader changedMetadata = changedPe.GetMetadataReader();
        AssertEx.False(firstId == changedMetadata.GetGuid(changedMetadata.GetModuleDefinition().Mvid),
            "Generic metadata contents must contribute to the deterministic module identity.");
        AssertEx.Throws<ArgumentException>(() => Emit(program with { GenericMetadata = new byte[8 * 1024 * 1024 + 1] }));
        return Task.CompletedTask;
    }
}
