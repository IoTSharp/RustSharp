using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RustSharp.ProcWireCandidate;

namespace RustSharp.ProcWireBoundaries;

internal static class Program
{
    private sealed record Case(string Id,Action Run);
    public static int Main()
    {
        Case[] cases=
        [
            new("frame-utf8-bytes-65536-at",()=>Valid(Frame())),
            new("frame-utf8-bytes-65537-over",()=>{string at=Frame();Valid(at);Reject(at+" ","Frame byte budget");}),
            new("tokens-256-at",()=>Valid(Count("tokens",256).ToJsonString())),
            new("tokens-257-over",()=>CountOver("tokens",256,"Generic array item budget")),
            new("spans-256-at",()=>Valid(Count("spans",256).ToJsonString())),
            new("spans-257-over",()=>CountOver("spans",256,"Generic array item budget")),
            new("documents-8-at",()=>Valid(Count("documents",8).ToJsonString())),
            new("documents-9-over",()=>CountOver("documents",8,"documents")),
            new("diagnostics-16-at",()=>Valid(Count("diagnostics",16).ToJsonString())),
            new("diagnostics-17-over",()=>CountOver("diagnostics",16,"diagnostics")),
            new("token-text-utf8-256-at",()=>Valid(Text(false,256).ToJsonString())),
            new("token-text-utf8-257-over",()=>TextOver(false,256,"text")),
            new("diagnostic-text-utf8-2048-at",()=>Valid(Text(true,2048).ToJsonString())),
            new("diagnostic-text-utf8-2049-over",()=>TextOver(true,2048,"message")),
            new("delimiter-depth-16-at",()=>Valid(Delimiters(16).ToJsonString())),
            new("delimiter-depth-17-over",()=>{Valid(Delimiters(16).ToJsonString());Reject(Delimiters(17).ToJsonString(),"Open delimiter/depth");}),
            new("span-exact-document-end-at",()=>Valid(SpanEnd(false).ToJsonString())),
            new("span-above-document-end-over",()=>{Valid(SpanEnd(false).ToJsonString());Reject(SpanEnd(true).ToJsonString(),"Span outside");}),
            new("json-container-depth-13-parser-reject-valid-seed-first",JsonDepth),
        ];
        if(cases.Length!=19)throw new InvalidOperationException("Fixed boundary denominator changed.");
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));var clock=Stopwatch.StartNew();int passed=0,failed=0;
        Valid(Seed().ToJsonString()); // Tiny unchanged timeout-bounded trial, not a rerun of the historical 24-case batch.
        for(int index=0;index<cases.Length && index<19;index++)
        {
            deadline.Token.ThrowIfCancellationRequested();if(clock.ElapsedMilliseconds>=15_000)throw new TimeoutException("Boundary harness deadline.");
            try{cases[index].Run();passed++;Report(cases[index].Id,"PASS",null);}
            catch(Exception exception)when(exception is InvalidOperationException or TimeoutException){failed++;Report(cases[index].Id,"FAIL",exception.Message);}
        }
        using var buffer=new MemoryStream();using(var writer=new Utf8JsonWriter(buffer)){writer.WriteStartObject();writer.WriteNumber("denominator",19);writer.WriteNumber("passed",passed);writer.WriteNumber("failed",failed);writer.WriteNumber("skipped",0);writer.WriteNumber("historicalCasesRerun",0);writer.WriteNumber("hostProcesses",0);writer.WriteNumber("macroCodeExecutions",0);writer.WriteBoolean("formalAcceptanceAllowed",false);writer.WriteEndObject();writer.Flush();}Console.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));return failed==0?0:1;
    }
    private static JsonObject Seed(bool diagnostic=false)=>new()
    {
        ["protocolVersion"]=Contract.Protocol,["requestId"]="request-boundary",["direction"]=diagnostic?"host-to-compiler":"compiler-to-host",["kind"]=diagnostic?"diagnostic":"expand",["capabilities"]=new JsonArray(),
        ["documents"]=new JsonArray(new JsonObject{["id"]="doc0",["utf16Length"]=3}),
        ["spans"]=new JsonArray(new JsonObject{["documentId"]="doc0",["start"]=0,["length"]=1}),
        ["tokens"]=diagnostic?new JsonArray():new JsonArray(Token("identifier","x")),
        ["diagnostics"]=diagnostic?new JsonArray(Diagnostic("message")):new JsonArray(),
    };
    private static JsonObject Token(string kind,string text)=>new(){["kind"]=kind,["text"]=text,["spanIndex"]=0};
    private static JsonObject Diagnostic(string message)=>new(){["severity"]="note",["code"]="BOUNDARY",["message"]=message,["spanIndex"]=0};
    private static JsonObject Count(string dimension,int count)
    {
        if(count is <=0 or >257)throw new InvalidOperationException("Builder count cap.");
        JsonObject root=Seed(dimension=="diagnostics");var array=new JsonArray();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(2));var clock=Stopwatch.StartNew();
        for(int index=0;index<count && index<257;index++)
        {
            deadline.Token.ThrowIfCancellationRequested();if(clock.ElapsedMilliseconds>=2000)throw new TimeoutException("Boundary builder deadline.");
            JsonObject item=dimension switch
            {
                "tokens"=>Token("identifier","x"),
                "spans"=>new(){["documentId"]="doc0",["start"]=0,["length"]=1},
                "documents"=>new(){["id"]="doc"+index,["utf16Length"]=3},
                "diagnostics"=>Diagnostic("message"),
                _=>throw new InvalidOperationException("Unknown builder dimension."),
            };array.Add((JsonNode)item);
        }
        root[dimension]=array;return root;
    }
    private static void CountOver(string dimension,int limit,string expected){Valid(Count(dimension,limit).ToJsonString());Reject(Count(dimension,limit+1).ToJsonString(),expected);}
    private static string Frame()
    {
        string prefix=Seed().ToJsonString().Replace("request-boundary","中",StringComparison.Ordinal);
        string result=prefix+new string(' ',Contract.MaximumFrameBytes-Encoding.UTF8.GetByteCount(prefix));
        Require(Encoding.UTF8.GetByteCount(result)==65536 && result.Length<65536,"Frame must exercise UTF8 bytes independently from UTF16 length.");return result;
    }
    private static JsonObject Text(bool diagnostic,int bytes)
    {
        string value=new string('é',bytes/2)+(bytes%2==0?"":"x");Require(Encoding.UTF8.GetByteCount(value)==bytes,"Exact multibyte fixture length.");JsonObject root=Seed(diagnostic);
        if(diagnostic)root["diagnostics"]![0]!["message"]=value;else root["tokens"]![0]!["text"]=value;return root;
    }
    private static void TextOver(bool diagnostic,int limit,string expected){Valid(Text(diagnostic,limit).ToJsonString());Reject(Text(diagnostic,limit+1).ToJsonString(),expected);}
    private static JsonObject Delimiters(int depth)
    {
        Require(depth is >0 and <=17,"Delimiter fixture cap.");var root=Seed();var tokens=new JsonArray();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(2));var clock=Stopwatch.StartNew();
        for(int index=0;index<depth && index<17;index++){deadline.Token.ThrowIfCancellationRequested();if(clock.ElapsedMilliseconds>=2000)throw new TimeoutException("Delimiter builder deadline.");tokens.Add((JsonNode)Token("open","("));}
        tokens.Add((JsonNode)Token("identifier","x"));
        for(int index=0;index<depth && index<17;index++){deadline.Token.ThrowIfCancellationRequested();if(clock.ElapsedMilliseconds>=2000)throw new TimeoutException("Delimiter builder deadline.");tokens.Add((JsonNode)Token("close",")"));}
        root["tokens"]=tokens;return root;
    }
    private static JsonObject SpanEnd(bool over){JsonObject root=Seed();root["spans"]![0]!["start"]=over?3:2;root["spans"]![0]!["length"]=1;return root;}
    private static void JsonDepth()
    {
        JsonObject root=Seed();Valid(root.ToJsonString());JsonNode nested=JsonValue.Create("x")!;using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(2));var clock=Stopwatch.StartNew();
        for(int index=0;index<10;index++){deadline.Token.ThrowIfCancellationRequested();if(clock.ElapsedMilliseconds>=2000)throw new TimeoutException("JSON nesting builder deadline.");nested=new JsonArray(nested);}
        root["tokens"]![0]!["text"]=nested;
        // Root object + tokens array + token object + ten arrays = thirteen containers.
        // No legal envelope has depth twelve: its fixed schema has no recursive JSON value slots.
        string json=root.ToJsonString();
        using(JsonDocument allowed=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=13})){Require(allowed.RootElement.ValueKind==JsonValueKind.Object,"Depth fixture is otherwise syntactically valid JSON.");}
        string? parserMessage=null;
        try{using JsonDocument rejected=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=12});}
        catch(JsonException exception){parserMessage=exception.Message;}
        Require(parserMessage is not null,"Independent parser must reject this JSON at exactly MaxDepth12.");
        Result result=Contract.Validate(json);
        Require(!result.IsSuccessful && result.DiagnosticCode=="RSPROC1090" && result.Message==parserMessage && result.Kind.Length==0 && result.TokenCount==0 && result.SpanCount==0,
            "Contract rejection must preserve the actual independent MaxDepth12 JsonException message, independent of UI language.");
    }
    private static void Valid(string json){Result result=Contract.Validate(json);Require(result.IsSuccessful,"At-limit message must actually validate: "+result.DiagnosticCode+" "+result.Message);Require(!result.FormalAcceptanceAllowed && result.MacroCodeExecutions==0,"Contract checks grant no execution or acceptance.");}
    private static void Reject(string json,string expected){Result result=Contract.Validate(json);Require(!result.IsSuccessful && result.Kind.Length==0 && result.TokenCount==0 && result.SpanCount==0,"Over-limit message must fail without partial result.");Require(result.Message.Contains(expected,StringComparison.OrdinalIgnoreCase),"Expected independent dimension rejection containing '"+expected+"', got: "+result.Message);}
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static void Report(string id,string status,string? error){using var buffer=new MemoryStream();using(var writer=new Utf8JsonWriter(buffer)){writer.WriteStartObject();writer.WriteString("id",id);writer.WriteString("status",status);if(error is not null)writer.WriteString("error",error);writer.WriteEndObject();writer.Flush();}Console.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));}
}
