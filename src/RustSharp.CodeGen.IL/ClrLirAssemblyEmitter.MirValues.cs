using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace RustSharp.CodeGen.IL;

public static partial class ClrLirAssemblyEmitter
{
    // Ordinary interface calls keep owner/path traversal visible to NativeAOT.
    // No reflection, dynamic code, unmanaged pointers or byref fields are used.
    private static void EmitMirValueAccessors(MetadataBuilder metadata, MethodBodyStreamEncoder bodies,
        ClrLirValueType layout, TypeDefinitionHandle type, MethodDefinitionHandle constructor,
        ImmutableArray<FieldDefinitionHandle> fields, Dictionary<string, TypeDefinitionHandle> valueTypes,
        Action checkBudget)
    {
        AssemblyReferenceHandle runtime = metadata.AddAssemblyReference(metadata.GetOrAddString("RustSharp.Runtime"),
            new Version(0, 1, 0, 0), default, default, default, default);
        TypeReferenceHandle contract = metadata.AddTypeReference(runtime, metadata.GetOrAddString("RustSharp.Runtime"),
            metadata.GetOrAddString("IMirValue"));
        metadata.AddInterfaceImplementation(type, contract);
        AddAccessor(write: false);
        AddAccessor(write: true);

        void AddAccessor(bool write)
        {
            var bytes = new BlobBuilder();
            var flow = new ControlFlowBuilder();
            var il = new InstructionEncoder(bytes, flow);
            for (int index = 0; index < fields.Length; index++)
            {
                checkBudget();
                LabelHandle next = il.DefineLabel();
                il.LoadArgument(1);
                il.LoadConstantI4(index);
                il.Branch(ILOpCode.Bne_un, next);
                if (write)
                {
                    // Reconstruct the value instead of mutating its box. A box
                    // may be shared by independent Rust value copies.
                    for (int field = 0; field < fields.Length; field++)
                    {
                        checkBudget();
                        if (field == index)
                        {
                            il.LoadArgument(2);
                            ConvertObject(layout.Fields[field].Type, unbox: true);
                        }
                        else
                        {
                            il.LoadArgument(0);
                            il.OpCode(ILOpCode.Ldfld);
                            il.Token(fields[field]);
                        }
                    }
                    il.OpCode(ILOpCode.Newobj);
                    il.Token(constructor);
                    il.OpCode(ILOpCode.Box);
                    il.Token(type);
                }
                else
                {
                    il.LoadArgument(0);
                    il.OpCode(ILOpCode.Ldfld);
                    il.Token(fields[index]);
                    ConvertObject(layout.Fields[index].Type, unbox: false);
                }
                il.OpCode(ILOpCode.Ret);
                il.MarkLabel(next);
            }
            // Throwing null produces a deterministic NullReferenceException for
            // malformed backend paths; validated MIR never reaches this edge.
            il.OpCode(ILOpCode.Ldnull);
            il.OpCode(ILOpCode.Throw);
            int offset = bodies.AddMethodBody(il, Math.Max(2, fields.Length));
            metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final |
                MethodAttributes.NewSlot | MethodAttributes.HideBySig, MethodImplAttributes.IL | MethodImplAttributes.Managed,
                metadata.GetOrAddString(write ? "WithField" : "ReadField"),
                CreateMethodSignature(metadata, ClrLirType.Any,
                    write ? [ClrLirType.I32, ClrLirType.Any] : [ClrLirType.I32], valueTypes, isInstanceMethod: true),
                offset, MetadataTokens.ParameterHandle(1));

            void ConvertObject(ClrLirType fieldType, bool unbox)
            {
                if (fieldType == ClrLirType.Any) return;
                if (fieldType == ClrLirType.Text)
                {
                    if (unbox)
                    {
                        il.OpCode(ILOpCode.Castclass);
                        il.Token(ClrLirEmitter.PrimitiveTypeHandle(metadata, "String"));
                    }
                    return;
                }
                il.OpCode(unbox ? ILOpCode.Unbox_any : ILOpCode.Box);
                il.Token(fieldType.Kind == ClrLirTypeKind.Value ? valueTypes[fieldType.Name!] :
                    ClrLirEmitter.PrimitiveTypeHandle(metadata, fieldType == ClrLirType.Bool ? "Boolean" : "Int32"));
            }
        }
    }
}
