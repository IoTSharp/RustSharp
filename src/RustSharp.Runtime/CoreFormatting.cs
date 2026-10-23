using System.Globalization;
using System.Text;

namespace RustSharp.Runtime.Core;

public static class CoreFormatting
{
    public static string Display<T>(T value) => Format(value, "display", 1_048_576);
    public static string Debug<T>(T value) => Format(value, "debug", 1_048_576);

    public static string Format<T>(T value, string specifier, int maximumUtf8Bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specifier);
        if (maximumUtf8Bytes is < 1 or > 16_777_216) throw new ArgumentOutOfRangeException(nameof(maximumUtf8Bytes));
        string result = specifier switch
        {
            "display" => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            "debug" => value?.ToString() ?? "null",
            _ => throw new FormatException($"Unsupported core format specifier '{specifier}'."),
        };
        if (Encoding.UTF8.GetByteCount(result) > maximumUtf8Bytes)
            throw new InvalidOperationException("Formatted output exceeds the configured UTF-8 byte budget.");
        return result;
    }
}
