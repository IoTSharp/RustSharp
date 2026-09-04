using System.Globalization;
using System.Text;

namespace RustSharp.Syntax;

internal static class RustIdentifierFacts
{
    public static bool IsIdentifierStart(char value)
    {
        if (value == '_')
        {
            return true;
        }

        UnicodeCategory category = char.GetUnicodeCategory(value);
        return category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or
            UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber;
    }

    public static bool IsIdentifierContinue(char value)
    {
        if (IsIdentifierStart(value) || char.IsDigit(value))
        {
            return true;
        }

        UnicodeCategory category = char.GetUnicodeCategory(value);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.ConnectorPunctuation;
    }

    public static string Canonicalize(string identifier)
    {
        string value = identifier.StartsWith("r#", StringComparison.Ordinal) ? identifier[2..] : identifier;
        return value.IsNormalized(NormalizationForm.FormC)
            ? value
            : value.Normalize(NormalizationForm.FormC);
    }

    public static bool IsForbiddenRawIdentifier(string identifier) =>
        identifier is "r#crate" or "r#self" or "r#super" or "r#Self";
}
