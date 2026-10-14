using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RustSharp.ProcWireCandidate;

/// <summary>Stateless JSON contract checks only; no transport, host, filesystem capability or macro execution.</summary>
public static class Contract
{
    public const string Protocol = "rustsharp.proc-wire.candidate.v1";
    public const int MaximumFrameBytes = 65_536;
    public const int MaximumTokens = 256;
    public const int MaximumSpans = 256;
    public const int MaximumDocuments = 8;
    public const int MaximumDiagnostics = 16;
    public const int MaximumTokenBytes = 256;
    public const int MaximumDiagnosticBytes = 2_048;
    public const int MaximumDelimiterDepth = 16;
    public const int MaximumJsonDepth = 12;
    public const int MaximumOperations = 8_192;
    public const int TimeoutMilliseconds = 1_000;
    private static readonly string[] Capabilities = ["token-tree-v1", "span-v1", "diagnostics-v1", "cancellation-v1"];

    public static Result Validate(string json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json); cancellationToken.ThrowIfCancellationRequested();
        if (json.Length > MaximumFrameBytes || Encoding.UTF8.GetByteCount(json) > MaximumFrameBytes) return Invalid("RSPROC1090", "Frame byte budget exceeded.");
        var validator = new Validator(Stopwatch.StartNew(), cancellationToken);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaximumJsonDepth });
            JsonElement root = document.RootElement; validator.Tree(root, 0);
            validator.Properties(root, ["protocolVersion", "requestId", "direction", "kind", "capabilities", "documents", "spans", "tokens", "diagnostics"]);
            if (validator.Text(root, "protocolVersion", 64) != Protocol) return Invalid("RSPROC1002", "Unsupported exact wire version.");
            _ = validator.Text(root, "requestId", 64);
            string direction = validator.Text(root, "direction", 32), kind = validator.Text(root, "kind", 32);
            string expectedDirection = kind switch
            {
                "hello" or "expand" or "cancel" => "compiler-to-host",
                "ready" or "expanded" or "diagnostic" or "error" or "cancelled" => "host-to-compiler",
                _ => throw new FormatException("Unsupported envelope kind."),
            };
            validator.Require(direction == expectedDirection, "Envelope direction/kind mismatch.");
            JsonElement capabilities = validator.Array(root, "capabilities", 4);
            var seenCapabilities = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement value in capabilities.EnumerateArray())
            {
                validator.Step(); string capability = value.GetString() ?? throw new FormatException("Null capability.");
                if (!Capabilities.Contains(capability, StringComparer.Ordinal)) return Invalid("RSPROC1001", "Unsupported capability: " + capability);
                validator.Require(seenCapabilities.Add(capability), "Duplicate capability.");
            }
            bool handshake = kind is "hello" or "ready";
            validator.Require(handshake ? seenCapabilities.SetEquals(Capabilities) : seenCapabilities.Count == 0, "Capabilities are exact at handshake and empty in later envelopes.");
            var documents = new Dictionary<string, int>(StringComparer.Ordinal);
            JsonElement sourceDocuments = validator.Array(root, "documents", MaximumDocuments);
            foreach (JsonElement value in sourceDocuments.EnumerateArray())
            {
                validator.Step(); validator.Properties(value, ["id", "utf16Length"]);
                string id = validator.Text(value, "id", 64); int length = value.GetProperty("utf16Length").GetInt32();
                validator.Require(length is >= 0 and <= MaximumFrameBytes && documents.TryAdd(id, length), "Invalid or duplicate source document.");
            }
            JsonElement spans = validator.Array(root, "spans", MaximumSpans);
            foreach (JsonElement value in spans.EnumerateArray())
            {
                validator.Step(); validator.Properties(value, ["documentId", "start", "length"]);
                string id = validator.Text(value, "documentId", 64); int start = value.GetProperty("start").GetInt32(), length = value.GetProperty("length").GetInt32();
                validator.Require(documents.TryGetValue(id, out int size) && start >= 0 && length >= 0 && start <= size - length, "Span outside its declared UTF16 document bounds.");
            }
            JsonElement tokens = validator.Array(root, "tokens", MaximumTokens);
            var delimiters = new Stack<char>();
            foreach (JsonElement token in tokens.EnumerateArray())
            {
                validator.Step(); validator.Properties(token, ["kind", "text", "spanIndex"]);
                string tokenKind = validator.Text(token, "kind", 32), text = validator.Text(token, "text", MaximumTokenBytes);
                validator.Require(tokenKind is "identifier" or "literal" or "punctuation" or "open" or "close", "Unknown token kind.");
                int spanIndex = token.GetProperty("spanIndex").GetInt32(); validator.Require(spanIndex >= 0 && spanIndex < spans.GetArrayLength(), "Token span index is invalid.");
                if (tokenKind == "open")
                {
                    validator.Require(text.Length == 1 && text[0] is '(' or '[' or '{' && delimiters.Count < MaximumDelimiterDepth, "Open delimiter/depth limit is invalid.");
                    delimiters.Push(text[0]);
                }
                else if (tokenKind == "close")
                {
                    validator.Require(text.Length == 1 && text[0] is ')' or ']' or '}' && delimiters.Count > 0, "Unmatched close delimiter.");
                    char open = delimiters.Pop(); validator.Require((open, text[0]) is ('(', ')') or ('[', ']') or ('{', '}'), "Delimiter kind mismatch.");
                }
            }
            validator.Require(delimiters.Count == 0, "Unclosed delimiter tree.");
            JsonElement diagnostics = validator.Array(root, "diagnostics", MaximumDiagnostics);
            foreach (JsonElement diagnostic in diagnostics.EnumerateArray())
            {
                validator.Step(); validator.Properties(diagnostic, ["severity", "code", "message", "spanIndex"]);
                string severity = validator.Text(diagnostic, "severity", 32); validator.Require(severity is "error" or "warning" or "note", "Unknown diagnostic severity.");
                _ = validator.Text(diagnostic, "code", 64); _ = validator.Text(diagnostic, "message", MaximumDiagnosticBytes);
                int index = diagnostic.GetProperty("spanIndex").GetInt32(); validator.Require(index == -1 || index >= 0 && index < spans.GetArrayLength(), "Invalid diagnostic span index.");
                if (kind == "error") validator.Require(severity == "error", "Error envelope requires error diagnostics.");
            }
            bool expansion = kind is "expand" or "expanded", diagnosticEnvelope = kind is "diagnostic" or "error";
            validator.Require(expansion ? tokens.GetArrayLength() > 0 && documents.Count > 0 && spans.GetArrayLength() > 0 : tokens.GetArrayLength() == 0, "Token payload is inappropriate for envelope kind.");
            validator.Require(diagnosticEnvelope ? diagnostics.GetArrayLength() > 0 : diagnostics.GetArrayLength() == 0, "Diagnostic payload is inappropriate for envelope kind.");
            if (handshake || kind is "cancel" or "cancelled") validator.Require(documents.Count == 0 && spans.GetArrayLength() == 0, "Control envelope cannot carry source payload.");
            validator.Step(); return new(true, string.Empty, string.Empty, kind, tokens.GetArrayLength(), spans.GetArrayLength());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or OverflowException or TimeoutException)
        { return Invalid("RSPROC1090", exception.Message); }
    }
    private static Result Invalid(string code, string message) => new(false, code, message, string.Empty, 0, 0);
    private sealed class Validator(Stopwatch clock, CancellationToken cancellationToken)
    {
        private int _operations;
        internal void Step() { cancellationToken.ThrowIfCancellationRequested(); if (++_operations > MaximumOperations || clock.ElapsedMilliseconds >= TimeoutMilliseconds) throw new TimeoutException("Wire validation operation/deadline budget exceeded."); }
        internal void Require(bool condition, string message) { Step(); if (!condition) throw new FormatException(message); }
        internal string Text(JsonElement value, string name, int maximumBytes) { Step(); string? text = value.GetProperty(name).GetString(); Require(!string.IsNullOrWhiteSpace(text) && Encoding.UTF8.GetByteCount(text) <= maximumBytes, "Missing, empty or oversized text: " + name); return text!; }
        internal JsonElement Array(JsonElement value, string name, int maximum) { Step(); JsonElement array = value.GetProperty(name); Require(array.ValueKind == JsonValueKind.Array && array.GetArrayLength() <= maximum, "Array type/count limit: " + name); return array; }
        internal void Properties(JsonElement value, string[] names)
        {
            Step(); var actual = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject()) { Step(); Require(actual.Add(property.Name), "Duplicate JSON property."); }
            Require(actual.SetEquals(names), "Missing or unexpected JSON property.");
        }
        internal void Tree(JsonElement value, int depth)
        {
            Step(); Require(depth <= MaximumJsonDepth, "JSON nesting depth exceeded.");
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in value.EnumerateObject()) { Step(); Require(names.Add(property.Name), "Duplicate JSON property."); Tree(property.Value, depth + 1); }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                Require(value.GetArrayLength() <= MaximumTokens, "Generic array item budget exceeded.");
                foreach (JsonElement item in value.EnumerateArray()) Tree(item, depth + 1);
            }
        }
    }
}
public sealed record Result(bool IsSuccessful, string DiagnosticCode, string Message, string Kind, int TokenCount, int SpanCount)
{ public bool FormalAcceptanceAllowed { get; } public int MacroCodeExecutions { get; } }
