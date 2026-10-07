using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RustSharp.Syntax;

namespace RustSharp.Compiler;

internal sealed class DotNetBindingBudget(CancellationToken cancellationToken)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _operations;
    internal CancellationToken CancellationToken => cancellationToken;
    internal TimeSpan RemainingTime
    {
        get
        {
            Step(0);
            TimeSpan remaining = TimeSpan.FromSeconds(10) - _clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new DotNetBindingFailure("RSDN1001", "Import binding exceeded its ten-second bound.", default);
            return remaining;
        }
    }
    public void Step(int amount = 1)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _operations = checked(_operations + amount);
        if (_operations > 200_000 || _clock.ElapsedMilliseconds > 10_000)
            throw new DotNetBindingFailure("RSDN1001", "Import binding exceeded its 200000-operation/10-second bound.", default);
    }
}

internal sealed class DotNetBindingFailure(string code, string message, TextSpan span) : Exception(message)
{
    public string Code { get; } = code;
    public TextSpan Span { get; } = span;
}

/// <summary>Consumes the committed attribute/extern grammar through the production Rust lexer.</summary>
internal sealed class DotNetImportParser
{
    private readonly IReadOnlyList<RustToken> _tokens;
    private readonly DotNetBindingBudget _budget;
    private readonly int _sourceLength;
    private int _position;
    private const int MaximumSourceBytes = 262_144;

    public DotNetImportParser(string source, string sourcePath, DotNetBindingBudget budget)
    {
        _budget = budget;
        _sourceLength = source.Length;
        budget.Step();
        if (source.Length > MaximumSourceBytes || Encoding.UTF8.GetByteCount(source) > MaximumSourceBytes)
            Fail("RSDN1001", "Import source exceeds 262144 UTF-8 bytes.", default);
        RustLexResult lex = RustLexer.Lex(source, sourcePath, new RustLexerOptions
        {
            Timeout = budget.RemainingTime, MaximumSourceLength = MaximumSourceBytes, MaximumTokens = 65_536,
            MaximumTrivia = 65_536, MaximumDelimiterDepth = 32, MaximumDiagnostics = 1,
        }, budget.CancellationToken);
        budget.Step();
        if (lex.Diagnostics.Count != 0)
            Fail("RSDN1001", "Malformed import source: " + lex.Diagnostics[0].Message, lex.Diagnostics[0].Span);
        _tokens = lex.Tokens;
    }

    public ImmutableArray<DotNetImportDeclaration> Parse()
    {
        var declarations = ImmutableArray.CreateBuilder<DotNetImportDeclaration>();
        // Every token advances exactly once; source/tokens/declarations and the shared wall clock are bounded.
        for (int count = 0; _position < _tokens.Count && count < 257; count++)
        {
            _budget.Step();
            if (count == 256) Fail("RSDN1001", "At most 256 imports are permitted.", CurrentSpan);
            int start = CurrentSpan.Start;
            Expect("#"); Expect("["); Expect("dotnet_import"); Expect("(");
            var attributes = new Dictionary<string, (string Value, TextSpan Span)>(StringComparer.Ordinal);
            for (int item = 0; !At(")") && item < 5; item++)
            {
                string name;
                if (Take("type")) name = "type";
                else name = Identifier();
                Expect("=");
                TextSpan span = CurrentSpan;
                string value = String();
                if (name is not ("assembly" or "type" or "member" or "signature") || !attributes.TryAdd(name, (value, span)))
                    Fail("RSDN1001", "Unknown or duplicated dotnet_import identity field.", span);
                if (!Take(",")) break;
            }
            Expect(")"); Expect("]"); Expect("extern");
            TextSpan abiSpan = CurrentSpan;
            if (String() != "dotnet") Fail("RSDN1010", "Only the explicit dotnet ABI is supported.", abiSpan);
            Expect("{"); Take("pub"); Expect("fn");
            TextSpan aliasSpan = CurrentSpan;
            string alias = Identifier(); Expect("(");
            var parameters = ImmutableArray.CreateBuilder<string>();
            for (int index = 0; !At(")") && index < 257; index++)
            {
                if (index == 256) Fail("RSDN1005", "At most 256 boundary parameters are permitted.", CurrentSpan);
                Identifier(); Expect(":"); parameters.Add(Type(returnPosition: false));
                if (!Take(",")) break;
            }
            Expect(")");
            string result = "System.Void";
            if (Take("->")) result = Type(returnPosition: true);
            Expect(";"); Expect("}");
            string assembly = Required("assembly");
            string type = Required("type");
            string member = Required("member");
            if (!MetadataName(assembly, allowDot: true) || !MetadataName(type, allowDot: true))
                Fail("RSDN1001", "Invalid assembly/type metadata identity.", attributes["type"].Span);
            attributes.TryGetValue("signature", out var signature);
            declarations.Add(new(alias, assembly, type, member, signature.Value, parameters.ToImmutable(), result,
                new(start, PreviousEnd - start), aliasSpan, attributes["member"].Span, signature.Span));

            string Required(string key)
            {
                if (!attributes.TryGetValue(key, out var field) || string.IsNullOrWhiteSpace(field.Value))
                    Fail("RSDN1001", "Missing managed identity field: " + key, new(start, PreviousEnd - start));
                return field.Value;
            }
        }
        if (declarations.Count == 0) Fail("RSDN1001", "At least one explicit import declaration is required.", default);
        return declarations.ToImmutable();
    }

