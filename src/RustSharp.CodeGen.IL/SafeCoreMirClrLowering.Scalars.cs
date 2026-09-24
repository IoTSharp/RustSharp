using RustSharp.Semantics;

namespace RustSharp.CodeGen.IL;

public static partial class SafeCoreMirClrLowering
{
    private sealed partial class BodyLowerer
    {
        private void ScalarRuntime(string name, ClrLirType result, params ClrLirType[] parameters) =>
            Emit(new ClrLirCall(new("MirScalar." + name, result, parameters)
            {
                ExternalCall = new("RustSharp.Runtime", "RustSharp.Runtime", "MirScalar", name),
            }));

        private bool EmitScalarCast(SafeCoreMirRvalue value)
        {
            if (value.Kind != SafeCoreMirRvalueKind.Cast) return false;
            if (value.Operands[0].Type.Kind == SafeCoreSemanticTypeKind.Bool &&
                value.Type.Kind is SafeCoreSemanticTypeKind.I32 or SafeCoreSemanticTypeKind.Usize)
            {
                EmitOperand(value.Operands[0]);
                ScalarRuntime("BoolToInteger", ClrLirType.I32, ClrLirType.Bool);
                return true;
            }
            if (value.Operands[0].Type.Kind == SafeCoreSemanticTypeKind.I32 && value.Type.Kind == SafeCoreSemanticTypeKind.Usize)
            {
                EmitOperand(value.Operands[0]);
                ScalarRuntime("ToUsize", ClrLirType.I32, ClrLirType.I32);
                return true;
            }
            return false;
        }

        private bool EmitExtendedBinary(SafeCoreMirRvalue value)
        {
            SafeCoreSemanticTypeKind kind = value.Operands[0].Type.Kind;
            if (kind is not (SafeCoreSemanticTypeKind.I32 or SafeCoreSemanticTypeKind.Usize or SafeCoreSemanticTypeKind.Bool)) return false;
            string? method = value.Operator switch
            {
                "+" when kind == SafeCoreSemanticTypeKind.Usize => "AddUsize",
                "-" when kind == SafeCoreSemanticTypeKind.Usize => "SubtractUsize",
                "*" when kind == SafeCoreSemanticTypeKind.Usize => "MultiplyUsize",
                "/" => "DivideInt32",
                "%" => "RemainderInt32",
                "<<" => kind == SafeCoreSemanticTypeKind.Usize ? "ShiftLeftUsize" : "ShiftLeftInt32",
                ">>" => kind == SafeCoreSemanticTypeKind.Usize ? "ShiftRightUsize" : "ShiftRightInt32",
                "<" or ">=" when kind == SafeCoreSemanticTypeKind.Bool => "LessThanBool",
                ">" or "<=" when kind == SafeCoreSemanticTypeKind.Bool => "GreaterThanBool",
                _ => null,
            };
            if (method is not null)
            {
                EmitOperand(value.Operands[0]);
                EmitOperand(value.Operands[1]);
                bool comparison = value.Operator is "<" or ">" or "<=" or ">=";
                ClrLirType parameterType = kind == SafeCoreSemanticTypeKind.Bool ? ClrLirType.Bool : ClrLirType.I32;
                ScalarRuntime(method, comparison ? ClrLirType.Bool : ClrLirType.I32, parameterType, parameterType);
                if (value.Operator is "<=" or ">=")
                {
                    Emit(new ClrLirLoadBoolean(false));
                    Emit(new ClrLirBinary(ClrLirBinaryOperator.Equal, ClrLirType.Bool));
                }
                return true;
            }
            if (value.Operator is not ("&" or "|" or "^")) return false;
            EmitOperand(value.Operands[0]);
            EmitOperand(value.Operands[1]);
            Emit(new ClrLirBinary(value.Operator switch
            {
                "&" => ClrLirBinaryOperator.And,
                "|" => ClrLirBinaryOperator.Or,
                _ => ClrLirBinaryOperator.ExclusiveOr,
            }, kind == SafeCoreSemanticTypeKind.Bool ? ClrLirType.Bool : ClrLirType.I32));
            return true;
        }
    }
}
