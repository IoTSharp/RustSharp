using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class Program
{
    private const string Candidate="f463ac8fff710b11f851881437f9db864e4b42f6";
    private const string InventoryHash="5FAA8F8772F250C28EF9A71E29D29266DC4D539E6E517969D7FE2ED3997184A1";
    private sealed class Work(int maximum,int milliseconds,CancellationToken token)
    {
        private readonly Stopwatch clock=Stopwatch.StartNew();private int count;
        internal void Step(){token.ThrowIfCancellationRequested();if(++count>maximum||clock.ElapsedMilliseconds>=milliseconds)throw new TimeoutException("Ordered inventory count/time bound.");}
    }
    private static int Main(string[] args)
    {
        if(args.Length!=3)throw new ArgumentException("proposal-manifest original-inventory-index fresh-report");
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(20));CancellationToken token=cancel.Token;
        Require(!File.Exists(args[2]),"Fresh report required.");
        JsonObject proposal=JsonNode.Parse(Read(args[0],4194304))?.AsObject()??throw new InvalidDataException("Proposal root.");
        using JsonDocument inventory=JsonDocument.Parse(Read(args[1],1048576));JsonElement input=inventory.RootElement;
        Require(input.GetProperty("candidate").GetString()==Candidate&&input.GetProperty("runId").GetString()=="38515250985"&&input.GetProperty("attempt").GetInt32()==1,"Frozen original identity.");
        JsonElement reports=input.GetProperty("reports");Require(reports.GetArrayLength()==4,"Four original reports required.");
        using var stream=new FileStream(args[2],FileMode.CreateNew,FileAccess.Write,FileShare.Read);using var json=new Utf8JsonWriter(stream,new(){Indented=true});
        json.WriteStartObject();json.WriteString("taskId","P1-GATE.02.exact-ordered-cleanup-inventory");json.WriteString("candidate",Candidate);json.WriteString("expectedInventorySha256",InventoryHash);json.WriteBoolean("originalCodeExecuted",false);
        json.WriteStartArray("originals");var keys=new HashSet<string>(StringComparer.Ordinal);var originalWork=new Work(4,10000,token);int originalCount=0;
        for(int index=0;index<4;index++)
        {
            originalWork.Step();JsonElement entry=reports[index];string platform=entry.GetProperty("platform").GetString()!,kind=entry.GetProperty("kind").GetString()!,key=platform+"/"+kind;
            Require(keys.Add(key),"Duplicate original report identity.");string expected=ReportHash(key);string path=entry.GetProperty("path").GetString()!;byte[] bytes=Read(path,4194304);
            string actualHash=Convert.ToHexString(SHA256.HashData(bytes));Require(actualHash==expected&&entry.GetProperty("reportSha256").GetString()==expected,"Actual original report bytes differ from immutable compiled binding.");
            using JsonDocument raw=JsonDocument.Parse(bytes);JsonElement cases=raw.RootElement.GetProperty("cases");
            // First original's first case is an explicit tiny shape trial before any28-row traversal.
            if(index==0){Require(cases.GetArrayLength()==28,"Tiny count.");CheckPair(cases[0].GetProperty("id").GetString(),cases[0].GetProperty("sourceSha256").GetString(),token);}
            string actual=Digest(cases,"sourceSha256",token);Require(actual==InventoryHash,"Original ordered28 ID/source inventory differs.");
            json.WriteStartObject();json.WriteString("identity",key);json.WriteString("path",path);json.WriteString("sha256",actualHash);json.WriteString("orderedInventorySha256",actual);json.WriteNumber("orderedRows",28);
            json.WriteStartArray("pairs");WritePairs(json,cases,"sourceSha256",token);json.WriteEndArray();json.WriteEndObject();originalCount++;Console.Error.WriteLine("original ordered inventory "+(index+1)+"/4");
        }
        Require(originalCount==4&&keys.Count==4,"Complete four originals required.");json.WriteEndArray();
        string? baselineError=ValidateProposal(proposal,token);Require(baselineError is null,"Exact proposal inventory rejected: "+baselineError);
        json.WriteBoolean("proposalExactOrderedInventory",true);json.WriteStartArray("newInventoryMutations");
        string[] names=["rename-unaffected-case","substitute-unaffected-case","alter-source-hash","duplicate-id","missing-id","reordered-pair"];
        int mutations=0;var mutationWork=new Work(6,5000,token);
        for(int index=0;index<6;index++)
        {
            mutationWork.Step();var changed=(JsonObject)proposal.DeepClone();JsonArray cases=changed["cases"]!.AsArray();
            switch(index)
            {
                case 0:cases[0]!["id"]=cases[0]!["id"]!.GetValue<string>()+"-renamed";break;
                case 1:cases[5]!["id"]="unrelated-substituted-case";break;
                case 2:string hash=cases[9]!["originalSourceSha256"]!.GetValue<string>();cases[9]!["originalSourceSha256"]=(hash[0]=='0'?"1":"0")+hash[1..];break;
                case 3:cases[10]!["id"]=cases[11]!["id"]!.GetValue<string>();break;
                case 4:cases.RemoveAt(7);break;
                case 5:JsonNode first=cases[1]!.DeepClone(),second=cases[2]!.DeepClone();cases[1]=second;cases[2]=first;break;
            }
            string? error=ValidateProposal(changed,token);Require(error is not null,"Inventory mutation accepted: "+names[index]);json.WriteStartObject();json.WriteString("id",names[index]);json.WriteBoolean("rejected",true);json.WriteString("reason",error);json.WriteEndObject();mutations++;
            Console.Error.WriteLine("new inventory mutation "+(index+1)+"/6");
        }
        Require(mutations==6,"All six new inventory mutations required.");json.WriteEndArray();json.WriteNumber("originalReportDenominator",4);json.WriteNumber("originalOrderedRowDenominator",112);json.WriteNumber("proposalOrderedRows",28);json.WriteNumber("newMutationDenominator",6);json.WriteNumber("newMutationsExecuted",mutations);
        json.WriteString("scope","EXACT_INVENTORY_BINDING_ONLY_NOT_RUNTIME_SEMANTICS");json.WriteBoolean("proposalApproved",false);json.WriteBoolean("productionImplemented",false);json.WriteBoolean("fullP1Closure",false);json.WriteString("state","COMPONENT_PASS_PENDING_ROOT_REVIEW");json.WriteEndObject();json.Flush();stream.Flush(true);return 0;
    }
    private static string? ValidateProposal(JsonObject proposal,CancellationToken token)
    {
        try
        {
            if(proposal["approvalGranted"]?.GetValue<bool>()!=false||proposal["oldProfilesPreserved"]?.GetValue<bool>()!=true||proposal["productionImplemented"]?.GetValue<bool>()!=false||proposal["fullP1Closure"]?.GetValue<bool>()!=false)return "Proposal authority/compatibility boundary changed.";
            if(proposal["fixtureDenominator"]?.GetValue<int>()!=28||proposal["differentialCells"]?.GetValue<int>()!=168||proposal["ilverifyOriginalPeCells"]?.GetValue<int>()!=56||proposal["callableCasesPerHost"]?.GetValue<int>()!=7||proposal["callableArtifactsPerHost"]?.GetValue<int>()!=2)return "Frozen runtime/ILVerify/callable denominator changed.";
            JsonArray array=proposal["cases"]?.AsArray()??throw new InvalidDataException("Missing proposal cases.");
            return DigestProposal(array,token)==InventoryHash?null:"Exact ordered ID+source hash fingerprint mismatch.";
        }
        catch(Exception error) when(error is InvalidDataException or InvalidOperationException or FormatException or JsonException){return error.Message;}
    }
    private static string Digest(JsonElement cases,string hashName,CancellationToken token)
    {
        Require(cases.ValueKind==JsonValueKind.Array&&cases.GetArrayLength()==28,"Exactly28 ordered cases required.");var work=new Work(28,2000,token);var ids=new HashSet<string>(StringComparer.Ordinal);var text=new StringBuilder();
        for(int index=0;index<28;index++){work.Step();string id=cases[index].GetProperty("id").GetString()!,hash=cases[index].GetProperty(hashName).GetString()!;CheckPair(id,hash,token);Require(ids.Add(id),"Duplicate case ID.");text.Append(id).Append('\t').Append(hash).Append('\n');}
        Require(ids.Count==28,"Complete unique inventory required.");return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
    private static string DigestProposal(JsonArray cases,CancellationToken token)
    {
        Require(cases.Count==28,"Exactly28 ordered proposal cases required.");var work=new Work(28,2000,token);var ids=new HashSet<string>(StringComparer.Ordinal);var text=new StringBuilder();
        for(int index=0;index<28;index++){work.Step();string? id=cases[index]?["id"]?.GetValue<string>(),hash=cases[index]?["originalSourceSha256"]?.GetValue<string>();CheckPair(id,hash,token);Require(ids.Add(id!),"Duplicate proposal case ID.");text.Append(id).Append('\t').Append(hash).Append('\n');}
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
    private static void CheckPair(string? id,string? hash,CancellationToken token){Require(id is {Length:>0 and <=256}&&id.IndexOfAny(['\r','\n','\t'])<0,"Bounded unambiguous case ID.");Require(hash is {Length:64},"SourceSHA256 exact length.");var work=new Work(64,1000,token);for(int index=0;index<64;index++){work.Step();char c=hash![index];Require(c is >= '0' and <= '9' or >= 'A' and <= 'F',"Uppercase source SHA256 required.");}}
    private static void WritePairs(Utf8JsonWriter json,JsonElement cases,string hashName,CancellationToken token){var work=new Work(28,2000,token);for(int index=0;index<28;index++){work.Step();json.WriteStartObject();json.WriteNumber("ordinal",index);json.WriteString("id",cases[index].GetProperty("id").GetString());json.WriteString("sourceSha256",cases[index].GetProperty(hashName).GetString());json.WriteEndObject();}}
    private static string ReportHash(string identity)=>identity switch
    {
        "linux-x64/native-v5.json"=>"E031787E1806CFE2C09BB7AE891240C27C58A69C3DA029827004F0C443E10850",
        "linux-x64/native-aot-v1.json"=>"17151CA4FF3605E4681D38459017BD460C6E6E0FF22CDCE53672FE73A8DA11A6",
        "windows-x64/native-v5.json"=>"443E70A3193E30A4B1DE1F652D5F06586A1E1EAC2265861F7578C1A783FD1B7A",
        "windows-x64/native-aot-v1.json"=>"85CBEFDB7D830B0EF238A4059EA68C766EA92FC4C7543CF3D761CEF2DA5646EB",
        _=>throw new InvalidDataException("Unexpected native original identity.")
    };
    private static byte[] Read(string path,int maximum){using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);Require(input.Length>0&&input.Length<=maximum,"Input byte bound.");byte[] bytes=new byte[checked((int)input.Length)];input.ReadExactly(bytes);return bytes;}
    private static void Require(bool value,string message){if(!value)throw new InvalidDataException(message);}
}