    private string Type(bool returnPosition, int depth = 0)
    {
        _budget.Step();
        TextSpan anchor = CurrentSpan;
        if (depth >= 32) Fail("RSDN1005", "Boundary type nesting exceeds 32 levels.", anchor);
        if (Take("*")) Fail("RSDN1010", "Unmanaged pointers are excluded.", anchor);
        if (At("fn") || At("extern") || At("...")) Fail("RSDN1010", "Callbacks and varargs are excluded.", anchor);
        if (Take("(")) { Expect(")"); if (returnPosition) return "System.Void"; Fail("RSDN1005", "Unit is return-only.", anchor); }
        bool loan = Take("&");
        if (loan) Take("mut");
        string name = Identifier();
        if (name == "Option")
        {
            Expect("<"); string nullable = Type(false, depth + 1); Expect(">");
            if (!nullable.StartsWith("InteropFixtures.", StringComparison.Ordinal))
                Fail("RSDN1005", "Option requires the declared managed handle adapter.", anchor);
            return nullable;
        }
        if (name == "dotnet")
        {
            Expect("::"); name = Identifier();
            if (name == "String" && !loan) return "System.String";
            if (name != "Object") Fail("RSDN1005", "Unknown dotnet source type.", anchor);
            Expect("<"); string managed = Identifier();
            for (int part = 0; Take("::") && part < 32; part++) managed += "." + Identifier();
            Expect(">"); return managed;
        }
        if (loan) Fail("RSDN1005", "Only managed handle loans are supported by this boundary.", anchor);
        return name switch
        {
            "i32" => "System.Int32", "bool" => "System.Boolean", "Pair" => "RustSharp.Interop.Pair",
            _ => throw new DotNetBindingFailure("RSDN1005", "Unsupported Rust boundary type: " + name, anchor),
        };
    }

    internal static bool MetadataName(string name, bool allowDot)
    {
        if (name.Length is 0 or > 512) return false;
        string[] parts = allowDot ? name.Split('.') : [name];
        return parts.Length <= 32 && parts.All(part => part.Length > 0 &&
            (char.IsAsciiLetter(part[0]) || part[0] == '_') && part.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'));
    }
    private string Identifier()
    {
        _budget.Step();
        if (_position >= _tokens.Count || _tokens[_position].Kind != RustTokenKind.Identifier)
            Fail("RSDN1001", "Expected an identifier.", CurrentSpan);
        return _tokens[_position++].Text;
    }
    private string String()
    {
        _budget.Step();
        RustToken? token = _position < _tokens.Count ? _tokens[_position] : null;
        if (token is null || token.Kind != RustTokenKind.StringLiteral)
            Fail("RSDN1001", "Expected a quoted identity string.", CurrentSpan);
        _position++;
        try
        {
            using JsonDocument document = JsonDocument.Parse(token!.Text, new JsonDocumentOptions { MaxDepth = 2 });
            if (document.RootElement.ValueKind != JsonValueKind.String) throw new JsonException();
            return document.RootElement.GetString() ?? throw new JsonException();
        }
        catch (JsonException) { throw new DotNetBindingFailure("RSDN1001", "Invalid identity string escape.", token!.Span); }
    }
    private bool At(string text) => _position < _tokens.Count && _tokens[_position].Text == text;
    private bool Take(string text) { _budget.Step(); if (!At(text)) return false; _position++; return true; }
    private void Expect(string text) { if (!Take(text)) Fail("RSDN1001", "Expected '" + text + "'.", CurrentSpan); }
    private TextSpan CurrentSpan => _position < _tokens.Count ? _tokens[_position].Span : new(_sourceLength, 0);
    private int PreviousEnd => _position > 0 ? _tokens[_position - 1].Span.End : 0;
    private static void Fail(string code, string message, TextSpan span) => throw new DotNetBindingFailure(code, message, span);
}
