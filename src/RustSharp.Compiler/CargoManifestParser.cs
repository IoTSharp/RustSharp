using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Compiler;

/// <summary>A position-preserving parser for the deliberately closed, single-physical-line cargo-v1 TOML subset.</summary>
internal sealed class CargoManifestParser(string path, string text, CargoLoadBudget budget, bool lockFormat = false)
{
    private readonly List<CargoTomlTable> _tables = [];
    private readonly HashSet<string> _declaredTables = new(StringComparer.Ordinal);
    private CargoTomlTable? _table;
    private string _line = string.Empty;
    private int _lineStart;
    private int _position;

    internal CargoParsedManifest Parse()
    {
        if (lockFormat)
        {
            _table = new(Array.Empty<string>(), default, false);
            _tables.Add(_table);
        }
        for (int start = 0; start < text.Length;)
        {
            budget.Step(path, new(start, 0));
            int newline = text.IndexOf('\n', start);
            int end = newline < 0 ? text.Length : newline;
            if (end > start && text[end - 1] == '\r') end--;
            _lineStart = start;
            _line = text[start..end];
            _position = start == 0 && _line.StartsWith('\ufeff') ? 1 : 0;
            SkipSpace();
            if (!AtEnd && Current != '#')
            {
                if (Current == '[') ReadHeader();
                else ReadAssignment();
                SkipSpace();
                if (!AtEnd && Current != '#') Fail(CargoWorkspace.ManifestDiagnostic, "Unexpected text after a TOML declaration.", RemainingSpan());
            }
            if (!AtEnd && Current == '#')
            {
                for (; _position < _line.Length; _position++)
                    if ((_position & 255) == 0) budget.Check(path, Span(_position, 1));
            }
            start = newline < 0 ? text.Length : newline + 1;
        }
        budget.Check(path, new(text.Length, 0));
        return new(path, _tables.AsReadOnly());
    }

    private void ReadHeader()
    {
        int start = _position++;
        bool array = !AtEnd && Current == '[';
        if (array) _position++;
        var components = new List<string>();
        var componentSpans = new List<TextSpan>();
        for (int component = 0; component < 8; component++)
        {
            SkipSpace();
            if (AtEnd || Current == ']') Fail(CargoWorkspace.ManifestDiagnostic, "A TOML table requires a name.", Span(start, _line.Length - start));
            (string key, TextSpan keySpan) = ReadKey(allowDotted: true);
            components.Add(key); componentSpans.Add(keySpan);
            SkipSpace();
            if (AtEnd) Fail(CargoWorkspace.ManifestDiagnostic, "Unterminated TOML table header.", Span(start, _line.Length - start));
            if (Current == '.') { _position++; continue; }
            break;
        }
        if (AtEnd || Current != ']') Fail(CargoWorkspace.ManifestDiagnostic, "Malformed TOML table header.", Span(start, _line.Length - start));
        _position++;
        if (array)
        {
            if (AtEnd || Current != ']') Fail(CargoWorkspace.ManifestDiagnostic, "Malformed array table header.", Span(start, _line.Length - start));
            _position++;
        }
        TextSpan headerSpan = Span(start, _position - start);
        if (array && (components.Count != 1 || components[0] != (lockFormat ? "package" : "bin")))
            Fail(CargoWorkspace.UnsupportedManifestDiagnostic, lockFormat ? "Only [[package]] array tables are admitted in cargo-v1 locks." : "Only [[bin]] array tables are admitted in cargo-v1 manifests.", headerSpan);
        string identity = string.Join('\0', components);
        if (!array && !_declaredTables.Add(identity))
            Fail(CargoWorkspace.ManifestDiagnostic, "Duplicate singleton TOML table.", headerSpan);
        _table = new(components.AsReadOnly(), headerSpan, array) { PathSpans = componentSpans.AsReadOnly() };
        _tables.Add(_table);
        budget.Step(path, headerSpan);
        SkipSpace();
        if (!AtEnd && Current != '#')
            Fail(CargoWorkspace.ManifestDiagnostic, "Malformed TOML table header.", Span(start, _line.Length - start));
    }

    private void ReadAssignment()
    {
        (string key, TextSpan keySpan) = ReadKey(allowDotted: false);
        if (_table is null) Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Manifest assignments must be inside an admitted table.", keySpan);
        SkipSpace();
        if (!AtEnd && Current == '.')
            Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Dotted assignments are outside cargo-v1.", keySpan);
        if (AtEnd || Current != '=') Fail(CargoWorkspace.ManifestDiagnostic, "A TOML assignment requires equals.", RemainingSpan());
        _position++;
        SkipSpace();
        CargoTomlValue value = ReadValue(allowInline: true);
        if (!_table!.Entries.TryAdd(key, new(key, keySpan, value)))
            Fail(CargoWorkspace.ManifestDiagnostic, "Duplicate scalar TOML key.", keySpan);
    }

