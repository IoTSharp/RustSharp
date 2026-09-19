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
            constructorResolver, fieldResolver, CheckBudget, cancellationToken);

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

        foreach (ClrLirBlock block in method.Blocks)
        {
            encoder.MarkLabel(labels[block.Label]);
            foreach (ClrLirInstruction instruction in block.Instructions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkBudget?.Invoke();
                switch (instruction)
                {
                    case ClrLirLoadInt32 loadInt32:
                        encoder.LoadConstantI4(loadInt32.Value);
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
        }

        return Math.Max(1, validation.MaximumStackDepth);
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
