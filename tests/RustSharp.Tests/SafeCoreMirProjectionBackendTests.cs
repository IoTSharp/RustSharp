using System.Reflection;
using System.Runtime.Loader;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreMirProjectionBackendTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("MIR CLR nested named tuple array places retain owner storage", NestedPlacesAsync),
        new("MIR CLR reference calls returns and CFG joins retain runtime owner", ReferenceCallAsync),
        new("MIR CLR dynamic array bounds trap instead of aliasing another field", BoundsAsync),
        new("MIR CLR rejects forged projected operands at its public entry", ForgedPlaceAsync),
        new("CLR LIR rejects immutable indirect stores and malformed referent types", InvalidAddressesAsync),
        new("MIR CLR public lowering rejects a reference to dead callee storage", DeadReturnAsync),
        new("MIR CLR rejects reference-bearing aggregates before assembly emission", ReferenceAggregateAsync),
        new("MIR source zero-array indexing emits a private bounds helper with valid metadata", SourceZeroArrayAsync),
        new("MIR CLR managed references load and store complete aggregate values", AggregateReferenceAsync),
        new("MIR CLR legacy shared slice reborrows preserve their fixed-array layout", SliceReborrowAsync),
        new("MIR CLR executes the declared CFG entry before reading a reference", NonzeroEntryAsync),
    ];

    private static readonly SafeCoreMirSource Source = new("projection-backend.rs", new TextSpan(0, 1), 0, 1);
    private static readonly SafeCoreType Integer = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.I32);
    private static readonly SafeCoreType Usize = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Usize);
    private static readonly SafeCoreType Boolean = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Bool);
    private static readonly SafeCoreType Mutable = SafeCoreType.Reference(Integer, true);

    private static SafeCoreMirLocal Local(int id, SafeCoreType type, bool mutable = false,
        SafeCoreMirLocalKind kind = SafeCoreMirLocalKind.User) => new(id, "value" + id, type, kind, mutable, Source);
    private static SafeCoreMirOperand Value(int id, SafeCoreType type) => SafeCoreMirOperand.Local(id, type, Source);
    private static SafeCoreMirOperand Int(int value) => SafeCoreMirOperand.Constant(Integer,
        value.ToString(System.Globalization.CultureInfo.InvariantCulture), Source);
    private static SafeCoreMirStatement Assign(int id, SafeCoreMirRvalue value) => new(id, value, Source);
    private static SafeCoreMirStatement Use(int id, SafeCoreMirOperand value) => Assign(id, SafeCoreMirRvalue.Use(value, Source));
    private static SafeCoreMirOperand Place(int id, SafeCoreType type, params SafeCoreMirProjection[] projections) =>
        SafeCoreMirOperand.PlaceValue(new(id, projections), type, Source);

    private static SafeCoreMirProgram NestedProgram(int index)
    {
        SafeCoreType array = SafeCoreType.Array(Integer, 2);
        SafeCoreType tuple = SafeCoreType.Tuple([Boolean, array]);
        SafeCoreType adt = SafeCoreType.Adt("crate::Container");
        SafeCoreMirOperand leaf = Place(2, Integer, SafeCoreMirProjection.Field("payload"),
            SafeCoreMirProjection.TupleIndex(1), SafeCoreMirProjection.DynamicIndex(3));
        SafeCoreMirPlace write = new(4, [SafeCoreMirProjection.Dereference()]);
        return new([
            new(0, "crate::main", Integer,
                [Local(0, array), Local(1, tuple), Local(2, adt, true), Local(3, Usize), Local(4, Mutable)],
                [new(0, [
                    Assign(0, SafeCoreMirRvalue.Array([Int(3), Int(7)], array, Source)),
                    Assign(1, SafeCoreMirRvalue.Tuple([SafeCoreMirOperand.Constant(Boolean, "true", Source), Value(0, array)], tuple, Source)),
                    Assign(2, SafeCoreMirRvalue.Adt([Value(1, tuple)], adt, Source)),
                    Use(3, SafeCoreMirOperand.Constant(Usize, index.ToString(System.Globalization.CultureInfo.InvariantCulture), Source)),
                    Assign(4, SafeCoreMirRvalue.Unary("&mut", leaf, Mutable, Source)),
                    Use(4, Int(41)) with { DestinationPlace = write },
                ], SafeCoreMirTerminator.Return(Place(2, Integer, SafeCoreMirProjection.Field("payload"),
                    SafeCoreMirProjection.TupleIndex(1), SafeCoreMirProjection.ArrayIndex(1)), Source), Source)], 0, Source),
        ], [new(adt, [new("payload", tuple, Source)], Source)]);
    }

    private static Task NestedPlacesAsync()
    {
        AssertEx.Equal(41, Run(NestedProgram(1)));
        return Task.CompletedTask;
    }

    private static Task AggregateReferenceAsync()
    {
        SafeCoreType tuple = SafeCoreType.Tuple([Integer, Boolean]);
        SafeCoreType reference = SafeCoreType.Reference(tuple, true);
        SafeCoreMirProgram program = new([new(0, "main", Integer,
            [Local(0, tuple, true), Local(1, tuple), Local(2, reference), Local(3, tuple)],
            [new(0, [Assign(0, SafeCoreMirRvalue.Tuple([Int(1), SafeCoreMirOperand.Constant(Boolean, "false", Source)], tuple, Source)),
                Assign(1, SafeCoreMirRvalue.Tuple([Int(29), SafeCoreMirOperand.Constant(Boolean, "true", Source)], tuple, Source)),
                Assign(2, SafeCoreMirRvalue.Unary("&mut", Value(0, tuple), reference, Source)),
                Use(2, Value(1, tuple)) with { DestinationPlace = new(2, [SafeCoreMirProjection.Dereference()]) },
                Assign(3, SafeCoreMirRvalue.Unary("*", Value(2, reference), tuple, Source))],
                SafeCoreMirTerminator.Return(Place(3, Integer, SafeCoreMirProjection.TupleIndex(0)), Source), Source)], 0, Source)]);
        AssertEx.Equal(29, Run(program));
        return Task.CompletedTask;
    }

    private static Task BoundsAsync()
    {
        bool trapped = false;
        try { _ = Run(NestedProgram(2)); }
        catch (TargetInvocationException exception) when (exception.InnerException is IndexOutOfRangeException) { trapped = true; }
        AssertEx.True(trapped, "Dynamic indexing past the fixed array must raise a bounds exception.");
        SafeCoreType empty = SafeCoreType.Array(Integer, 0);
        SafeCoreMirProgram zero = new([new(0, "main", Integer, [Local(0, empty), Local(1, Usize)],
            [new(0, [Assign(0, SafeCoreMirRvalue.Array([], empty, Source)),
                Use(1, SafeCoreMirOperand.Constant(Usize, "0", Source))],
                SafeCoreMirTerminator.Return(Place(0, Integer, SafeCoreMirProjection.DynamicIndex(1)), Source), Source)], 0, Source)]);
        trapped = false;
        try { _ = Run(zero); }
        catch (TargetInvocationException exception) when (exception.InnerException is IndexOutOfRangeException) { trapped = true; }
        AssertEx.True(trapped, "Every dynamic index into a zero-length array must trap at runtime.");
        return Task.CompletedTask;
    }

    private static Task ReferenceCallAsync()
    {
        SafeCoreType signature = SafeCoreType.Function([Boolean, Mutable, Mutable], Mutable, "crate::choose");
        SafeCoreMirFunction main = new(0, "crate::main", Integer,
            [Local(0, Integer, true), Local(1, Integer, true), Local(2, Mutable), Local(3, Mutable), Local(4, Mutable)],
            [new(0, [Use(0, Int(3)), Use(1, Int(7)),
                Assign(2, SafeCoreMirRvalue.Unary("&mut", Value(0, Integer), Mutable, Source)),
                Assign(3, SafeCoreMirRvalue.Unary("&mut", Value(1, Integer), Mutable, Source))],
                SafeCoreMirTerminator.Call(SafeCoreMirOperand.Function(1, signature, Source),
                    [SafeCoreMirOperand.Constant(Boolean, "false", Source), Value(2, Mutable), Value(3, Mutable)], 4, 1, Source), Source),
             new(1, [Use(4, Int(23)) with { DestinationPlace = new(4, [SafeCoreMirProjection.Dereference()]) }],
                SafeCoreMirTerminator.Return(Value(1, Integer), Source), Source)], 0, Source);
        SafeCoreMirFunction choose = new(1, "crate::choose", Mutable,
            [Local(0, Boolean, kind: SafeCoreMirLocalKind.Parameter), Local(1, Mutable, kind: SafeCoreMirLocalKind.Parameter),
             Local(2, Mutable, kind: SafeCoreMirLocalKind.Parameter), Local(3, Mutable)],
            [new(0, [], SafeCoreMirTerminator.Branch(Value(0, Boolean), 1, 2, Source), Source),
             new(1, [Use(3, Value(1, Mutable))], SafeCoreMirTerminator.Goto(3, Source), Source),
             new(2, [Use(3, Value(2, Mutable))], SafeCoreMirTerminator.Goto(3, Source), Source),
             new(3, [], SafeCoreMirTerminator.Return(Value(3, Mutable), Source), Source)], 0, Source);
        AssertEx.Equal(23, Run(new([main, choose])));
        return Task.CompletedTask;
    }

    private static Task ForgedPlaceAsync()
    {
        SafeCoreType tuple = SafeCoreType.Tuple([Integer]);
        SafeCoreMirProgram program = new([new(0, "main", Boolean, [Local(0, tuple)],
            [new(0, [], SafeCoreMirTerminator.Return(Place(0, Boolean, SafeCoreMirProjection.TupleIndex(0)), Source), Source)], 0, Source)]);
        SafeCoreClrResult result = SafeCoreMirClrLowering.Lower(program);
        AssertEx.False(result.IsSuccessful, "The public backend must validate hand-authored MIR before emission.");
        AssertEx.True(result.Diagnostics.Any(static diagnostic => diagnostic.Code == SafeCoreMirDiagnosticCodes.TypeMismatch),
            "A forged projected result type must retain the MIR type diagnostic.");
        return Task.CompletedTask;
    }

    private static Task InvalidAddressesAsync()
    {
        AssertEx.Throws<ArgumentNullException>(() => _ = new ClrLirFieldAddress(null!, 0));
        var sharedStore = new ClrLirMethod("Main", ClrLirType.Void, [], [new("x", ClrLirType.I32)],
            [new("entry", [new ClrLirLoadLocalAddress(0), new ClrLirLoadInt32(1),
                new ClrLirStoreIndirect(ClrLirType.I32), new ClrLirReturn()])]);
        AssertEx.False(sharedStore.Validate().IsValid, "A shared pointer must never satisfy a mutable indirect store.");
        var nested = new ClrLirMethod("Main", ClrLirType.Void, [], [],
            [new("entry", [new ClrLirLoadIndirect(ClrLirType.ByReference(ClrLirType.I32)), new ClrLirReturn()])]);
        AssertEx.False(nested.Validate().IsValid, "Malformed nested indirect references must be diagnostics, not exceptions.");
        return Task.CompletedTask;
    }

    private static Task SliceReborrowAsync()
    {
        SafeCoreType array = SafeCoreType.Array(Integer, 2);
        SafeCoreType slice = SafeCoreType.Slice(Integer);
        SafeCoreType parent = SafeCoreType.Reference(slice, true);
        SafeCoreType child = SafeCoreType.Reference(slice, false);
        SafeCoreMirProgram program = new([new(0, "main", Integer,
            [Local(0, array, true), Local(1, parent), Local(2, child), Local(3, Usize), Local(4, Integer)],
            [new(0, [Assign(0, SafeCoreMirRvalue.Array([Int(4), Int(9)], array, Source)),
                Assign(1, SafeCoreMirRvalue.Unary("&mut", Value(0, array), parent, Source)),
                Assign(2, SafeCoreMirRvalue.Unary("reborrow", Value(1, parent), child, Source)),
                Use(3, SafeCoreMirOperand.Constant(Usize, "1", Source)),
                Assign(4, SafeCoreMirRvalue.Index(Value(2, child), Value(3, Usize), Integer, Source))],
                SafeCoreMirTerminator.Return(Value(4, Integer), Source), Source)], 0, Source)]);
        AssertEx.Equal(9, Run(program));
        return Task.CompletedTask;
    }

    private static Task NonzeroEntryAsync()
    {
        SafeCoreType reference = SafeCoreType.Reference(Integer, false);
        SafeCoreMirProgram program = new([new(0, "main", Integer, [Local(0, Integer), Local(1, reference)],
            [new(0, [], SafeCoreMirTerminator.Return(Place(1, Integer, SafeCoreMirProjection.Dereference()), Source), Source),
             new(1, [Use(0, Int(53)), Assign(1, SafeCoreMirRvalue.Unary("&", Value(0, Integer), reference, Source))],
                SafeCoreMirTerminator.Goto(0, Source), Source)], 1, Source)]);
        AssertEx.Equal(53, Run(program));
        return Task.CompletedTask;
    }

    private static Task DeadReturnAsync()
    {
        SafeCoreMirFunction main = new(0, "main", Integer, [],
            [new(0, [], SafeCoreMirTerminator.Return(Int(0), Source), Source)], 0, Source);
        SafeCoreMirFunction bad = new(1, "bad", Mutable, [Local(0, Integer, true), Local(1, Mutable)],
            [new(0, [Use(0, Int(7)), Assign(1, SafeCoreMirRvalue.Unary("&mut", Value(0, Integer), Mutable, Source))],
                SafeCoreMirTerminator.Return(Value(1, Mutable), Source), Source)], 0, Source);
        SafeCoreClrResult result = SafeCoreMirClrLowering.Lower(new([main, bad]));
        AssertEx.False(result.IsSuccessful, "A managed pointer to callee storage must never escape public lowering.");
        AssertEx.True(result.Diagnostics.Any(static diagnostic => diagnostic.Code == SafeCoreMirReferenceProvenance.EscapingReference),
            "The backend must expose the checked provenance escape diagnostic.");
        return Task.CompletedTask;
    }

    private static Task ReferenceAggregateAsync()
    {
        SafeCoreType tuple = SafeCoreType.Tuple([Mutable]);
        SafeCoreMirProgram program = new([new(0, "main", Integer, [Local(0, tuple)],
            [new(0, [], SafeCoreMirTerminator.Return(Int(0), Source), Source)], 0, Source)]);
        SafeCoreClrResult result = SafeCoreMirClrLowering.Lower(program);
        AssertEx.False(result.IsSuccessful, "An ordinary CLR struct must never contain a managed reference field.");
        AssertEx.True(result.Diagnostics.Any(static diagnostic => diagnostic.Code == SafeCoreMirClrLowering.Unsupported ||
            diagnostic.Code == SafeCoreMirReferenceProvenance.InvalidOrigin &&
            diagnostic.Message.Contains("nested lifetime contract", StringComparison.Ordinal)),
            "The byref-like aggregate boundary must be explicit before emitting metadata.");
        return Task.CompletedTask;
    }

    private static Task SourceZeroArrayAsync()
    {
        const string source = "fn main() { let values: [i32; 0] = []; let index: usize = 0; println!(\"{}\", values[index]); }";
        string root = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string directory = Path.Combine(root, "mir-zero-array-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            string output = Path.Combine(directory, "ZeroArray.dll");
            CompilationResult compilation = CompilerDriver.Compile(source, "zero-array.rs", output,
                assemblyName: "ZeroArray", profile: CompilationProfile.SafeCoreMirV2, cancellationToken: deadline.Token);
            AssertEx.True(compilation.Success, string.Join("; ", compilation.Diagnostics.Select(static item => item.Code + ": " + item.Message)));
            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(output);
            AssertEx.True(imported.IsSuccessful, string.Join("; ", imported.Diagnostics));
            RustSharpMetadataFunction helper = imported.Document!.Functions.Single(static method => method.Name.StartsWith("bounds_failure_", StringComparison.Ordinal));
            AssertEx.False(helper.IsPublic, "Bounds helpers cannot become source exports.");
            bool trapped = false;
            try { _ = Invoke(File.ReadAllBytes(output)); }
            catch (TargetInvocationException exception) when (exception.InnerException is IndexOutOfRangeException) { trapped = true; }
            AssertEx.True(trapped, "Zero-array indexing must reach its runtime bounds failure.");
        }
        finally
        {
            AssertEx.True(Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "Cleanup is restricted to this test's owned directory.");
            Directory.Delete(directory, recursive: true);
        }
        return Task.CompletedTask;
    }

    private static int Run(SafeCoreMirProgram mir)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        SafeCoreClrResult lir = SafeCoreMirClrLowering.Lower(mir, deadline.Token);
        AssertEx.True(lir.IsSuccessful, string.Join("; ", lir.Diagnostics.Select(static item => item.Code + ": " + item.Message)));
        GeneratedAssembly assembly = ClrLirAssemblyEmitter.EmitProgram(lir, "ProjectionBackend", "x", Source.SourcePath,
            "ProjectionBackend.pdb", cancellationToken: deadline.Token);
        return (int)Invoke(assembly.PeImage)!;
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The untrimmed test harness loads a freshly generated, complete PE to verify executable IL.")]
    private static object? Invoke(byte[] image)
    {
        var context = new AssemblyLoadContext("projection-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            using var bytes = new MemoryStream(image, writable: false);
            Assembly loaded = context.LoadFromStream(bytes);
            return loaded.EntryPoint!.Invoke(null, []);
        }
        finally { context.Unload(); }
    }
}
