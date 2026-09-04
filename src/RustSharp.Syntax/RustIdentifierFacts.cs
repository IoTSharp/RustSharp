using System.Text;

namespace RustSharp.Syntax;

internal static class RustIdentifierFacts
{
    public static bool IsIdentifierStart(char value)
    {
        return !char.IsSurrogate(value) && IsIdentifierStart(new Rune(value));
    }

    public static bool IsIdentifierStart(Rune value)
    {
        return value.Value == '_' || RustUnicodeIdentifierTables.IsXidStart(value.Value);
    }

    public static bool IsIdentifierContinue(char value)
    {
        return !char.IsSurrogate(value) && IsIdentifierContinue(new Rune(value));
    }

    public static bool IsIdentifierContinue(Rune value)
    {
        return RustUnicodeIdentifierTables.IsXidContinue(value.Value);
    }

    public static string Canonicalize(string identifier)
    {
        string value = identifier.StartsWith("r#", StringComparison.Ordinal) ? identifier[2..] : identifier;
        return value.IsNormalized(NormalizationForm.FormC)
            ? value
            : value.Normalize(NormalizationForm.FormC);
    }

    public static bool IsForbiddenRawIdentifier(string identifier) =>
        identifier is "r#crate" or "r#self" or "r#super" or "r#Self" or "r#_";
}