    private (string Key, TextSpan Span) ReadKey(bool allowDotted)
    {
        int start = _position;
        budget.Step(path, Span(start, 0));
        if (!AtEnd && Current is '"' or '\'')
        {
            CargoTomlValue value = ReadString();
            return ((string)value.Value, value.Span);
        }
        for (; _position < _line.Length && (char.IsAsciiLetterOrDigit(Current) || Current is '_' or '-'); _position++)
            if ((_position & 255) == 0) budget.Check(path, Span(_position, 1));
        if (_position == start) Fail(CargoWorkspace.ManifestDiagnostic, "Invalid TOML key.", Span(start, AtEnd ? 0 : 1));
        if (!allowDotted && !AtEnd && Current == '.')
        {
            int end = _position;
            for (; end < _line.Length && _line[end] != '=' && !char.IsWhiteSpace(_line[end]); end++)
                if ((end & 255) == 0) budget.Check(path, Span(end, 1));
            Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Dotted assignments are outside cargo-v1.", Span(start, end - start));
        }
        return (_line[start.._position], Span(start, _position - start));
    }

    private CargoTomlValue ReadValue(bool allowInline)
    {
        int start = _position;
        budget.Step(path, Span(start, 0));
        if (AtEnd || Current == '#') Fail(CargoWorkspace.ManifestDiagnostic, "A TOML assignment requires a value.", Span(start, 0));
        if (Current is '"' or '\'') return ReadString();
        if (Current == '[') return ReadArray();
        if (Current == '{')
        {
            if (!allowInline) Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Nested inline tables are outside cargo-v1.", RemainingSpan());
            return ReadInline();
        }
        for (; _position < _line.Length && Current is not ',' and not '}' and not ']' and not '#' && !char.IsWhiteSpace(Current); _position++)
            if ((_position & 255) == 0) budget.Check(path, Span(_position, 1));
        string token = _line[start.._position];
        if (token is "true" or "false") return new(token == "true", Span(start, _position - start));
        if (lockFormat && token == "4") return new(4, Span(start, _position - start));
        Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Only strings, booleans, single-line string arrays and flat dependency tables are admitted.", Span(start, _position - start));
        throw new InvalidOperationException();
    }

    private CargoTomlValue ReadString()
    {
        int start = _position;
        char quote = Current;
        if (_position + 2 < _line.Length && _line[_position + 1] == quote && _line[_position + 2] == quote)
            Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Multiline strings are outside cargo-v1.", RemainingSpan());
        _position++;
        var value = new StringBuilder();
        for (; _position < _line.Length;)
        {
            if ((_position & 255) == 0) budget.Check(path, Span(_position, 1));
            char character = _line[_position++];
            if (character == quote) return new(value.ToString(), Span(start, _position - start));
            if (character < ' ' && character != '\t' || character == '\u007f')
                Fail(CargoWorkspace.ManifestDiagnostic, "Control characters are invalid in a TOML string.", Span(_position - 1, 1));
            if (quote == '"' && character == '\\')
            {
                if (AtEnd) Fail(CargoWorkspace.ManifestDiagnostic, "Unterminated TOML string escape.", Span(start, _line.Length - start));
                char escaped = _line[_position++];
                if (escaped is 'u' or 'U')
                {
                    int digits = escaped == 'u' ? 4 : 8;
                    if (_position + digits > _line.Length) Fail(CargoWorkspace.ManifestDiagnostic, "Incomplete Unicode escape.", Span(start, _line.Length - start));
                    uint scalar = 0;
                    for (int digit = 0; digit < digits; digit++)
                    {
                        char hex = _line[_position++];
                        int number = hex is >= '0' and <= '9' ? hex - '0' : hex is >= 'a' and <= 'f' ? hex - 'a' + 10 : hex is >= 'A' and <= 'F' ? hex - 'A' + 10 : -1;
                        if (number < 0) Fail(CargoWorkspace.ManifestDiagnostic, "Invalid Unicode escape.", Span(_position - 1, 1));
                        scalar = scalar * 16 + (uint)number;
                    }
                    if (scalar > 0x10ffff || scalar is >= 0xd800 and <= 0xdfff)
                        Fail(CargoWorkspace.ManifestDiagnostic, "Invalid Unicode scalar.", Span(_position - digits, digits));
                    value.Append(char.ConvertFromUtf32((int)scalar));
                }
                else
                {
                    value.Append(escaped switch
                    {
                        'b' => '\b', 't' => '\t', 'n' => '\n', 'f' => '\f', 'r' => '\r', '"' => '"', '\\' => '\\',
                        _ => InvalidEscape(escaped),
                    });
                }
            }
            else value.Append(character);
        }
        Fail(CargoWorkspace.ManifestDiagnostic, "Unterminated TOML string.", Span(start, _line.Length - start));
        throw new InvalidOperationException();
    }

