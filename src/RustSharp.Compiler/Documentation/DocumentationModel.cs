using System.Collections.ObjectModel;
using System.Diagnostics;
using RustSharp.Syntax;
namespace RustSharp.Compiler.Documentation;
public sealed record DocumentationOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaximumItems { get; init; } = 10_000;
    public int MaximumLinks { get; init; } = 20_000;
    public int MaximumOutputBytes { get; init; } = 16_777_216;
    public int MaximumOperations { get; init; } = 1_000_000;
    public string? SourceRoot { get; init; }
}
public sealed record DocumentationItem(string Identity, string Name, string Kind, string Signature, string Comment, SafeCoreSourceLocation Source)
{ public string Anchor => Uri.EscapeDataString(Identity + "-" + Kind); }
public sealed record DocumentationResult(DocumentationModel? Model, IReadOnlyList<Diagnostic> Diagnostics)
{ public bool IsSuccessful => Model is not null && Diagnostics.Count == 0; }
public sealed record DocumentationOutput(string? Html, IReadOnlyList<Diagnostic> Diagnostics)
{ public bool IsSuccessful => Html is not null && Diagnostics.Count == 0; public IReadOnlyDictionary<string,string> Files { get; init; } = new ReadOnlyDictionary<string,string>(new Dictionary<string,string>()); }
internal sealed class DocumentationLimitException : Exception { }
internal sealed class DocumentationBudget
{
    private readonly DocumentationOptions options; private readonly CancellationToken cancellation; private readonly long started = Stopwatch.GetTimestamp(); private int operations;
    internal DocumentationBudget(DocumentationOptions options, CancellationToken cancellation)
    { if(options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromSeconds(10) || options.MaximumItems is <1 or >10_000 || options.MaximumLinks is <1 or >20_000 || options.MaximumOutputBytes is <1 or >16_777_216 || options.MaximumOperations is <1 or >1_000_000) throw new DocumentationLimitException(); this.options=options;this.cancellation=cancellation;Step(); }
    internal void Step() { cancellation.ThrowIfCancellationRequested();if(++operations > options.MaximumOperations || Stopwatch.GetElapsedTime(started)>options.Timeout)throw new DocumentationLimitException(); }
}
public sealed class DocumentationModel
{
    private DocumentationModel(IReadOnlyList<DocumentationItem> items, IReadOnlyDictionary<string,IReadOnlyList<DocumentationItem>> aliases){Items=items;Aliases=aliases;}
    public IReadOnlyList<DocumentationItem> Items { get; }
    internal IReadOnlyDictionary<string,IReadOnlyList<DocumentationItem>> Aliases { get; }
    public static DocumentationResult Create(SafeCoreSyntaxResult syntax,SafeCoreSourceMap sourceMap,DocumentationOptions options,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(syntax);ArgumentNullException.ThrowIfNull(sourceMap);ArgumentNullException.ThrowIfNull(options);
        try{return CreateCore(syntax,sourceMap,options,cancellationToken);}catch(DocumentationLimitException){return new(null,new[]{new Diagnostic("RSTD0001","Documentation budget exceeded.",new(0,0)){SourcePath=syntax.SourcePath}});}
    }
    private static DocumentationResult CreateCore(SafeCoreSyntaxResult syntax,SafeCoreSourceMap map,DocumentationOptions options,CancellationToken cancellation)
    {
        var budget=new DocumentationBudget(options,cancellation);if(!syntax.IsSuccessful)return new(null,syntax.Diagnostics);if(map.Documents.Count>1024||map.Documents.Sum(static d=>(long)d.Text.Length)>1_000_000)throw new DocumentationLimitException();
        var all=new List<(DocumentationItem Item,string Parent,bool Public,bool Visible)>();var uses=new List<(SafeCoreUseSyntax Use,string Parent,bool Visible)>();var impls=new List<(SafeCoreImplSyntax Impl,string Parent)>();var errors=new List<Diagnostic>();var aliases=new Dictionary<string,IReadOnlyList<DocumentationItem>>(StringComparer.Ordinal);
        Visit(syntax.Root!.Items,"crate",true,0);
        foreach(var use in uses){budget.Step();if(!use.Visible)continue;Use(use.Use.Tree,use.Use.Path,use.Use.Alias,use.Parent,use.Use.Span,[],0);}
        Promote();
        foreach(var entry in impls){budget.Step();string type=entry.Impl.SelfType is SafeCorePathTypeSyntax path?string.Join("::",path.Segments.Select(static p=>p.Name)):syntax.GetText(entry.Impl.SelfType.Span);string id=Normalize(type,entry.Parent);var owner=all.FirstOrDefault(e=>e.Visible&&(e.Item.Identity==id||e.Item.Name==type)&&e.Item.Kind is "struct" or "enum" or "type-alias");if(owner.Item is not null)Associated(entry.Impl.Items,owner.Item.Identity,entry.Impl.Trait is not null,true);}
        Promote();if(errors.Count>0)return new(null,errors.AsReadOnly());return new(new(Array.AsReadOnly(all.Where(static e=>e.Visible).Select(static e=>e.Item).OrderBy(static i=>i.Identity,StringComparer.Ordinal).ThenBy(static i=>i.Kind,StringComparer.Ordinal).ToArray()),new ReadOnlyDictionary<string,IReadOnlyList<DocumentationItem>>(aliases)),Array.Empty<Diagnostic>());
        void Add(string name,string kind,TextSpan span,IReadOnlyList<SafeCoreAttributeSyntax> attrs,string parent,bool pub,bool visible,int? end=null){budget.Step();if(all.Count>=options.MaximumItems)throw new DocumentationLimitException();all.Add((new(parent+"::"+name,name,kind,syntax.GetText(new(span.Start,Math.Max(0,(end??span.End)-span.Start))).Trim(),string.Join("\n",attrs.Where(static a=>a.IsDocumentation).Select(static a=>a.DocumentationText??string.Empty)),map.MapSpan(span)),parent,pub,visible));}
        int Header(TextSpan span){int nested=0;foreach(var token in syntax.LexResult.Tokens){budget.Step();if(token.Span.Start<span.Start)continue;if(token.Span.Start>=span.End)break;if(token.Text is "(" or "[" or "<")nested++;else if(token.Text is ")" or "]" or ">")nested=Math.Max(0,nested-1);else if(token.Text=="{"&&nested==0)return token.Span.Start;}return span.End;}
        void Visit(IReadOnlyList<SafeCoreItemSyntax> nodes,string parent,bool inherited,int depth){budget.Step();if(depth>128)throw new DocumentationLimitException();foreach(var node in nodes){budget.Step();bool pub=node.Visibility.Kind==SafeCoreVisibilityKind.Public;bool visible=inherited&&pub;switch(node){
            case SafeCoreUseSyntax u:uses.Add((u,parent,visible));break;
            case SafeCoreImplSyntax i:impls.Add((i,parent));break;
            case SafeCoreModuleSyntax m:Add(m.Name,"module",m.Span,m.Attributes.Concat(m.InnerAttributes).ToArray(),parent,pub,visible,Header(m.Span));if(m.IsExternal&&visible)Error("RSTD1001","External module must be expanded.",m.Span);else Visit(m.Items,parent+"::"+m.Name,visible,depth+1);break;
            case SafeCoreFunctionSyntax f:Add(f.Name,"function",f.Span,f.Attributes,parent,pub,visible,f.Body.Span.Start);break;
            case SafeCoreStructSyntax s:Add(s.Name,"struct",s.Span,s.Attributes,parent,pub,visible,Header(s.Span));for(int n=0;n<s.Fields.Count;n++){budget.Step();var field=s.Fields[n];bool fp=field.Visibility?.Kind==SafeCoreVisibilityKind.Public||field.Visibility is null&&field.IsPublic;Add(field.Name??n.ToString(System.Globalization.CultureInfo.InvariantCulture),"public-field",field.Span,field.Attributes,parent+"::"+s.Name,fp,visible&&fp);}break;
            case SafeCoreEnumSyntax e:Add(e.Name,"enum",e.Span,e.Attributes,parent,pub,visible,Header(e.Span));foreach(var v in e.Variants){budget.Step();Add(v.Name,"variant",v.Span,v.Attributes,parent+"::"+e.Name,true,visible);for(int n=0;n<v.Fields.Count;n++){budget.Step();var field=v.Fields[n];Add(field.Name??n.ToString(System.Globalization.CultureInfo.InvariantCulture),"public-field",field.Span,field.Attributes,parent+"::"+e.Name+"::"+v.Name,true,visible);}}break;
            case SafeCoreTypeAliasSyntax a:Add(a.Name,"type-alias",a.Span,a.Attributes,parent,pub,visible);break;
            case SafeCoreConstSyntax c:Add(c.Name,"const",c.Span,c.Attributes,parent,pub,visible);break;
            case SafeCoreTraitSyntax t:Add(t.Name,"trait",t.Span,t.Attributes.Concat(t.InnerAttributes).ToArray(),parent,pub,visible,Header(t.Span));Associated(t.Items,parent+"::"+t.Name,true,visible);break;
        }}}
        void Associated(IReadOnlyList<SafeCoreAssociatedItemSyntax> members,string parent,bool trait,bool visible){foreach(var member in members){budget.Step();bool pub=trait||member.Visibility.Kind==SafeCoreVisibilityKind.Public;string kind=member switch{SafeCoreAssociatedFunctionSyntax=>"associated-function",SafeCoreAssociatedTypeSyntax=>"associated-type",_=>"associated-const"};int? end=member is SafeCoreAssociatedFunctionSyntax{Body:{}body}?body.Span.Start:null;Add(member.Name,kind,member.Span,member.Attributes,parent,pub,visible&&pub,end);}}
        void Promote(){for(int round=0;round<all.Count;round++){budget.Step();bool changed=false;var owners=all.Where(static e=>e.Visible).Select(static e=>e.Item.Identity).ToHashSet(StringComparer.Ordinal);for(int n=0;n<all.Count;n++){budget.Step();var e=all[n];if(!e.Visible&&e.Public&&owners.Contains(e.Parent)){all[n]=(e.Item,e.Parent,e.Public,true);changed=true;}}if(!changed)break;}}
        void Use(SafeCoreUseTreeSyntax? tree,string fallback,string? alias,string parent,TextSpan span,IReadOnlyList<string> inherited,int depth){budget.Step();if(depth>128)throw new DocumentationLimitException();string[] parts=tree is null?fallback.Split("::",StringSplitOptions.RemoveEmptyEntries):inherited.Concat(tree.Prefix).ToArray();if(tree?.Kind==SafeCoreUseTreeKind.Group){foreach(var child in tree.Children)Use(child,fallback,alias,parent,span,parts,depth+1);return;}string target=Normalize(string.Join("::",parts),parent);var matches=all.Select((e,n)=>(Entry:e,Index:n)).Where(e=>e.Entry.Public&&(tree?.Kind==SafeCoreUseTreeKind.Glob?e.Entry.Parent==target:e.Entry.Item.Identity==target)).ToArray();if(matches.Length==0){Error("RSTD1001","Missing or private reexport: "+target,span);return;}foreach(var match in matches){budget.Step();var e=match.Entry;all[match.Index]=(e.Item,e.Parent,e.Public,true);string exported=tree?.Kind==SafeCoreUseTreeKind.Glob?e.Item.Name:tree?.Alias??alias??parts[^1];if(exported!="_"){string key=parent+"::"+exported;aliases[key]=Array.AsReadOnly(matches.Select(static x=>x.Entry.Item).ToArray());}}}
        void Error(string code,string message,TextSpan span){var loc=map.MapSpan(span);errors.Add(new(code,message,loc.Span){SourcePath=loc.Document.Path});}
    }
    private static string Normalize(string path,string parent){if(path.StartsWith("::",StringComparison.Ordinal))return "crate::"+path[2..];if(path.StartsWith("crate::",StringComparison.Ordinal))return path;if(path.StartsWith("self::",StringComparison.Ordinal))return parent+path[4..];if(path.StartsWith("super::",StringComparison.Ordinal)){int at=parent.LastIndexOf("::",StringComparison.Ordinal);return(at<0?"crate":parent[..at])+path[5..];}return parent+"::"+path;}
}