using RustSharp.Compiler;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreMirReferenceProvenanceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("MIR provenance returns checked input field origins across calls", FieldCallAsync),
        new("MIR provenance unions distinct incoming reference origins", BranchOriginsAsync),
        new("MIR provenance rejects missing initialization on an incoming edge", MissingOriginAsync),
        new("MIR provenance rejects local storage returned as a reference", LocalEscapeAsync),
        new("MIR provenance rejects fabricated reference constants", ForgedOriginAsync),
        new("MIR provenance enforces operation and path budgets", BudgetAsync),
        new("MIR provenance cancellation is observed before work", CancellationAsync),
        new("MIR ownership keeps both possible returned-call referents borrowed", CallLoanUnionAsync),
        new("MIR provenance rejects invalid public input without throwing", InvalidArenaAsync),
        new("MIR provenance checks a reference used only by a projected write", WriteAfterScopeAsync),
        new("MIR explicit ownership evidence cannot omit reference effects", OmittedEvidenceAsync),
        new("MIR NLL ends a reference value before its replacement", ReferenceReplacementAsync),
        new("MIR ownership permits disjoint mutable projections of a parent reference", DisjointReborrowAsync),
        new("MIR ownership checks runtime projection index initialization", IndexInitializationAsync),
        new("MIR ownership restores moved fields and clears replaced child paths", ReinitializedFieldsAsync),
        new("MIR call arguments reject repeated exclusive references", CallArgumentAliasAsync),
        new("MIR source calls reject exclusive aliases and allow disjoint consecutive calls", SourceCallAliasesAsync),
        new("MIR reference coercion creates a shared reborrow with the right provenance", SharedCoercionAsync),
    ];

    private static readonly SafeCoreMirSource Source = new("provenance.rs", new TextSpan(0, 1), 0, 1);
    private static readonly SafeCoreType Integer = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.I32);
    private static readonly SafeCoreType Boolean = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Bool);
    private static readonly SafeCoreType Reference = SafeCoreType.Reference(Integer, false);

    private static Task FieldCallAsync()
    {
        SafeCoreType tuple = SafeCoreType.Tuple([Integer, Integer]);
        SafeCoreType tupleReference = SafeCoreType.Reference(tuple, false);
        var field = SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.Dereference()).Append(SafeCoreMirProjection.TupleIndex(1));
        SafeCoreMirFunction project = new(0, "project", Reference,
            [Local(0, tupleReference, true), Local(1, Reference)],
            [Block(0, [new(1, SafeCoreMirRvalue.Unary("&", SafeCoreMirOperand.PlaceValue(field, Integer, Source), Reference, Source), Source)],
                SafeCoreMirTerminator.Return(Operand(1, Reference), Source))], 0, Source);
        SafeCoreMirFunction relay = new(1, "relay", Reference,
            [Local(0, tupleReference, true), Local(1, Reference)],
            [Block(0, [], SafeCoreMirTerminator.Call(SafeCoreMirOperand.Function(0, SafeCoreType.Function([tupleReference], Reference, "project"), Source),
                [Operand(0, tupleReference)], 1, 1, Source)),
             Block(1, [], SafeCoreMirTerminator.Return(Operand(1, Reference), Source))], 0, Source);
        SafeCoreMirProgram program = new([project, relay]);
        SafeCoreMirReferenceProvenanceResult result = SafeCoreMirReferenceProvenance.Analyze(program);
        AssertEx.True(result.IsSuccessful, Format(result.Diagnostics));
        SafeCoreMirReferenceOrigin origin = result.Functions[1].ReturnOrigins.Single();
        AssertEx.True(origin.IsParameter && origin.LocalId == 0 && !origin.IsMutable, "The call must retain its input reference contract.");
        AssertEx.Equal(SafeCoreMirProjection.TupleIndex(1), origin.Projections.Single());
        SafeCoreMirOwnershipResult ownership = SafeCoreMirOwnershipAdapter.Analyze(program, new() { InferNonLexicalLifetimes = true });
        AssertEx.True(ownership.IsSuccessful, Format(ownership.Diagnostics));
        return Task.CompletedTask;
    }

    private static SafeCoreMirFunction Choose(bool missing = false) => new(0, "choose", Reference,
        [Local(0, Reference, true), Local(1, Reference, true), Local(2, Boolean, true), Local(3, Reference)],
        [Block(0, [], SafeCoreMirTerminator.Branch(Operand(2, Boolean), 1, 2, Source)),
         Block(1, [new(3, SafeCoreMirRvalue.Use(Operand(0, Reference), Source), Source)], SafeCoreMirTerminator.Goto(3, Source)),
         Block(2, missing ? [] : [new(3, SafeCoreMirRvalue.Use(Operand(1, Reference), Source), Source)], SafeCoreMirTerminator.Goto(3, Source)),
         Block(3, [], SafeCoreMirTerminator.Return(Operand(3, Reference), Source))], 0, Source);

    private static Task BranchOriginsAsync()
    {
        var program = new SafeCoreMirProgram([Choose()]);
        SafeCoreMirReferenceProvenanceResult result = SafeCoreMirReferenceProvenance.Analyze(program);
        AssertEx.True(result.IsSuccessful, Format(result.Diagnostics));
        AssertEx.True(result.Functions[0].ReturnOrigins.Select(origin => origin.LocalId).Order().SequenceEqual([0, 1]),
            "A join must retain both possible input origins.");
        SafeCoreMirOwnershipResult ownership = SafeCoreMirOwnershipAdapter.Analyze(program, new() { InferNonLexicalLifetimes = true });
        AssertEx.True(ownership.IsSuccessful, Format(ownership.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task MissingOriginAsync()
    {
        SafeCoreMirReferenceProvenanceResult result = SafeCoreMirReferenceProvenance.Analyze(new([Choose(missing: true)]));
        AssertEx.False(result.IsSuccessful, "One initialized predecessor cannot invent the missing predecessor's origin.");
        AssertEx.True(result.Diagnostics.Any(d => d.Code == SafeCoreMirReferenceProvenance.InvalidOrigin), Format(result.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task LocalEscapeAsync()
    {
        SafeCoreMirFunction function = new(0, "escape", Reference, [Local(0, Integer, true), Local(1, Reference)],
            [Block(0, [new(1, SafeCoreMirRvalue.Unary("&", Operand(0, Integer), Reference, Source), Source)],
                SafeCoreMirTerminator.Return(Operand(1, Reference), Source))], 0, Source);
        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(new([function]));
        AssertEx.False(result.IsSuccessful, "A by-value parameter is local storage and cannot escape by reference.");
        AssertEx.True(result.Diagnostics.Any(d => d.Code == SafeCoreOwnershipDiagnosticCodes.Escape), Format(result.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task ForgedOriginAsync()
    {
        SafeCoreMirFunction function = new(0, "forged", Reference, [Local(0, Reference)],
            [Block(0, [new(0, SafeCoreMirRvalue.Use(SafeCoreMirOperand.Constant(Reference, "7", Source), Source), Source)],
                SafeCoreMirTerminator.Return(Operand(0, Reference), Source))], 0, Source);
        SafeCoreMirReferenceProvenanceResult result = SafeCoreMirReferenceProvenance.Analyze(new([function]));
        AssertEx.False(result.IsSuccessful, "A typed constant is not reference origin evidence.");
        AssertEx.True(result.Diagnostics.Count > 0 && result.Functions.Count == 0, "Invalid evidence must not publish a partial summary.");
        return Task.CompletedTask;
    }

    private static Task BudgetAsync()
    {
        SafeCoreMirProgram program = new([Choose()]);
        SafeCoreMirReferenceProvenanceResult operations = SafeCoreMirReferenceProvenance.Analyze(program, new() { MaximumOperations = 1 });
        SafeCoreMirReferenceProvenanceResult paths = SafeCoreMirReferenceProvenance.Analyze(program, new() { MaximumPaths = 1 });
        AssertEx.True(operations.IsTruncated && paths.IsTruncated, "Reference dataflow must respect both work and path bounds.");
        return Task.CompletedTask;
    }

    private static Task CancellationAsync()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool observed = false;
        try { SafeCoreMirReferenceProvenance.Analyze(new([Choose()]), new() { CancellationToken = cancelled.Token }); }
        catch (OperationCanceledException) { observed = true; }
        AssertEx.True(observed, "Cancelled provenance analysis must stop before work.");
        return Task.CompletedTask;
    }

    private static Task CallLoanUnionAsync()
    {
        SafeCoreMirFunction caller = new(1, "caller", Integer,
            [Local(0, Integer, true), Local(1, Integer, true), Local(2, Boolean, true), Local(3, Reference), Local(4, Reference), Local(5, Reference), Local(6, Integer)],
            [Block(0,
                [new(3, SafeCoreMirRvalue.Unary("&", Operand(0, Integer), Reference, Source), Source),
                 new(4, SafeCoreMirRvalue.Unary("&", Operand(1, Integer), Reference, Source), Source)],
                SafeCoreMirTerminator.Call(SafeCoreMirOperand.Function(0, SafeCoreType.Function([Reference, Reference, Boolean], Reference, "choose"), Source),
                    [Operand(3, Reference), Operand(4, Reference), Operand(2, Boolean)], 5, 1, Source)),
             Block(1,
                [new(0, SafeCoreMirRvalue.Use(SafeCoreMirOperand.Constant(Integer, "9", Source), Source), Source),
                 new(6, SafeCoreMirRvalue.Unary("*", Operand(5, Reference), Integer, Source), Source)],
                 SafeCoreMirTerminator.Return(Operand(6, Integer), Source))], 0, Source);
        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(new([Choose(), caller]), new() { InferNonLexicalLifetimes = true });
        AssertEx.False(result.IsSuccessful, "A write to either possible returned referent must conflict with the live loan.");
        AssertEx.True(result.Diagnostics.Any(d => d.Code == SafeCoreOwnershipDiagnosticCodes.BorrowConflict), Format(result.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task InvalidArenaAsync()
    {
        SafeCoreMirFunction function = new(0, "invalid", Reference, [],
            [Block(0, [], SafeCoreMirTerminator.Return(Operand(999, Reference), Source))], 0, Source);
        SafeCoreMirReferenceProvenanceResult result = SafeCoreMirReferenceProvenance.Analyze(new([function]));
        AssertEx.False(result.IsSuccessful, "Public provenance analysis must validate arena IDs first.");
        AssertEx.Equal(0, result.Functions.Count);
        return Task.CompletedTask;
    }

    private static Task WriteAfterScopeAsync()
    {
        SafeCoreMirSource all = new("scope-write.rs", new TextSpan(0, 100), 0, 100);
        SafeCoreMirSource inner = all with { Span = new(0, 20) };
        SafeCoreMirSource early = all with { Span = new(10, 1) };
        SafeCoreMirSource late = all with { Span = new(50, 1) };
        SafeCoreType mutableReference = SafeCoreType.Reference(Integer, true);
        SafeCoreMirPlace destination = SafeCoreMirPlace.Root(1).Append(SafeCoreMirProjection.Dereference());
        SafeCoreMirFunction function = new(0, "write_after_scope", SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Unit),
            [new SafeCoreMirLocal(0, "owner", Integer, SafeCoreMirLocalKind.User, true, early) { StorageScope = inner },
             new SafeCoreMirLocal(1, "reference", mutableReference, SafeCoreMirLocalKind.User, false, early)],
            [new SafeCoreMirBlock(0,
                [new(0, SafeCoreMirRvalue.Use(SafeCoreMirOperand.Constant(Integer, "1", early), early), early),
                 new(1, SafeCoreMirRvalue.Unary("&mut", SafeCoreMirOperand.Local(0, Integer, early), mutableReference, early), early),
                 new SafeCoreMirStatement(1, SafeCoreMirRvalue.Use(SafeCoreMirOperand.Constant(Integer, "2", late), late), late) { DestinationPlace = destination }],
                SafeCoreMirTerminator.Return(null, late), all)], 0, all);
        SafeCoreMirReferenceProvenanceResult result = SafeCoreMirReferenceProvenance.Analyze(new([function]));
        AssertEx.False(result.IsSuccessful, "A projected store must check the reference's storage lifetime even without reference-valued operands.");
        AssertEx.True(result.Diagnostics.Any(d => d.Code == SafeCoreOwnershipDiagnosticCodes.Escape), Format(result.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task OmittedEvidenceAsync()
    {
        SafeCoreType unit = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Unit);
        SafeCoreMirFunction function = new(0, "omitted", unit,
            [Local(0, Integer, true), Local(1, Reference)],
            [Block(0, [new(1, SafeCoreMirRvalue.Unary("&", Operand(0, Integer), Reference, Source), Source)],
                SafeCoreMirTerminator.Return(null, Source))], 0, Source);
        SafeCoreOwnershipFunction forged = new("omitted",
            [new(0, "local0", Integer, SafeCoreOwnershipKind.Copy, false, 0, false, true, Source),
             new(1, "local1", Reference, SafeCoreOwnershipKind.Copy, false, 0, true, false, Source)],
            [new(0, -1, Source)], [new(0, 0, [], SafeCoreOwnershipTerminator.ReturnUnit(Source), Source)],
            0, SafeCorePanicStrategy.Unwind, Source);
        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(new([function]), new SafeCoreOwnershipProgram([forged]));
        AssertEx.False(result.IsSuccessful, "Explicit ownership evidence cannot erase a MIR loan creation.");
        AssertEx.True(result.Diagnostics.Any(d => d.Code == SafeCoreMirOwnershipAdapter.EvidenceMismatch), Format(result.Diagnostics));
        SafeCoreOwnershipFunction injected = new("omitted", forged.Locals, forged.Scopes,
            [new(0, 0, [SafeCoreOwnershipInstruction.Borrow(0, 1, false, Source), SafeCoreOwnershipInstruction.EndBorrow(1, Source)],
                SafeCoreOwnershipTerminator.ReturnUnit(Source), Source)], 0, SafeCorePanicStrategy.Unwind, Source);
        SafeCoreMirOwnershipResult injectedResult = SafeCoreMirOwnershipAdapter.Analyze(new([function]), new SafeCoreOwnershipProgram([injected]));
        AssertEx.False(injectedResult.IsSuccessful, "Explicit evidence cannot inject an early loan end absent from typed MIR.");
        AssertEx.True(injectedResult.Diagnostics.Any(d => d.Code == SafeCoreMirOwnershipAdapter.EvidenceMismatch), Format(injectedResult.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task ReferenceReplacementAsync()
    {
        SafeCoreType mutableReference = SafeCoreType.Reference(Integer, true);
        SafeCoreMirFunction function = new(0, "replace", Integer,
            [Local(0, Integer, true), Local(1, mutableReference), Local(2, Integer)],
            [Block(0,
                [new(1, SafeCoreMirRvalue.Unary("&mut", Operand(0, Integer), mutableReference, Source), Source),
                 new(1, SafeCoreMirRvalue.Unary("&mut", Operand(0, Integer), mutableReference, Source), Source),
                 new(2, SafeCoreMirRvalue.Unary("*", Operand(1, mutableReference), Integer, Source), Source)],
                SafeCoreMirTerminator.Return(Operand(2, Integer), Source))], 0, Source);
        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(new([function]), new() { InferNonLexicalLifetimes = true });
        AssertEx.True(result.IsSuccessful, Format(result.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task DisjointReborrowAsync()
    {
        SafeCoreType tuple = SafeCoreType.Tuple([Integer, Integer]);
        SafeCoreType tupleReference = SafeCoreType.Reference(tuple, true);
        SafeCoreType mutableReference = SafeCoreType.Reference(Integer, true);
        SafeCoreMirPlace first = SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.Dereference()).Append(SafeCoreMirProjection.TupleIndex(0));
        SafeCoreMirPlace second = SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.Dereference()).Append(SafeCoreMirProjection.TupleIndex(1));
        SafeCoreMirPlace write = SafeCoreMirPlace.Root(1).Append(SafeCoreMirProjection.Dereference());
        SafeCoreMirFunction function = new(0, "disjoint", Integer,
            [Local(0, tupleReference, true), Local(1, mutableReference), Local(2, mutableReference), Local(3, Integer)],
            [Block(0,
                [new(1, SafeCoreMirRvalue.Unary("&mut", SafeCoreMirOperand.PlaceValue(first, Integer, Source), mutableReference, Source), Source),
                 new(2, SafeCoreMirRvalue.Unary("&mut", SafeCoreMirOperand.PlaceValue(second, Integer, Source), mutableReference, Source), Source),
                 new SafeCoreMirStatement(1, SafeCoreMirRvalue.Use(SafeCoreMirOperand.Constant(Integer, "7", Source), Source), Source) { DestinationPlace = write },
                 new(3, SafeCoreMirRvalue.Unary("*", Operand(2, mutableReference), Integer, Source), Source)],
                SafeCoreMirTerminator.Return(Operand(3, Integer), Source))], 0, Source);
        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(new([function]), new() { InferNonLexicalLifetimes = true });
        AssertEx.True(result.IsSuccessful, Format(result.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task IndexInitializationAsync()
    {
        SafeCoreType array = SafeCoreType.Array(Integer, 2);
        SafeCoreType usize = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Usize);
        SafeCoreMirPlace indexed = SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.DynamicIndex(1));
        SafeCoreMirFunction function = new(0, "uninitialized_index", Integer,
            [Local(0, array, true), Local(1, usize), Local(2, Reference), Local(3, Integer)],
            [Block(0,
                [new(2, SafeCoreMirRvalue.Unary("&", SafeCoreMirOperand.PlaceValue(indexed, Integer, Source), Reference, Source), Source),
                 new(3, SafeCoreMirRvalue.Unary("*", Operand(2, Reference), Integer, Source), Source)],
                SafeCoreMirTerminator.Return(Operand(3, Integer), Source))], 0, Source);
        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(new([function]), new() { InferNonLexicalLifetimes = true });
        AssertEx.False(result.IsSuccessful, "An uninitialized index cannot silently use the CLR zero default.");
        AssertEx.True(result.Diagnostics.Any(d => d.Code == SafeCoreOwnershipDiagnosticCodes.UseAfterMove), Format(result.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task ReinitializedFieldsAsync()
    {
        CheckReinitializedField(replaceParent: false);
        CheckReinitializedField(replaceParent: true);
        CheckReinitializedRoot(construct: false);
        CheckReinitializedRoot(construct: true);
        return Task.CompletedTask;
    }

    private static void CheckReinitializedRoot(bool construct)
    {
        SafeCoreType token = SafeCoreType.Adt("Token");
        SafeCoreType pair = SafeCoreType.Tuple([token, token]);
        SafeCoreMirPlace moved = SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.TupleIndex(0));
        SafeCoreMirRvalue replacement = construct
            ? SafeCoreMirRvalue.Tuple([Operand(1, token), Operand(2, token)], pair, Source)
            : SafeCoreMirRvalue.Use(Operand(1, pair), Source);
        SafeCoreMirFunction function = new(0, "restore_root", pair,
            [Local(0, pair, true), Local(1, construct ? token : pair, true), Local(2, token, true), Local(3, token)],
            [Block(0,
                [new(3, SafeCoreMirRvalue.Use(SafeCoreMirOperand.PlaceValue(moved, token, Source), Source), Source),
                 new(0, replacement, Source)], SafeCoreMirTerminator.Return(Operand(0, pair), Source))], 0, Source);
        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(new([function], [new(token, [], Source)]),
            new() { Timeout = TimeSpan.FromSeconds(5) });
        AssertEx.True(result.IsSuccessful, Format(result.Diagnostics));
    }

    private static Task CallArgumentAliasAsync()
    {
        SafeCoreType mutableReference = SafeCoreType.Reference(Integer, true);
        SafeCoreType unit = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Unit);
        SafeCoreMirFunction callee = new(0, "both", unit, [Local(0, mutableReference, true), Local(1, mutableReference, true)],
            [Block(0, [], SafeCoreMirTerminator.Return(null, Source))], 0, Source);
        SafeCoreMirFunction caller = new(1, "caller_alias", unit, [Local(0, mutableReference, true)],
            [Block(0, [], SafeCoreMirTerminator.Call(SafeCoreMirOperand.Function(0,
                SafeCoreType.Function([mutableReference, mutableReference], unit, "both"), Source),
                [Operand(0, mutableReference), Operand(0, mutableReference)], null, 1, Source)),
             Block(1, [], SafeCoreMirTerminator.Return(null, Source))], 0, Source);
        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(new([callee, caller]),
            new() { Timeout = TimeSpan.FromSeconds(5), InferNonLexicalLifetimes = true });
        AssertEx.False(result.IsSuccessful, "One mutable reference cannot supply two simultaneous exclusive call arguments.");
        AssertEx.True(result.Diagnostics.Any(d => d.Code == SafeCoreOwnershipDiagnosticCodes.BorrowConflict), Format(result.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task SourceCallAliasesAsync()
    {
        CheckSource("fn both(a: &mut i32,b: &mut i32){*a=1;*b=2;} fn main(){let mut x=0;let r=&mut x;both(r,r);println!(\"{}\",x);}", false);
        CheckSource("fn mixed(a: &mut i32,b: &i32){*a=1;println!(\"{}\",*b);} fn main(){let mut x=0;let r=&mut x;mixed(r,&*r);}", false);
        CheckSource("fn both(a: &mut i32,b: &mut i32){*a=1;*b=2;} fn mutate(a: &mut i32){*a=3;} fn main(){let mut pair=(0,0);both(&mut pair.0,&mut pair.1);let r=&mut pair.0;mutate(r);mutate(r);println!(\"{}\",pair.0);}", true);
        CheckSource("struct T(i32); fn main(){let mut p=(T(1),T(2));let taken=p.0;p=(T(3),T(4));println!(\"{}\",p.0.0);}", true);
        return Task.CompletedTask;
    }

    private static Task SharedCoercionAsync()
    {
        SafeCoreType mutableReference = SafeCoreType.Reference(Integer, true);
        SafeCoreMirFunction function = new(0, "weaken", Reference,
            [Local(0, mutableReference, true), Local(1, Reference)],
            [Block(0, [new(1, SafeCoreMirRvalue.Coerce(Operand(0, mutableReference), Reference, Source), Source)],
                SafeCoreMirTerminator.Return(Operand(1, Reference), Source))], 0, Source);
        SafeCoreMirProgram program = new([function]);
        SafeCoreMirReferenceProvenanceResult provenance = SafeCoreMirReferenceProvenance.Analyze(program);
        AssertEx.True(provenance.IsSuccessful, Format(provenance.Diagnostics));
        AssertEx.False(provenance.Functions[0].ReturnOrigins.Single().IsMutable, "A shared coercion must not retain mutable-origin authority.");
        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(program, new() { InferNonLexicalLifetimes = true });
        AssertEx.True(result.IsSuccessful, Format(result.Diagnostics));
        CheckSource("fn main(){let mut x=1;let r=&mut x;let s:&i32=r;println!(\"{}\",*s);*r=2;}", true);
        CheckSource("fn main(){let mut x=1;let r=&mut x;let s:&i32=r;*r=2;println!(\"{}\",*s);}", false);
        return Task.CompletedTask;
    }

    private static void CheckSource(string source, bool successful)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        CompilationResult result = CompilerDriver.Check(source, "provenance-source.rs", CompilationProfile.SafeCoreMirV2, timeout.Token);
        AssertEx.Equal(successful, result.Success, Format(result.Diagnostics));
        if (!successful) AssertEx.True(result.Diagnostics.Any(d => d.Code is SafeCoreOwnershipDiagnosticCodes.BorrowConflict or SafeCoreOwnershipDiagnosticCodes.InvalidBorrow), Format(result.Diagnostics));
    }

    private static void CheckReinitializedField(bool replaceParent)
    {
        SafeCoreType token = SafeCoreType.Adt("Token");
        SafeCoreType pair = SafeCoreType.Tuple([token, token]);
        SafeCoreType aggregate = replaceParent ? SafeCoreType.Tuple([pair, token]) : pair;
        SafeCoreType replacement = replaceParent ? pair : token;
        SafeCoreMirPlace destination = SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.TupleIndex(0));
        SafeCoreMirPlace moved = replaceParent ? destination.Append(SafeCoreMirProjection.TupleIndex(0)) : destination;
        SafeCoreMirFunction function = new(0, "restore_field", aggregate,
            [Local(0, aggregate, true), Local(1, replacement, true), Local(2, token)],
            [Block(0,
                [new(2, SafeCoreMirRvalue.Use(SafeCoreMirOperand.PlaceValue(moved, token, Source), Source), Source),
                 new SafeCoreMirStatement(0, SafeCoreMirRvalue.Use(Operand(1, replacement), Source), Source) { DestinationPlace = destination }],
                SafeCoreMirTerminator.Return(Operand(0, aggregate), Source))], 0, Source);
        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(new([function], [new(token, [], Source)]),
            new() { Timeout = TimeSpan.FromSeconds(5) });
        AssertEx.True(result.IsSuccessful, Format(result.Diagnostics));
    }

    private static SafeCoreMirLocal Local(int id, SafeCoreType type, bool parameter = false) =>
        new(id, "local" + id, type, parameter ? SafeCoreMirLocalKind.Parameter : SafeCoreMirLocalKind.Temporary, true, Source);
    private static SafeCoreMirOperand Operand(int id, SafeCoreType type) => SafeCoreMirOperand.Local(id, type, Source);
    private static SafeCoreMirBlock Block(int id, IReadOnlyList<SafeCoreMirStatement> statements, SafeCoreMirTerminator terminator) =>
        new(id, statements, terminator, Source);
    private static string Format(IReadOnlyList<Diagnostic> diagnostics) => string.Join("; ", diagnostics.Select(d => d.Code + ": " + d.Message));
}
