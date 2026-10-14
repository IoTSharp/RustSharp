using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RustSharp.ProcWireCandidate;

internal static class Program
{
    private sealed record Case(string Id, Action Run);
    public static int Main()
    {
        Case[] cases =
        [
            new("hello-positive", () => Valid("hello")), new("ready-positive", () => Valid("ready")),
            new("expand-positive", () => Valid("expand")), new("expanded-positive", () => Valid("expanded")),
            new("diagnostic-positive", () => Valid("diagnostic")), new("error-positive", () => Valid("error")),
            new("cancel-positive", () => Valid("cancel")), new("cancelled-positive", () => Valid("cancelled")),
            new("empty-request-reject", () => Mutation("expand", root => root["requestId"]="")),
            new("version-reject", () => Mutation("expand", root => root["protocolVersion"]="v2")),
            new("direction-reject", () => Mutation("expand", root => root["direction"]="host-to-compiler")),
            new("kind-reject", () => Mutation("expand", root => root["kind"]="execute")),
            new("unknown-capability-reject", () => Mutation("hello", root => root["capabilities"]![0]="filesystem-read")),
            new("duplicate-capability-reject", () => Mutation("hello", root => root["capabilities"]![0]="span-v1")),
            new("unknown-field-reject", () => Mutation("expand", root => root["trustAllHostOutput"]=true)),
            new("duplicate-json-field-reject", DuplicateField),
            new("span-overflow-reject", () => Mutation("expand", root => root["spans"]![0]!["start"]=int.MaxValue)),
            new("negative-span-reject", () => Mutation("expand", root => root["spans"]![0]!["length"]=-1)),
            new("missing-document-reject", () => Mutation("expand", root => root["spans"]![0]!["documentId"]="missing")),
            new("frame-byte-limit-reject", () => Invalid(new string(' ',Contract.MaximumFrameBytes+1))),
            new("token-byte-limit-reject", () => Mutation("expand", root => root["tokens"]![0]!["text"]=new string('x',257))),
            new("null-diagnostic-reject", () => Mutation("diagnostic", root => root["diagnostics"]![0]!["message"]=null)),
            new("caller-cancellation-propagates", Cancellation),
            new("unknown-token-field-reject", () => Mutation("expand", root => root["tokens"]![0]!["execute"]=true)),
        ];
        if(cases.Length!=24) throw new InvalidOperationException("Fixed test denominator changed.");
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10)); var clock=Stopwatch.StartNew(); int passed=0,failed=0;
        // Tiny unchanged seed trial precedes the finite batch; it is not an extra counted case.
        Valid("hello");
        for(int index=0;index<cases.Length && index<24;index++)
        {
            deadline.Token.ThrowIfCancellationRequested();if(clock.ElapsedMilliseconds>=10_000)throw new TimeoutException("Harness deadline.");
            try{cases[index].Run();passed++;ReportCase(cases[index].Id,"PASS",null);}
            catch(Exception exception) when(exception is InvalidOperationException or TimeoutException){failed++;ReportCase(cases[index].Id,"FAIL",exception.Message);}
        }
        using var summary=new MemoryStream();using(var writer=new Utf8JsonWriter(summary)){writer.WriteStartObject();writer.WriteNumber("denominator",24);writer.WriteNumber("passed",passed);writer.WriteNumber("failed",failed);writer.WriteNumber("skipped",0);writer.WriteNumber("macroCodeExecutions",0);writer.WriteBoolean("formalAcceptanceAllowed",false);writer.WriteNumber("acceptedP3Leaves",0);writer.WriteNumber("acceptedP3Gates",0);writer.WriteEndObject();writer.Flush();}Console.WriteLine(Encoding.UTF8.GetString(summary.ToArray()));return failed==0?0:1;
    }
    private static void ReportCase(string id,string status,string? error)
    {using var buffer=new MemoryStream();using(var writer=new Utf8JsonWriter(buffer)){writer.WriteStartObject();writer.WriteString("id",id);writer.WriteString("status",status);if(error is not null)writer.WriteString("error",error);writer.WriteEndObject();writer.Flush();}Console.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));}
    private static void DuplicateField(){Valid("expand");string json=Seed("expand").ToJsonString();Invalid(json.Insert(1,"\"requestId\":\"duplicate\","));}
    private static JsonObject Seed(string kind)
    {
        bool handshake=kind is "hello" or "ready", expansion=kind is "expand" or "expanded", diagnostic=kind is "diagnostic" or "error";
        return new JsonObject
        {
            ["protocolVersion"]=Contract.Protocol,["requestId"]="request-001",["direction"]=kind is "hello" or "expand" or "cancel"?"compiler-to-host":"host-to-compiler",["kind"]=kind,
            ["capabilities"]=handshake?new JsonArray("token-tree-v1","span-v1","diagnostics-v1","cancellation-v1"):new JsonArray(),
            ["documents"]=expansion||diagnostic?new JsonArray(new JsonObject{["id"]="source-1",["utf16Length"]=3}):new JsonArray(),
            ["spans"]=expansion||diagnostic?new JsonArray(new JsonObject{["documentId"]="source-1",["start"]=0,["length"]=3}):new JsonArray(),
            ["tokens"]=expansion?new JsonArray(new JsonObject{["kind"]="identifier",["text"]="abc",["spanIndex"]=0}):new JsonArray(),
            ["diagnostics"]=diagnostic?new JsonArray(new JsonObject{["severity"]="error",["code"]="PROC001",["message"]="Example diagnostic",["spanIndex"]=0}):new JsonArray(),
        };
    }
    private static void Valid(string kind){Result result=Contract.Validate(Seed(kind).ToJsonString());if(!result.IsSuccessful||result.FormalAcceptanceAllowed||result.MacroCodeExecutions!=0)throw new InvalidOperationException(result.Message);}
    private static void Mutation(string kind,Action<JsonObject> change){JsonObject root=Seed(kind);change(root);Invalid(root.ToJsonString());}
    private static void Invalid(string json){Result result=Contract.Validate(json);if(result.IsSuccessful||string.IsNullOrEmpty(result.DiagnosticCode)||result.Kind.Length!=0||result.TokenCount!=0||result.SpanCount!=0)throw new InvalidOperationException("Mutation failed to reject without partial inventory.");}
    private static void Cancellation(){using var cancellation=new CancellationTokenSource();cancellation.Cancel();try{Contract.Validate("{}",cancellation.Token);}catch(OperationCanceledException){return;}throw new InvalidOperationException("Caller cancellation was swallowed.");}
}
