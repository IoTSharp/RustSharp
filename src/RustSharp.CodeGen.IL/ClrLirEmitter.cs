using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace RustSharp.CodeGen.IL;

public sealed record ClrLirMethodBody
{
    public ClrLirMethodBody(ImmutableArray<byte> ilBytes, int maxStack)
    {
        if (ilBytes.IsDefault)
        {
            ilBytes = [];
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxStack, 1);

        IlBytes = ilBytes;
        MaxStack = maxStack;
    }

    public ImmutableArray<byte> IlBytes { get; }
    public int MaxStack { get; }
}

/// <summary>Lower a validated CLR LIR method to deterministic ECMA-335 method-body bytes.</summary>
public static class ClrLirEmitter
{
    public static ClrLirMethodBody EmitMethodBody(
        ClrLirMethod method,
        MetadataBuilder metadata,
        Func<ClrLirCallSite, EntityHandle> callResolver,
        Func<ClrLirValueType, EntityHandle>? constructorResolver = null,
        Func<ClrLirValueType, int, EntityHandle>? fieldResolver = null,
        CancellationToken cancellationToken = default) => Emit(method, metadata, callResolver,
            constructorResolver, fieldResolver, cancellationToken);

    public static ClrLirMethodBody Emit(
        ClrLirMethod method,
        MetadataBuilder metadata,
        Func<ClrLirCallSite, EntityHandle> callResolver,
        Func<ClrLirValueType, EntityHandle>? constructorResolver = null,
        Func<ClrLirValueType, int, EntityHandle>? fieldResolver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(callResolver);

        var code = new BlobBuilder();
        var controlFlow = new ControlFlowBuilder();
        var encoder = new InstructionEncoder(code, controlFlow);
        long started = Stopwatch.GetTimestamp();
        int maxStack = EncodeInstructions(method, metadata, callResolver, encoder,
            constructorResolver, fieldResolver, CheckBudget, cancellationToken: cancellationToken);

        return new ClrLirMethodBody(code.ToArray().ToImmutableArray(), maxStack);

        void CheckBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(10))
                throw new TimeoutException("CLR LIR emission exceeded its time limit.");
        }
    }

    /// <summary>
    /// Encodes a validated method into an existing instruction encoder. This is
    /// shared by the standalone method-body API and the PE integration spike so
    /// both paths use exactly the same label and instruction lowering rules.
    /// </summary>
    internal static int EncodeInstructions(
        ClrLirMethod method,
        MetadataBuilder metadata,
        Func<ClrLirCallSite, EntityHandle> callResolver,
        InstructionEncoder encoder,
        Func<ClrLirValueType, EntityHandle>? constructorResolver = null,
        Func<ClrLirValueType, int, EntityHandle>? fieldResolver = null,
        Action? checkBudget = null,
        Func<ClrLirType, EntityHandle>? valueTypeResolver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(callResolver);

        ClrLirValidationResult validation = method.Validate(cancellationToken);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Cannot emit invalid CLR LIR: {string.Join("; ", validation.Diagnostics)}");
        }

        var labels = new Dictionary<string, LabelHandle>(StringComparer.Ordinal);
        (MemberReferenceHandle Culture, MemberReferenceHandle Convert)? invariantFormat = null;
        foreach (ClrLirBlock block in method.Blocks)
        {
            labels.Add(block.Label, encoder.DefineLabel());
        }

        // A source-level Drop obligation is also materialized as a CLR fault
        // handler.  Normal MIR return paths contain explicit destructor calls;
        // this handler covers exceptions raised by the function body before a
        // normal return.  ControlFlowBuilder carries the labels into the
        // MethodBodyStreamEncoder without changing the ordinary LIR CFG.
        LabelHandle tryStart = default;
        LabelHandle tryEnd = default;
        LabelHandle handlerStart = default;
        LabelHandle handlerEnd = default;
        if (method.HasExceptionCleanup)
        {
            tryStart = encoder.DefineLabel();
            tryEnd = encoder.DefineLabel();
            handlerStart = encoder.DefineLabel();
            handlerEnd = encoder.DefineLabel();
            encoder.MarkLabel(tryStart);
        }

        bool tryEndMarked = false;
        int blockOrdinal = 0;
        foreach (ClrLirBlock block in method.Blocks)
        {
            if (method.HasExceptionCleanup && !tryEndMarked &&
                blockOrdinal == method.FaultTryBlockCount)
            {
                encoder.MarkLabel(tryEnd);
                tryEndMarked = true;
            }
            encoder.MarkLabel(labels[block.Label]);
            foreach (ClrLirInstruction instruction in block.Instructions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkBudget?.Invoke();
                switch (instruction)
                {
                    case ClrLirLoadNull:
                        encoder.OpCode(ILOpCode.Ldnull);
                        break;
                    case ClrLirLoadType loadType:
                        encoder.OpCode(ILOpCode.Ldtoken);
                        encoder.Token(valueTypeResolver?.Invoke(loadType.Type) ??
                            throw new InvalidOperationException("A runtime type token requires a value type resolver."));
                        encoder.Call(AddTypeFromHandle(metadata));
                        break;
                    case ClrLirBox box:
                        EncodeBox(box.Type, false);
                        break;
                    case ClrLirUnbox unbox:
                        EncodeBox(unbox.Type, true);
                        break;
                    case ClrLirLoadInt32 loadInt32:
                        encoder.LoadConstantI4(loadInt32.Value);
                        break;
                    case ClrLirLeave leave:
                        encoder.Branch(ILOpCode.Leave, labels[leave.Target]);
                        break;
                    case ClrLirLoadBoolean loadBoolean:
                        encoder.LoadConstantI4(loadBoolean.Value ? 1 : 0);
                        break;
                    case ClrLirLoadString loadString:
                        encoder.LoadString(metadata.GetOrAddUserString(loadString.Value));
                        break;
                    case ClrLirLoadLocal loadLocal:
                        encoder.LoadLocal(loadLocal.Index);
                        break;
                    case ClrLirLoadLocalAddress address:
                        encoder.LoadLocalAddress(address.Index);
                        break;
                    case ClrLirFieldAddress address:
                        EntityHandle addressField = fieldResolver?.Invoke(address.Definition, address.FieldIndex) ?? default;
                        if (addressField.IsNil) throw new InvalidOperationException("Field address requires a resolved field.");
                        encoder.OpCode(ILOpCode.Ldflda);
                        encoder.Token(addressField);
                        break;
                    case ClrLirLoadIndirect indirect:
                        EncodeIndirect(indirect.Type, store: false);
                        break;
                    case ClrLirStoreIndirect indirect:
                        EncodeIndirect(indirect.Type, store: true);
                        break;
                    case ClrLirReadOnlyReference:
                        break;
                    case ClrLirThrowIndexOutOfRange:
                        encoder.OpCode(ILOpCode.Newobj);
                        encoder.Token(AddIndexOutOfRangeConstructor(metadata));
                        encoder.OpCode(ILOpCode.Throw);
                        break;
                    case ClrLirStoreLocal storeLocal:
                        encoder.StoreLocal(storeLocal.Index);
                        break;
                    case ClrLirLoadArgument argument:
                        encoder.LoadArgument(argument.Index);
                        break;
                    case ClrLirDiscard:
                        encoder.OpCode(ILOpCode.Pop);
                        break;
                    case ClrLirConstructValue construct:
                        EntityHandle constructor = constructorResolver?.Invoke(construct.Definition) ?? default;
                        if (constructor.IsNil) throw new InvalidOperationException("Value construction requires a resolved constructor.");
                        encoder.OpCode(ILOpCode.Newobj);
                        encoder.Token(constructor);
                        break;
                    case ClrLirReadField field:
                        EntityHandle fieldHandle = fieldResolver?.Invoke(field.Definition, field.FieldIndex) ?? default;
                        if (fieldHandle.IsNil) throw new InvalidOperationException("Field access requires a resolved field.");
                        encoder.OpCode(ILOpCode.Ldfld);
                        encoder.Token(fieldHandle);
                        break;
                    case ClrLirFormatInt32:
                        invariantFormat ??= AddInvariantFormatReferences(metadata);
                        encoder.Call(invariantFormat.Value.Culture);
                        encoder.Call(invariantFormat.Value.Convert);
                        break;
                    case ClrLirBinary binary:
                        encoder.OpCode(binary.Operator switch
                        {
                            ClrLirBinaryOperator.AddChecked => ILOpCode.Add_ovf,
                            ClrLirBinaryOperator.SubtractChecked => ILOpCode.Sub_ovf,
                            ClrLirBinaryOperator.MultiplyChecked => ILOpCode.Mul_ovf,
                            ClrLirBinaryOperator.Equal => ILOpCode.Ceq,
                            ClrLirBinaryOperator.LessThan => ILOpCode.Clt,
                            ClrLirBinaryOperator.GreaterThan => ILOpCode.Cgt,
                            ClrLirBinaryOperator.ExclusiveOr => ILOpCode.Xor,
                            ClrLirBinaryOperator.And => ILOpCode.And,
                            ClrLirBinaryOperator.Or => ILOpCode.Or,
                            _ => throw new InvalidOperationException("Invalid binary operator."),
                        });
                        break;
                    case ClrLirCall call:
                        EntityHandle target = callResolver(call.Site);
                        if (target.IsNil)
                        {
                            throw new InvalidOperationException($"Call resolver returned a nil target for '{call.Site.Name}'.");
                        }

                        encoder.Call(target);
                        break;
                    case ClrLirBranch branch:
                        encoder.Branch(ILOpCode.Br, labels[branch.Target]);
                        break;
                    case ClrLirBranchTrue branchTrue:
                        encoder.Branch(ILOpCode.Brtrue, labels[branchTrue.Target]);
                        break;
                    case ClrLirReturn:
                        encoder.OpCode(ILOpCode.Ret);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported CLR LIR instruction '{instruction.GetType().Name}'.");
                }
            }
            blockOrdinal++;
        }

        void EncodeIndirect(ClrLirType type, bool store)
        {
            if (type.Kind == ClrLirTypeKind.Value)
            {
                EntityHandle token = valueTypeResolver?.Invoke(type) ?? default;
                if (token.IsNil) throw new InvalidOperationException("Indirect aggregate access requires a resolved value type.");
                encoder.OpCode(store ? ILOpCode.Stobj : ILOpCode.Ldobj);
                encoder.Token(token);
                return;
            }
            encoder.OpCode(type.Kind switch
            {
                ClrLirTypeKind.I32 => store ? ILOpCode.Stind_i4 : ILOpCode.Ldind_i4,
                ClrLirTypeKind.Bool => store ? ILOpCode.Stind_i1 : ILOpCode.Ldind_u1,
                ClrLirTypeKind.Any or ClrLirTypeKind.Text => store ? ILOpCode.Stind_ref : ILOpCode.Ldind_ref,
                _ => throw new InvalidOperationException("Invalid indirect CLR type."),
            });
        }

        void EncodeBox(ClrLirType type, bool unbox)
        {
            if (type == ClrLirType.Any) return;
            if (type == ClrLirType.Text)
            {
                if (unbox)
                {
                    encoder.OpCode(ILOpCode.Castclass);
                    encoder.Token(PrimitiveTypeHandle(metadata, "String"));
                }
                return;
            }
            EntityHandle handle = type.Kind == ClrLirTypeKind.Value
                ? valueTypeResolver?.Invoke(type) ?? throw new InvalidOperationException("Boxing requires a value type resolver.")
                : PrimitiveTypeHandle(metadata, type == ClrLirType.Bool ? "Boolean" : "Int32");
            encoder.OpCode(unbox ? ILOpCode.Unbox_any : ILOpCode.Box);
            encoder.Token(handle);
        }

        if (method.HasExceptionCleanup)
        {
            if (!tryEndMarked)
                encoder.MarkLabel(tryEnd);
            encoder.MarkLabel(handlerStart);
            if (method.PanicHandling is { } panicHandling)
            {
                EncodePanicHandler(panicHandling);
                encoder.MarkLabel(handlerEnd);
                encoder.ControlFlowBuilder!.AddCatchRegion(tryStart, tryEnd, handlerStart, handlerEnd,
                    PrimitiveTypeHandle(metadata, "Exception"));
                return Math.Max(2, validation.MaximumStackDepth);
            }
            foreach (ClrLirCallSite cleanup in method.ExceptionCleanup)
            {
                EntityHandle target = callResolver(cleanup);
                if (target.IsNil)
                    throw new InvalidOperationException($"Call resolver returned a nil cleanup target for '{cleanup.Name}'.");
                encoder.Call(target);
            }

            foreach (ClrLirGuardedCleanup cleanup in method.GuardedExceptionCleanup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkBudget?.Invoke();
                LabelHandle next = encoder.DefineLabel();
                encoder.LoadLocal(cleanup.FlagLocalIndex);
                encoder.Branch(ILOpCode.Brfalse, next);
                EncodeCleanupGuards(cleanup, next);
                // Consume the obligation before any user destructor executes.
                encoder.LoadConstantI4(0);
                encoder.StoreLocal(cleanup.FlagLocalIndex);
                EncodeConsumedFlags(cleanup);
                EncodeSharedDropConsumption(cleanup);
                EncodeCleanupReceiver(cleanup);
                EntityHandle target = callResolver(cleanup.Site);
                if (target.IsNil)
                    throw new InvalidOperationException($"Call resolver returned a nil cleanup target for '{cleanup.Site.Name}'.");
                encoder.Call(target);
                encoder.MarkLabel(next);
            }

            encoder.OpCode(ILOpCode.Endfinally);
            encoder.MarkLabel(handlerEnd);
            encoder.ControlFlowBuilder!.AddFaultRegion(tryStart, tryEnd, handlerStart, handlerEnd);
        }

        return Math.Max(1, validation.MaximumStackDepth);

        void EncodePanicHandler(ClrLirPanicHandling handling)
        {
            encoder.StoreLocal(handling.PanicLocalIndex);
            LabelHandle propagate = encoder.DefineLabel();
            encoder.LoadLocal(handling.PanicLocalIndex);
            CallPanic("IsAbort", ClrLirType.Bool, ClrLirType.Any);
            encoder.Branch(ILOpCode.Brtrue, propagate);
            // Legacy v1 and ordinary function bodies propagate a nested failure
            // intact. Native v2 destructor bodies can finish their own owned
            // obligations on the native Linux unwind path. A subsequent failure
            // must stop that obligation list rather than use normal continuation.
            if (handling.DropCleanupProfile == SafeCoreDropCleanupProfile.NativeV2 &&
                handling.IsDestructorBody && !handling.AbortWithoutUnwind)
            {
                LabelHandle beginCleanup = encoder.DefineLabel();
                CallPanic("IsUnwinding", ClrLirType.Bool);
                encoder.Branch(ILOpCode.Brfalse, beginCleanup);
                CallPanic("NativeV2UnwindsDestructorBody", ClrLirType.Bool);
                encoder.Branch(ILOpCode.Brfalse, propagate);
                encoder.LoadConstantI4(0);
                encoder.StoreLocal(handling.NormalCleanupLocalIndex);
                encoder.MarkLabel(beginCleanup);
            }
            else
            {
                CallPanic("IsUnwinding", ClrLirType.Bool);
                encoder.Branch(ILOpCode.Brtrue, propagate);
            }
            if (handling.AbortWithoutUnwind)
            {
                encoder.LoadLocal(handling.PanicLocalIndex);
                CallPanic("Abort", ClrLirType.Any, ClrLirType.Any);
                encoder.StoreLocal(handling.PanicLocalIndex);
                encoder.Branch(ILOpCode.Br, propagate);
            }
            else
            {
                foreach (ClrLirGuardedCleanup cleanup in method.GuardedExceptionCleanup)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    checkBudget?.Invoke();
                    LabelHandle next = encoder.DefineLabel();
                    LabelHandle cleanupTryStart = encoder.DefineLabel();
                    LabelHandle cleanupTryEnd = encoder.DefineLabel();
                    LabelHandle cleanupHandlerStart = encoder.DefineLabel();
                    LabelHandle cleanupHandlerEnd = encoder.DefineLabel();
                    LabelHandle normalFailure = encoder.DefineLabel();
                    LabelHandle nestedAbort = encoder.DefineLabel();
                    encoder.LoadLocal(cleanup.FlagLocalIndex);
                    encoder.Branch(ILOpCode.Brfalse, next);
                    EncodeCleanupGuards(cleanup, next);
                    encoder.LoadConstantI4(0);
                    encoder.StoreLocal(cleanup.FlagLocalIndex);
                    EncodeConsumedFlags(cleanup);
                    encoder.LoadLocal(handling.NormalCleanupLocalIndex);
                    encoder.LoadConstantI4(1);
                    encoder.OpCode(ILOpCode.Xor);
                    CallPanic("SwapUnwinding", ClrLirType.Bool, ClrLirType.Bool);
                    encoder.StoreLocal(handling.UnwindStateLocalIndex);
                    encoder.MarkLabel(cleanupTryStart);
                    EncodeSharedDropConsumption(cleanup);
                    EncodeCleanupReceiver(cleanup);
                    EntityHandle destructor = callResolver(cleanup.Site);
                    if (destructor.IsNil) throw new InvalidOperationException("A generated destructor did not resolve.");
                    encoder.Call(destructor);
                    RestoreUnwindState(handling);
                    encoder.Branch(ILOpCode.Leave, next);
                    encoder.MarkLabel(cleanupTryEnd);
                    encoder.MarkLabel(cleanupHandlerStart);
                    encoder.StoreLocal(handling.CleanupFailureLocalIndex);
                    RestoreUnwindState(handling);
                    encoder.LoadLocal(handling.CleanupFailureLocalIndex);
                    CallPanic("IsAbort", ClrLirType.Bool, ClrLirType.Any);
                    encoder.Branch(ILOpCode.Brtrue, nestedAbort);
                    encoder.LoadLocal(handling.NormalCleanupLocalIndex);
                    encoder.Branch(ILOpCode.Brtrue, normalFailure);
                    encoder.LoadLocal(handling.PanicLocalIndex);
                    encoder.LoadLocal(handling.CleanupFailureLocalIndex);
                    CallPanic("DoublePanic", ClrLirType.Any, ClrLirType.Any, ClrLirType.Any);
                    encoder.StoreLocal(handling.PanicLocalIndex);
                    encoder.Branch(ILOpCode.Leave, propagate);
                    encoder.MarkLabel(nestedAbort);
                    if (handling.DropCleanupProfile == SafeCoreDropCleanupProfile.NativeV2)
                        encoder.LoadLocal(handling.PanicLocalIndex);
                    encoder.LoadLocal(handling.CleanupFailureLocalIndex);
                    if (handling.DropCleanupProfile == SafeCoreDropCleanupProfile.NativeV2)
                        CallPanic("PreserveNestedAbort", ClrLirType.Any, ClrLirType.Any, ClrLirType.Any);
                    encoder.StoreLocal(handling.PanicLocalIndex);
                    encoder.Branch(ILOpCode.Leave, propagate);
                    encoder.MarkLabel(normalFailure);
                    encoder.LoadLocal(handling.PanicLocalIndex);
                    encoder.LoadLocal(handling.CleanupFailureLocalIndex);
                    CallPanic("ContinueNormalCleanup", ClrLirType.Any, ClrLirType.Any, ClrLirType.Any);
                    encoder.StoreLocal(handling.PanicLocalIndex);
                    encoder.Branch(ILOpCode.Leave, next);
                    encoder.MarkLabel(cleanupHandlerEnd);
                    encoder.MarkLabel(next);
                    encoder.ControlFlowBuilder!.AddCatchRegion(cleanupTryStart, cleanupTryEnd,
                        cleanupHandlerStart, cleanupHandlerEnd, PrimitiveTypeHandle(metadata, "Exception"));
                }
            }
            encoder.MarkLabel(propagate);
            encoder.LoadLocal(handling.PanicLocalIndex);
            encoder.LoadConstantI4(handling.IsEntryBoundary ? 1 : 0);
            CallPanic("Propagate", ClrLirType.Void, ClrLirType.Any, ClrLirType.Bool);
            // Propagate is non-returning. Keep that terminal edge explicit in IL.
            encoder.OpCode(ILOpCode.Ldnull);
            encoder.OpCode(ILOpCode.Throw);
        }

        void CallPanic(string name, ClrLirType result, params ClrLirType[] parameters)
        {
            EntityHandle target = callResolver(new("RustGeneratedPanic." + name, result, parameters)
            {
                ExternalCall = new("RustSharp.Runtime", "RustSharp.Runtime", "RustGeneratedPanic", name),
            });
            if (target.IsNil) throw new InvalidOperationException("A generated panic operation did not resolve.");
            encoder.Call(target);
        }

        void RestoreUnwindState(ClrLirPanicHandling handling)
        {
            encoder.LoadLocal(handling.UnwindStateLocalIndex);
            CallPanic("SwapUnwinding", ClrLirType.Bool, ClrLirType.Bool);
            encoder.OpCode(ILOpCode.Pop);
        }

        void EncodeCleanupReceiver(ClrLirGuardedCleanup cleanup)
        {
            if (cleanup.ReceiverLocalIndex is int receiver) encoder.LoadLocal(receiver);
            EncodeCleanupProjection(cleanup.ReceiverInstructions);
        }

        void EncodeCleanupGuards(ClrLirGuardedCleanup cleanup, LabelHandle skipped)
        {
            foreach (var guard in cleanup.GuardInstructions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkBudget?.Invoke();
                EncodeCleanupProjection(guard);
                encoder.Branch(ILOpCode.Brfalse, skipped);
            }
        }

        void EncodeSharedDropConsumption(ClrLirGuardedCleanup cleanup)
        {
            if (!cleanup.HasReceiver) return;
            EncodeCleanupReceiver(cleanup);
            EntityHandle target = callResolver(new("MirReference.ConsumeDrop", ClrLirType.Void, [ClrLirType.Any])
            {
                ExternalCall = new("RustSharp.Runtime", "RustSharp.Runtime", "MirReference", "ConsumeDrop"),
            });
            if (target.IsNil) throw new InvalidOperationException("A shared cleanup consumption operation did not resolve.");
            encoder.Call(target);
        }

        void EncodeCleanupProjection(IEnumerable<ClrLirInstruction> instructions)
        {
            foreach (ClrLirInstruction instruction in instructions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkBudget?.Invoke();
                switch (instruction)
                {
                    case ClrLirLoadLocal local: encoder.LoadLocal(local.Index); break;
                    case ClrLirLoadInt32 integer: encoder.LoadConstantI4(integer.Value); break;
                    case ClrLirUnbox unbox: EncodeBox(unbox.Type, true); break;
                    case ClrLirCall call:
                        EntityHandle target = callResolver(call.Site);
                        if (target.IsNil) throw new InvalidOperationException("A cleanup receiver projection did not resolve.");
                        encoder.Call(target);
                        break;
                    default: throw new InvalidOperationException("An unsafe cleanup receiver instruction reached emission.");
                }
            }
        }

        void EncodeConsumedFlags(ClrLirGuardedCleanup cleanup)
        {
            foreach (int flag in cleanup.ConsumedFlagLocalIndices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkBudget?.Invoke();
                encoder.LoadConstantI4(0);
                encoder.StoreLocal(flag);
            }
        }
    }

    private static MemberReferenceHandle AddIndexOutOfRangeConstructor(MetadataBuilder metadata)
    {
        AssemblyReferenceHandle runtime = metadata.AddAssemblyReference(metadata.GetOrAddString("System.Runtime"),
            new Version(10, 0, 0, 0), default,
            metadata.GetOrAddBlob(new byte[] { 0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a }), default, default);
        TypeReferenceHandle exception = metadata.AddTypeReference(runtime, metadata.GetOrAddString("System"),
            metadata.GetOrAddString("IndexOutOfRangeException"));
        var signature = new BlobBuilder();
        new BlobEncoder(signature).MethodSignature(isInstanceMethod: true).Parameters(0,
            static result => result.Void(), static _ => { });
        return metadata.AddMemberReference(exception, metadata.GetOrAddString(".ctor"), metadata.GetOrAddBlob(signature));
    }

    internal static TypeReferenceHandle PrimitiveTypeHandle(MetadataBuilder metadata, string name)
    {
        AssemblyReferenceHandle runtime = metadata.AddAssemblyReference(metadata.GetOrAddString("System.Runtime"),
            new Version(10, 0, 0, 0), default,
            metadata.GetOrAddBlob(new byte[] { 0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a }), default, default);
        return metadata.AddTypeReference(runtime, metadata.GetOrAddString("System"), metadata.GetOrAddString(name));
    }

    private static MemberReferenceHandle AddTypeFromHandle(MetadataBuilder metadata)
    {
        TypeReferenceHandle type = PrimitiveTypeHandle(metadata, "Type");
        TypeReferenceHandle handle = PrimitiveTypeHandle(metadata, "RuntimeTypeHandle");
        var signature = new BlobBuilder();
        new BlobEncoder(signature).MethodSignature().Parameters(1,
            result => result.Type().Type(type, isValueType: false),
            parameters => parameters.AddParameter().Type().Type(handle, isValueType: true));
        return metadata.AddMemberReference(type, metadata.GetOrAddString("GetTypeFromHandle"), metadata.GetOrAddBlob(signature));
    }

    private static (MemberReferenceHandle Culture, MemberReferenceHandle Convert) AddInvariantFormatReferences(MetadataBuilder metadata)
    {
        AssemblyReferenceHandle runtime = metadata.AddAssemblyReference(metadata.GetOrAddString("System.Runtime"),
            new Version(10, 0, 0, 0), default,
            metadata.GetOrAddBlob((ImmutableArray<byte>)[0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a]), default, default);
        TypeReferenceHandle culture = metadata.AddTypeReference(runtime, metadata.GetOrAddString("System.Globalization"), metadata.GetOrAddString("CultureInfo"));
        TypeReferenceHandle provider = metadata.AddTypeReference(runtime, metadata.GetOrAddString("System"), metadata.GetOrAddString("IFormatProvider"));
        TypeReferenceHandle convert = metadata.AddTypeReference(runtime, metadata.GetOrAddString("System"), metadata.GetOrAddString("Convert"));
        var cultureSignature = new BlobBuilder();
        new BlobEncoder(cultureSignature).MethodSignature().Parameters(0,
            result => result.Type().Type(culture, isValueType: false), _ => { });
        var convertSignature = new BlobBuilder();
        new BlobEncoder(convertSignature).MethodSignature().Parameters(2,
            result => result.Type().String(), parameters =>
            {
                parameters.AddParameter().Type().Int32();
                parameters.AddParameter().Type().Type(provider, isValueType: false);
            });
        return (
            metadata.AddMemberReference(culture, metadata.GetOrAddString("get_InvariantCulture"), metadata.GetOrAddBlob(cultureSignature)),
            metadata.AddMemberReference(convert, metadata.GetOrAddString("ToString"), metadata.GetOrAddBlob(convertSignature)));
    }
}
