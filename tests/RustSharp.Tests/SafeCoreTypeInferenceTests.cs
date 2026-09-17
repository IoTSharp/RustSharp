using RustSharp.Semantics;

namespace RustSharp.Tests;

internal static class SafeCoreTypeInferenceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("structural types preserve immutable structural and nominal identity", StructuralIdentityAsync),
        new("type inference propagates and defaults numeric constraints", NumericInferenceAsync),
        new("type inference rejects recursive types and rolls back failed constraints", RollbackAndOccursAsync),
        new("type inference distinguishes scalar and reference coercions", CoercionAsync),
        new("type inference preserves function item identity and coerces to pointers", FunctionIdentityAsync),
        new("type inference obeys cancellation and operation variable depth time bounds", BoundedInferenceAsync),
        new("structural type construction bounds collection access and honors cancellation", BoundedConstructionAsync),
        new("type inference preserves closure identity captures and pointer coercion rollback", ClosureInferenceAsync),
        new("type inference checks every reference dereference mutability boundary", ReferenceChainAsync),
    ];

    private static Task StructuralIdentityAsync()
    {
        SafeCoreType integer = Primitive(SafeCoreSemanticTypeKind.I32);
        SafeCoreType boolean = Primitive(SafeCoreSemanticTypeKind.Bool);
        SafeCoreType[] input = [integer, SafeCoreType.Array(boolean, 2)];
        SafeCoreType first = SafeCoreType.Tuple(input);
        input[0] = boolean;
        SafeCoreType second = SafeCoreType.Tuple([Primitive(SafeCoreSemanticTypeKind.I32), SafeCoreType.Array(boolean, 2)]);
        AssertEx.Equal(first, second);
        AssertEx.Equal(first.GetHashCode(), second.GetHashCode());
        AssertEx.Equal("(i32, [bool; 2])", first.ToString());
        AssertEx.Equal(integer, first.Elements[0]);
        AssertEx.Equal(Primitive(SafeCoreSemanticTypeKind.Unit), SafeCoreType.Tuple([]));
        AssertEx.False(SafeCoreType.Tuple([integer]) == integer, "A one-element tuple is not its element type.");
        AssertEx.False(SafeCoreType.Array(integer, 1) == SafeCoreType.Array(integer, 2), "Array length is part of identity.");
        AssertEx.False(SafeCoreType.Adt("crate::left::A") == SafeCoreType.Adt("crate::right::A"), "ADT identity must be nominal.");
        AssertEx.False(SafeCoreType.Reference(integer, true) == SafeCoreType.Reference(integer, false), "Reference mutability is part of identity.");
        return Task.CompletedTask;
    }

    private static Task NumericInferenceAsync()
    {
        var context = new SafeCoreTypeInference();
        SafeCoreType local = context.Fresh();
        SafeCoreType literal = context.Fresh(SafeCoreInferenceKind.Integer);
        SafeCoreType floating = context.Fresh(SafeCoreInferenceKind.Float);
        AssertEx.True(context.Unify(local, literal), "General local type must acquire the literal's integer constraint.");
        AssertEx.False(context.Unify(local, floating), "Integer and floating-point variables cannot unify.");
        AssertEx.False(context.Unify(local, Primitive(SafeCoreSemanticTypeKind.Bool)), "Integer constraints must survive aliases.");
        AssertEx.True(context.Unify(local, Primitive(SafeCoreSemanticTypeKind.U16)), "Context must constrain an integer literal to u16.");
        AssertEx.Equal(Primitive(SafeCoreSemanticTypeKind.U16), context.Resolve(literal, defaultNumerics: true));
        AssertEx.Equal(Primitive(SafeCoreSemanticTypeKind.F64), context.Resolve(floating, defaultNumerics: true));
        SafeCoreType unconstrainedInteger = context.Fresh(SafeCoreInferenceKind.Integer);
        AssertEx.Equal(Primitive(SafeCoreSemanticTypeKind.I32), context.Resolve(unconstrainedInteger, defaultNumerics: true));
        SafeCoreType unconstrained = context.Fresh();
        AssertEx.Equal(SafeCoreSemanticTypeKind.Inference, context.Resolve(unconstrained, defaultNumerics: true).Kind);
        return Task.CompletedTask;
    }

    private static Task RollbackAndOccursAsync()
    {
        var context = new SafeCoreTypeInference();
        SafeCoreType variable = context.Fresh();
        SafeCoreType integer = Primitive(SafeCoreSemanticTypeKind.I32);
        SafeCoreType boolean = Primitive(SafeCoreSemanticTypeKind.Bool);
        AssertEx.False(context.Unify(SafeCoreType.Tuple([variable, boolean]), SafeCoreType.Tuple([integer, integer])),
            "A late tuple mismatch must reject the constraint.");
        AssertEx.Equal(variable, context.Resolve(variable));
        AssertEx.False(context.Unify(variable, SafeCoreType.Reference(variable, false)), "An infinite type must be rejected.");
        AssertEx.True(context.Unify(variable, boolean), "Failed constraints must leave variables reusable.");
        SafeCoreType second = context.Fresh();
        SafeCoreType target = SafeCoreType.Function([second], boolean);
        SafeCoreType source = SafeCoreType.Function([integer], integer, "crate::source");
        AssertEx.False(context.Coerce(source, target), "A return-type mismatch must reject function coercion.");
        AssertEx.Equal(second, context.Resolve(second));
        return Task.CompletedTask;
    }

    private static Task CoercionAsync()
    {
        var context = new SafeCoreTypeInference();
        SafeCoreType integer = Primitive(SafeCoreSemanticTypeKind.I32);
        SafeCoreType boolean = Primitive(SafeCoreSemanticTypeKind.Bool);
        SafeCoreType never = Primitive(SafeCoreSemanticTypeKind.Never);
        AssertEx.True(context.Coerce(never, boolean), "Never must coerce to any result type.");
        AssertEx.False(context.Coerce(boolean, never), "Never coercion must be directional.");
        AssertEx.False(context.Unify(never, boolean), "Coercion must remain distinct from unification.");
        AssertEx.False(context.Coerce(integer, Primitive(SafeCoreSemanticTypeKind.I64)), "Rust does not implicitly widen scalar integers.");
        AssertEx.True(context.Coerce(SafeCoreType.Reference(integer, true), SafeCoreType.Reference(integer, false)),
            "A mutable reference may weaken to shared.");
        AssertEx.False(context.Coerce(SafeCoreType.Reference(integer, false), SafeCoreType.Reference(integer, true)),
            "A shared reference cannot gain mutability.");
        SafeCoreType array = SafeCoreType.Array(integer, 3);
        SafeCoreType slice = SafeCoreType.Slice(integer);
        AssertEx.True(context.Coerce(SafeCoreType.Reference(array, true), SafeCoreType.Reference(slice, false)),
            "Array reference unsizing may combine with mutability weakening.");
        AssertEx.False(context.Coerce(array, slice), "Unsizing requires a supported reference coercion site.");
        AssertEx.False(context.Coerce(SafeCoreType.Reference(array, false), SafeCoreType.Reference(SafeCoreType.Slice(boolean), false)),
            "Array unsizing must preserve the element type.");
        return Task.CompletedTask;
    }

    private static Task FunctionIdentityAsync()
    {
        var context = new SafeCoreTypeInference();
        SafeCoreType integer = Primitive(SafeCoreSemanticTypeKind.I32);
        SafeCoreType item = SafeCoreType.Function([integer], integer, "crate::a");
        SafeCoreType other = SafeCoreType.Function([integer], integer, "crate::b");
        SafeCoreType pointer = SafeCoreType.Function([integer], integer);
        AssertEx.False(context.Unify(item, other), "Functions with identical signatures still have distinct item types.");
        AssertEx.False(context.Unify(item, pointer), "A function item is not a function pointer.");
        AssertEx.True(context.Coerce(item, pointer), "A function item must coerce to its function pointer signature.");
        AssertEx.False(context.Coerce(pointer, item), "A pointer cannot coerce back to a specific function item.");
        AssertEx.False(context.Coerce(item, other), "One function item cannot coerce to another.");
        AssertEx.False(context.Unify(SafeCoreType.Adt("crate::A"), SafeCoreType.Adt("crate::B")), "Distinct ADTs cannot unify.");
        return Task.CompletedTask;
    }

    private static Task BoundedInferenceAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = new SafeCoreTypeInference(new() { CancellationToken = cancellation.Token });
        AssertEx.Throws<OperationCanceledException>(() => cancelled.Fresh());
        var operations = new SafeCoreTypeInference(new() { MaximumOperations = 1 });
        operations.Fresh();
        AssertEx.Throws<SafeCoreTypeInferenceLimitException>(() => operations.Fresh());
        var variables = new SafeCoreTypeInference(new() { MaximumVariables = 1 });
        variables.Fresh();
        AssertEx.Throws<SafeCoreTypeInferenceLimitException>(() => variables.Fresh());
        var depth = new SafeCoreTypeInference(new() { MaximumNestingDepth = 1 });
        SafeCoreType variable = depth.Fresh();
        SafeCoreType nested = SafeCoreType.Tuple([SafeCoreType.Tuple([variable])]);
        AssertEx.Throws<SafeCoreTypeInferenceLimitException>(() => depth.Resolve(nested));
        var timed = new SafeCoreTypeInference(new() { Timeout = TimeSpan.FromTicks(1) });
        AssertEx.Throws<SafeCoreTypeInferenceLimitException>(() => timed.Fresh());
        return Task.CompletedTask;
    }

    private static Task BoundedConstructionAsync()
    {
        SafeCoreType integer = Primitive(SafeCoreSemanticTypeKind.I32);
        var parameters = new IndexOnlyTypeList(integer);
        AssertEx.Equal(SafeCoreType.Function([integer], integer), SafeCoreType.Function(parameters, integer));
        AssertEx.Equal(SafeCoreType.Tuple([integer]), SafeCoreType.Tuple(parameters));
        AssertEx.Throws<SafeCoreTypeInferenceLimitException>(() => SafeCoreType.Adt(new string('a', 262_144)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreType.Function(parameters, integer, cancellationToken: cancellation.Token));
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreType.Tuple(parameters, cancellation.Token));
        return Task.CompletedTask;
    }

    private static Task ClosureInferenceAsync()
    {
        var context = new SafeCoreTypeInference();
        SafeCoreType integer = Primitive(SafeCoreSemanticTypeKind.I32);
        SafeCoreType boolean = Primitive(SafeCoreSemanticTypeKind.Bool);
        SafeCoreType parameter = context.Fresh();
        SafeCoreType closure = SafeCoreType.Closure([parameter], integer, "crate::closure#1", hasCaptures: false);
        SafeCoreType incompatible = SafeCoreType.Function([boolean], boolean);
        AssertEx.False(context.Coerce(closure, incompatible), "A mismatched closure return type must reject pointer coercion.");
        AssertEx.Equal(parameter, context.Resolve(parameter));
        AssertEx.True(context.Coerce(closure, SafeCoreType.Function([integer], integer)), "A later compatible coercion must remain possible.");
        AssertEx.Equal(integer, context.Resolve(closure).ParameterTypes[0]);
        SafeCoreType capture = SafeCoreType.Closure([integer], integer, "crate::closure#2", hasCaptures: true, isMutable: true);
        AssertEx.False(context.Coerce(capture, SafeCoreType.Function([integer], integer)), "Capturing closures cannot reify to function pointers.");
        AssertEx.False(context.Unify(closure, SafeCoreType.Closure([integer], integer, "crate::closure#3", hasCaptures: false)),
            "Different closure expressions have distinct nominal identities.");
        SafeCoreType resolved = context.Resolve(capture);
        AssertEx.True(resolved.HasCaptures && resolved.IsMutable, "Resolving a closure must preserve its capture and call mutability information.");
        return Task.CompletedTask;
    }

    private static Task ReferenceChainAsync()
    {
        var context = new SafeCoreTypeInference();
        SafeCoreType integer = Primitive(SafeCoreSemanticTypeKind.I32);
        SafeCoreType shared = SafeCoreType.Reference(integer, false);
        SafeCoreType mutable = SafeCoreType.Reference(integer, true);
        AssertEx.True(context.Coerce(SafeCoreType.Reference(shared, false), shared), "A shared reference chain may dereference for a shared borrow.");
        AssertEx.True(context.Coerce(SafeCoreType.Reference(mutable, true), mutable), "Every mutable link permits a mutable reborrow.");
        AssertEx.False(context.Coerce(SafeCoreType.Reference(mutable, false), mutable), "An outer shared reference blocks mutable reborrowing.");
        AssertEx.False(context.Coerce(SafeCoreType.Reference(SafeCoreType.Reference(mutable, false), true), mutable),
            "An inner shared reference must also block mutable reborrowing.");
        AssertEx.True(context.Coerce(SafeCoreType.Reference(SafeCoreType.Reference(mutable, false), true), shared),
            "A mixed reference chain can still produce a shared borrow.");
        SafeCoreType variable = context.Fresh();
        SafeCoreType source = SafeCoreType.Reference(SafeCoreType.Reference(SafeCoreType.Tuple([variable, integer]), false), false);
        SafeCoreType target = SafeCoreType.Reference(SafeCoreType.Reference(SafeCoreType.Tuple([integer, Primitive(SafeCoreSemanticTypeKind.Bool)]), false), false);
        AssertEx.False(context.Coerce(source, target), "All dereference alternatives must fail for incompatible tuple fields.");
        AssertEx.Equal(variable, context.Resolve(variable));
        AssertEx.True(context.Unify(variable, Primitive(SafeCoreSemanticTypeKind.Bool)), "Failed reference coercion alternatives must restore their partial substitutions.");
        return Task.CompletedTask;
    }

    private sealed class IndexOnlyTypeList(SafeCoreType type) : IReadOnlyList<SafeCoreType>
    {
        public int Count => 1;
        public SafeCoreType this[int index] => index == 0 ? type : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<SafeCoreType> GetEnumerator() => throw new InvalidOperationException("A bounded factory must use Count and indexed access.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static SafeCoreType Primitive(SafeCoreSemanticTypeKind kind) => SafeCoreType.Primitive(kind);
}
