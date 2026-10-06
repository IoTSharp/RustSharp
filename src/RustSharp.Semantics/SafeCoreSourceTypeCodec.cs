using System.Diagnostics;
using System.Globalization;

namespace RustSharp.Semantics;

/// <summary>Canonical, bounded structural type identities at source-package boundaries.</summary>
public static class SafeCoreSourceTypeCodec
{
    public const int MaximumCharacters = 4096;
    public const int MaximumDepth = 32;
    public const int MaximumNodes = 256;

    /// <summary>Formats a supported closed source type without inferring its CLR representation.</summary>
    public static string Format(SafeCoreType type, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);
        var budget = new Budget(cancellationToken);
        Validate(type, 0, budget);
        string result = type.ToString();
        budget.Check();
        if (result.Length > MaximumCharacters)
            throw new ArgumentException("Source type identity exceeds its character budget.", nameof(type));
        return result;
    }

    /// <summary>Parses an exact canonical source type; unsupported or unclosed shapes reject.</summary>
    public static SafeCoreType Parse(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > MaximumCharacters)
            throw new ArgumentException("Source type identity exceeds its character budget.", nameof(text));
        var parser = new Parser(text, cancellationToken);
        SafeCoreType result = parser.ReadType(0);
        if (!parser.AtEnd || !string.Equals(result.ToString(), text, StringComparison.Ordinal))
            throw new ArgumentException("Source type identity is not canonical.", nameof(text));
        return result;
    }

    private static void Validate(SafeCoreType type, int depth, Budget budget)
    {
        budget.Step(depth);
        if (type.Kind is SafeCoreSemanticTypeKind.Unit or SafeCoreSemanticTypeKind.Bool or
            SafeCoreSemanticTypeKind.I32 or SafeCoreSemanticTypeKind.Usize)
            return;
        if (type.Kind == SafeCoreSemanticTypeKind.Adt)
        {
            if (type.Name is null || !type.Name.Contains("::", StringComparison.Ordinal) || !IsNominalName(type.Name))
                throw new ArgumentException("Source nominal identity must be a closed canonical path.");
            return;
        }
        if (type.Kind is not (SafeCoreSemanticTypeKind.Tuple or SafeCoreSemanticTypeKind.Array or
            SafeCoreSemanticTypeKind.Slice or SafeCoreSemanticTypeKind.Reference))
            throw new ArgumentException("Source type shape is outside the imported safe-core profile.");
        for (int index = 0; index < type.Elements.Count; index++)
        {
            budget.Check();
            Validate(type.Elements[index], depth + 1, budget);
        }
    }

    private static bool IsNominalName(string name)
    {
        if (name.Length is 0 or > MaximumCharacters) return false;
        bool segmentStart = true;
        for (int index = 0; index < name.Length; index++)
        {
            char value = name[index];
            if (value == ':' && !segmentStart && index + 1 < name.Length && name[index + 1] == ':')
            {
                index++;
                segmentStart = true;
                continue;
            }
            if (segmentStart ? !(char.IsLetter(value) || value == '_') :
                !(char.IsLetterOrDigit(value) || value == '_')) return false;
            segmentStart = false;
        }
        return !segmentStart;
    }

    private sealed class Budget(CancellationToken cancellationToken)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _nodes;

        public void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new ArgumentException("Source type codec exceeded its time budget.");
        }

        public void Step(int depth)
        {
            Check();
            if (depth >= MaximumDepth || ++_nodes > MaximumNodes)
                throw new ArgumentException("Source type identity exceeds its structural budget.");
        }
    }

    private sealed class Parser(string text, CancellationToken cancellationToken)
    {
        private readonly Budget _budget = new(cancellationToken);
        private int _position;
        public bool AtEnd => _position == text.Length;

        public SafeCoreType ReadType(int depth)
        {
            _budget.Step(depth);
            if (Take("&mut "))
                return SafeCoreType.Reference(ReadType(depth + 1), true, cancellationToken);
            if (Take("&"))
                return SafeCoreType.Reference(ReadType(depth + 1), false, cancellationToken);
            if (Take("("))
            {
                if (Take(")")) return SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Unit, cancellationToken);
                var elements = new List<SafeCoreType>();
                for (int index = 0; index < MaximumNodes; index++)
                {
                    _budget.Check();
                    elements.Add(ReadType(depth + 1));
                    if (Take(")"))
                    {
                        if (elements.Count == 1) throw Invalid();
                        return SafeCoreType.Tuple(elements, cancellationToken);
                    }
                    if (Take(",)"))
                    {
                        if (elements.Count != 1) throw Invalid();
                        return SafeCoreType.Tuple(elements, cancellationToken);
                    }
                    Require(", ");
                }
                throw Invalid();
            }
            if (Take("["))
            {
                SafeCoreType element = ReadType(depth + 1);
                if (Take("]")) return SafeCoreType.Slice(element, cancellationToken);
                Require("; ");
                int first = _position;
                for (int index = 0; index < MaximumCharacters && _position < text.Length; index++)
                {
                    _budget.Check();
                    if (text[_position] is < '0' or > '9') break;
                    _position++;
                }
                if (first == _position || !long.TryParse(text.AsSpan(first, _position - first),
                    NumberStyles.None, CultureInfo.InvariantCulture, out long length)) throw Invalid();
                Require("]");
                return SafeCoreType.Array(element, length, cancellationToken);
            }
            int start = _position;
            for (int index = 0; index < MaximumCharacters && _position < text.Length; index++)
            {
                _budget.Check();
                char value = text[_position];
                if (!(char.IsLetterOrDigit(value) || value is '_' or ':')) break;
                _position++;
            }
            if (start == _position) throw Invalid();
            string name = text[start.._position];
            return name switch
            {
                "i32" => SafeCoreType.Primitive(SafeCoreSemanticTypeKind.I32, cancellationToken),
                "bool" => SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Bool, cancellationToken),
                "usize" => SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Usize, cancellationToken),
                _ when IsNominalName(name) && name.Contains("::", StringComparison.Ordinal) =>
                    SafeCoreType.Adt(name, cancellationToken),
                _ => throw Invalid(),
            };
        }

        private bool Take(string value)
        {
            _budget.Check();
            if (!text.AsSpan(_position).StartsWith(value, StringComparison.Ordinal)) return false;
            _position += value.Length;
            return true;
        }

        private void Require(string value)
        {
            if (!Take(value)) throw Invalid();
        }

        private static ArgumentException Invalid() => new("Invalid or unsupported canonical source type identity.");
    }
}