    private char InvalidEscape(char character)
    {
        Fail(CargoWorkspace.ManifestDiagnostic, $"Invalid TOML string escape '{character}'.", Span(_position - 2, 2));
        return default;
    }

    private CargoTomlValue ReadArray()
    {
        int start = _position++;
        var values = new List<CargoTomlValue>();
        for (int item = 0; item <= 20_000; item++)
        {
            budget.Step(path, Span(_position, 0));
            SkipSpace();
            if (AtEnd || Current == '#') Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Multiline arrays are outside cargo-v1.", Span(start, _line.Length - start));
            if (Current == ']') { _position++; return new(values.AsReadOnly(), Span(start, _position - start)); }
            if (Current is not '"' and not '\'') Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Array entries must be strings.", UnsupportedArraySpan(start));
            values.Add(ReadString());
            SkipSpace();
            if (AtEnd || Current == '#') Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Multiline arrays are outside cargo-v1.", Span(start, _line.Length - start));
            if (Current == ']') { _position++; return new(values.AsReadOnly(), Span(start, _position - start)); }
            if (Current != ',') Fail(CargoWorkspace.ManifestDiagnostic, "Expected a comma in TOML array.", Span(_position, 1));
            _position++;
        }
        Fail(CargoWorkspace.LimitDiagnostic, "TOML array exceeded the operation bound.", Span(start, _position - start));
        throw new InvalidOperationException();
    }

    private CargoTomlValue ReadInline()
    {
        int start = _position++;
        var entries = new Dictionary<string, CargoTomlEntry>(StringComparer.Ordinal);
        for (int item = 0; item <= 20_000; item++)
        {
            budget.Step(path, Span(_position, 0));
            SkipSpace();
            if (AtEnd || Current == '#') Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Multiline inline tables are outside cargo-v1.", Span(start, _line.Length - start));
            if (Current == '}') { _position++; return new(entries, Span(start, _position - start)); }
            (string key, TextSpan keySpan) = ReadKey(allowDotted: false);
            SkipSpace();
            if (AtEnd || Current != '=') Fail(CargoWorkspace.ManifestDiagnostic, "Expected equals in inline table.", RemainingSpan());
            _position++;
            SkipSpace();
            CargoTomlValue value = ReadValue(allowInline: false);
            if (!entries.TryAdd(key, new(key, keySpan, value))) Fail(CargoWorkspace.ManifestDiagnostic, "Duplicate inline key.", keySpan);
            SkipSpace();
            if (AtEnd) Fail(CargoWorkspace.ManifestDiagnostic, "Unterminated inline table.", Span(start, _line.Length - start));
            if (Current == '}') { _position++; return new(entries, Span(start, _position - start)); }
            if (Current != ',') Fail(CargoWorkspace.ManifestDiagnostic, "Expected comma in inline table.", Span(_position, 1));
            _position++;
            SkipSpace();
            if (!AtEnd && Current == '}') Fail(CargoWorkspace.ManifestDiagnostic, "Trailing inline-table commas are invalid.", Span(_position - 1, 1));
        }
        Fail(CargoWorkspace.LimitDiagnostic, "Inline table exceeded the operation bound.", Span(start, _position - start));
        throw new InvalidOperationException();
    }

    private void SkipSpace()
    {
        for (; _position < _line.Length && Current is ' ' or '\t'; _position++)
            if ((_position & 255) == 0) budget.Check(path, Span(_position, 1));
    }

    private bool AtEnd => _position >= _line.Length;
    private char Current => _line[_position];
    private TextSpan Span(int start, int length) => new(_lineStart + start, length);
    private TextSpan RemainingSpan() => Span(_position, _line.Length - _position);
    private TextSpan UnsupportedArraySpan(int start)
    {
        int depth = 0;
        char quote = default;
        bool escaped = false;
        for (int index = start; index < _line.Length; index++)
        {
            if ((index & 255) == 0) budget.Check(path, Span(index, 1));
            char character = _line[index];
            if (quote != default)
            {
                if (escaped) escaped = false;
                else if (quote == '"' && character == '\\') escaped = true;
                else if (character == quote) quote = default;
            }
            else if (character is '"' or '\'') quote = character;
            else if (character == '[') depth++;
            else if (character == ']' && --depth == 0) return Span(start, index - start + 1);
        }
        return Span(start, _line.Length - start);
    }
    private void Fail(string code, string message, TextSpan span) => throw new CargoLoadException(code, message, path, span);
}
