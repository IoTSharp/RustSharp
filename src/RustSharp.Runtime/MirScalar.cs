namespace RustSharp.Runtime;

/// <summary>Checked scalar operations for i32 and the declared nonnegative i32-sized usize profile.</summary>
public static class MirScalar
{
    public static int DivideInt32(int left, int right) => checked(left / right);

    public static int RemainderInt32(int left, int right)
    {
        if (left == int.MinValue && right == -1) throw new OverflowException("Rust signed remainder overflows.");
        return left % right;
    }

    public static int ShiftLeftInt32(int value, int count)
    {
        ValidateShift(count, 32);
        return value << count;
    }

    public static int ShiftRightInt32(int value, int count)
    {
        ValidateShift(count, 32);
        return value >> count;
    }

    public static int ShiftLeftUsize(int value, int count)
    {
        ValidateShift(count, 64);
        // Rust truncates shifted-out bits to the native usize width before the
        // executable profile checks whether the result fits its bounded ABI.
        return checked((int)((ulong)value << count));
    }

    public static int ShiftRightUsize(int value, int count)
    {
        ValidateShift(count, 64);
        return count >= 31 ? 0 : value >> count;
    }

    public static int AddUsize(int left, int right) => checked(left + right);
    public static int SubtractUsize(int left, int right) => ToUsize(checked(left - right));
    public static int MultiplyUsize(int left, int right) => checked(left * right);
    public static int ComplementUsize(int value) => throw new OverflowException(
        "The usize complement exceeds the bounded executable representation.");
    public static int ToUsize(int value) => value < 0
        ? throw new OverflowException("A negative integer converts outside the bounded executable usize representation.") : value;
    public static int BoolToInteger(bool value) => value ? 1 : 0;
    public static bool LessThanBool(bool left, bool right) => !left && right;
    public static bool GreaterThanBool(bool left, bool right) => left && !right;

    private static void ValidateShift(int count, int width)
    {
        if ((uint)count >= (uint)width) throw new OverflowException("Rust shift count is outside its operand width.");
    }
}
