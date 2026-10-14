using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using RustSharp.Syntax;
namespace RustSharp.Compiler.Documentation;
public sealed record DocumentationPublicationProgress(int FilesStaged,string LeasePath);
public sealed record DocumentationPublicationResult(bool Published,string TargetPath,IReadOnlyList<Diagnostic> Diagnostics,string? RetainedLeasePath);
public static class DocumentationPublisher
{
 public static async Task<DocumentationPublicationResult> PublishNewAsync(DocumentationOutput output,string targetPath,DocumentationOptions options,Action<DocumentationPublicationProgress>? progress=null,CancellationToken cancellationToken=default)
 {
  ArgumentNullException.ThrowIfNull(output);ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);ArgumentNullException.ThrowIfNull(options);
  string? lease=null,marker=null;DocumentationFileIdentity? leaseBirth=null,markerBirth=null;string identity=Guid.NewGuid().ToString("N");bool published=false;var errors=new List<Diagnostic>();var files=new List<(string Path,DocumentationFileIdentity Birth,byte[] Bytes)>();var folders=new Dictionary<string,DocumentationFileIdentity>(StringComparer.OrdinalIgnoreCase);
  using var identities=new DocumentationIdentityScope();
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
  try
  {
   var budget=new DocumentationBudget(options,deadline.Token);deadline.CancelAfter(options.Timeout);
   if(!output.IsSuccessful||!Path.IsPathFullyQualified(targetPath)||output.Files.Count<1||output.Files.Count>options.MaximumFiles)throw new ArgumentException("Successful output, absolute new target, and bounded file count required.");
   targetPath=Path.GetFullPath(targetPath);string? parent=Path.GetDirectoryName(targetPath);if(parent is null||!Directory.Exists(parent)||Directory.Exists(targetPath)||File.Exists(targetPath))throw new ArgumentException("Existing ordinary parent and absent destination required.");CheckParents(parent,budget);
   var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int bytes=0;
   foreach(var pair in output.Files){budget.Step();SafePath(pair.Key,budget);if(!keys.Add(pair.Key))throw new ArgumentException("Output file paths collide.");for(int n=0;n<pair.Value.Length;n+=1024)budget.Step();int length=Encoding.UTF8.GetByteCount(pair.Value);if(length>options.MaximumOutputBytes-bytes)throw new DocumentationLimitException();bytes+=length;}
   if(!output.Files.TryGetValue("index.html",out string? index)||index!=output.Html)throw new ArgumentException("Index and Files payload differ.");ValidateLinks(output,options,budget);
   lease=Path.Combine(parent,".rustsharp-docs-"+identity);marker=lease+".lease";if(Directory.Exists(lease)||File.Exists(lease)||File.Exists(marker))throw new IOException("Publication lease collision.");
   using(var owner=new FileStream(marker,FileMode.CreateNew,FileAccess.Write,FileShare.None)){owner.Write(Encoding.ASCII.GetBytes(identity));}markerBirth=identities.Capture(marker);
   Directory.CreateDirectory(lease);leaseBirth=identities.Capture(lease);folders.Add(lease,leaseBirth);
   foreach(var pair in output.Files)
   {
    budget.Step();CheckParents(parent,budget);string path=Path.GetFullPath(Path.Combine(lease,pair.Key));if(!path.StartsWith(lease+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("File escapes lease.");
    if(!LeaseMatches())throw new IOException("Publication lease identity changed.");EnsureDirectories(lease,Path.GetDirectoryName(path)!,folders,identities,budget);CheckTrackedParents(lease,Path.GetDirectoryName(path)!,folders,budget);
    byte[] data=Encoding.UTF8.GetBytes(pair.Value);await using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.Asynchronous)){files.Add((path,identities.Capture(path),data));await stream.WriteAsync(data,deadline.Token).ConfigureAwait(false);await stream.FlushAsync(deadline.Token).ConfigureAwait(false);}
    budget.Step();if(!LeaseMatches())throw new IOException("Publication lease identity changed.");CheckTrackedParents(lease,Path.GetDirectoryName(path)!,folders,budget);progress?.Invoke(new(files.Count,lease));budget.Step();
   }
   budget.Step();CheckParents(parent,budget);if(!LeaseMatches())throw new IOException("Publication lease identity changed.");CheckInventory(lease,files,folders,budget);budget.Step();Directory.Move(lease,targetPath);published=true;lease=null;
  }
  catch(Exception exception)when(exception is ArgumentException or IOException or UnauthorizedAccessException or DocumentationLimitException or OperationCanceledException or RegexMatchTimeoutException){errors.Add(new("RSTD2001",exception.Message,new(0,0)){SourcePath=targetPath});}
  finally
  {
   // Preserve unknown objects. Cleanup uses pinned Linux identity (portable birth elsewhere), lease/content identity, and never recursive deletion.
   var cleanup=Stopwatch.StartNew();bool owned=CleanupLeaseMatches();var cleanupBudget=new DocumentationBudget(new(){Timeout=TimeSpan.FromSeconds(5)},CancellationToken.None);
   if(!published&&lease is not null&&owned)
   {
    for(int n=files.Count-1;n>=0&&files.Count<=1024&&cleanup.Elapsed<TimeSpan.FromSeconds(5);n--)
    {var file=files[n];try{if(CleanupLeaseMatches()&&SafeCleanupParents(lease!,Path.GetDirectoryName(file.Path)!,folders,cleanupBudget)&&File.Exists(file.Path)&&(File.GetAttributes(file.Path)&FileAttributes.ReparsePoint)==0&&file.Birth.Matches(file.Path)&&new FileInfo(file.Path).Length<=file.Bytes.Length){byte[] actual=File.ReadAllBytes(file.Path);if(actual.Length<=file.Bytes.Length&&actual.AsSpan().SequenceEqual(file.Bytes.AsSpan(0,actual.Length)))File.Delete(file.Path);}}catch(IOException){}catch(UnauthorizedAccessException){}}
    foreach(var folder in folders.OrderByDescending(static value=>value.Key.Length)){if(cleanup.Elapsed>=TimeSpan.FromSeconds(5))break;try{if(CleanupLeaseMatches()&&SafeCleanupParents(lease!,folder.Key,folders,cleanupBudget)&&Directory.Exists(folder.Key)&&(File.GetAttributes(folder.Key)&FileAttributes.ReparsePoint)==0&&folder.Value.Matches(folder.Key))Directory.Delete(folder.Key,false);}catch(IOException){}catch(UnauthorizedAccessException){}}
    if(!Directory.Exists(lease))lease=null;
   }
   if(marker is not null&&MarkerMatches()&&(published||lease is null))try{File.Delete(marker);}catch(IOException){}catch(UnauthorizedAccessException){}
   bool MarkerMatches(){try{return marker is not null&&File.Exists(marker)&&(File.GetAttributes(marker)&FileAttributes.ReparsePoint)==0&&markerBirth is not null&&markerBirth.Matches(marker)&&new FileInfo(marker).Length==32&&File.ReadAllText(marker)==identity;}catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}}
   bool CleanupLeaseMatches()=>MarkerMatches()&&(lease is null||Directory.Exists(lease)&&(File.GetAttributes(lease)&FileAttributes.ReparsePoint)==0&&leaseBirth is not null&&leaseBirth.Matches(lease));
  }
  string? retained=lease is not null&&(Directory.Exists(lease)||File.Exists(lease))?lease:null;if(marker is not null&&File.Exists(marker))retained??=marker;return new(published,targetPath,errors.AsReadOnly(),retained);
  bool LeaseMatches(){try{return marker is not null&&File.Exists(marker)&&(File.GetAttributes(marker)&FileAttributes.ReparsePoint)==0&&markerBirth is not null&&markerBirth.Matches(marker)&&new FileInfo(marker).Length==32&&File.ReadAllText(marker)==identity&&lease is not null&&Directory.Exists(lease)&&(File.GetAttributes(lease)&FileAttributes.ReparsePoint)==0&&leaseBirth is not null&&leaseBirth.Matches(lease);}catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}}
 }
 // These checks reject synchronous callback changes. Portable checks cannot close every concurrent filesystem TOCTOU window.
 private static void EnsureDirectories(string root,string parent,Dictionary<string,DocumentationFileIdentity> folders,DocumentationIdentityScope identities,DocumentationBudget budget)
 {
  CheckTrackedParents(root,root,folders,budget);string relative=Path.GetRelativePath(root,parent);if(relative==".")return;
  string current=root;var parts=relative.Split([Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar],StringSplitOptions.RemoveEmptyEntries);if(parts.Length>128)throw new DocumentationLimitException();
  foreach(string part in parts)
  {
   budget.Step();CheckTrackedParents(root,current,folders,budget);if(part is "." or "..")throw new IOException("Directory escapes lease.");string next=Path.Combine(current,part);
   if(!folders.ContainsKey(next)){if(Directory.Exists(next)||File.Exists(next))throw new IOException("Unknown staging directory.");Directory.CreateDirectory(next);folders.Add(next,identities.Capture(next));}
   CheckTrackedParents(root,next,folders,budget);current=next;
  }
 }
 private static void CheckTrackedParents(string root,string parent,IReadOnlyDictionary<string,DocumentationFileIdentity> folders,DocumentationBudget budget)
 {
  if(parent!=root&&!parent.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Ancestor escapes lease.");string? current=parent;
  for(int depth=0;current is not null&&depth<129;depth++)
  {
   budget.Step();if(!folders.TryGetValue(current,out DocumentationFileIdentity? birth)||!Directory.Exists(current)||(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0||!birth.Matches(current))throw new IOException("Unknown, replaced, or reparse staging ancestor.");if(string.Equals(current,root,StringComparison.OrdinalIgnoreCase))return;current=Path.GetDirectoryName(current);
  }
  throw new DocumentationLimitException();
 }
 private static bool SafeCleanupParents(string root,string parent,IReadOnlyDictionary<string,DocumentationFileIdentity> folders,DocumentationBudget budget)
 {try{CheckTrackedParents(root,parent,folders,budget);return true;}catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}catch(DocumentationLimitException){return false;}}
 private static void CheckInventory(string root,List<(string Path,DocumentationFileIdentity Birth,byte[] Bytes)> files,Dictionary<string,DocumentationFileIdentity> folders,DocumentationBudget budget)
 {
  var expected=files.ToDictionary(static file=>file.Path,StringComparer.OrdinalIgnoreCase);var pending=new Stack<(string Path,int Depth)>();pending.Push((root,0));int entries=0,seenFiles=0,seenFolders=0;var clock=Stopwatch.StartNew();
  int maximum=files.Count+folders.Count;
  for(int round=0;pending.Count>0&&round<maximum;round++)
  {
   budget.Step();if(clock.Elapsed>TimeSpan.FromSeconds(5))throw new DocumentationLimitException();var directory=pending.Pop();if(directory.Depth>128||!folders.TryGetValue(directory.Path,out DocumentationFileIdentity? birth)||!birth.Matches(directory.Path)||(File.GetAttributes(directory.Path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Unknown or replaced staging directory.");seenFolders++;
   foreach(string path in Directory.EnumerateFileSystemEntries(directory.Path))
   {
    budget.Step();if(++entries>maximum||clock.Elapsed>TimeSpan.FromSeconds(5))throw new DocumentationLimitException();var attributes=File.GetAttributes(path);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Staging reparse point detected.");
    if((attributes&FileAttributes.Directory)!=0){if(!folders.ContainsKey(path))throw new IOException("Unknown staging directory.");pending.Push((path,directory.Depth+1));}
    else{if(!expected.TryGetValue(path,out var file)||!file.Birth.Matches(path)||new FileInfo(path).Length!=file.Bytes.Length)throw new IOException("Unknown or replaced staging file.");byte[] actual=File.ReadAllBytes(path);budget.Step();if(!actual.AsSpan().SequenceEqual(file.Bytes))throw new IOException("Staging payload changed.");seenFiles++;}
   }
  }
  if(pending.Count!=0||seenFiles!=files.Count||seenFolders!=folders.Count)throw new IOException("Incomplete staging inventory.");
 }
 private static void CheckParents(string path,DocumentationBudget budget){string? current=path;for(int depth=0;current is not null&&depth<128;depth++){budget.Step();if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new ArgumentException("Reparse-point publication ancestors are forbidden.");current=Path.GetDirectoryName(current);}if(current is not null)throw new DocumentationLimitException();}
 private static void SafePath(string path,DocumentationBudget budget)
 {
  budget.Step();if(path.Length==0||Path.IsPathRooted(path)||path.Contains(':')||path.Contains('?')||path.Contains('#')||path.Contains('\\')||path.Contains('\0'))throw new ArgumentException("Unsafe output path.");
  foreach(string part in path.Split('/')){budget.Step();string stem=part.Split('.')[0].ToUpperInvariant();if(part is "" or "." or ".."||part.EndsWith(' ')||part.EndsWith('.')||part.IndexOfAny(['<','>','"','|','*'])>=0||part.Any(char.IsControl)||stem is "CON" or "PRN" or "AUX" or "NUL"||stem.Length==4&&(stem.StartsWith("COM",StringComparison.Ordinal)||stem.StartsWith("LPT",StringComparison.Ordinal))&&stem[3] is >= '1' and <= '9')throw new ArgumentException("Unsafe output path segment.");}
 }
 private static void ValidateLinks(DocumentationOutput output,DocumentationOptions options,DocumentationBudget budget)
 {
  var regex=new Regex("<[^>]*?\\bhref\\s*=\\s*(?:\"(?<double>[^\"]*)\"|'(?<single>[^']*)')[^>]*>",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant|RegexOptions.NonBacktracking,TimeSpan.FromMilliseconds(100));int links=0;
  foreach(var file in output.Files)foreach(Match match in regex.Matches(file.Value))
  {
   budget.Step();if(++links>options.MaximumLinks)throw new DocumentationLimitException();string href=System.Net.WebUtility.HtmlDecode(match.Groups["double"].Success?match.Groups["double"].Value:match.Groups["single"].Value);
   if(Uri.TryCreate(href,UriKind.Absolute,out Uri? uri)){if(uri.Scheme!=Uri.UriSchemeHttps)throw new ArgumentException("Unsafe documentation link scheme.");continue;}
   int at=href.IndexOf('#');string path=at<0?href:href[..at];string decoded=Uri.UnescapeDataString(path);if(path.Length>0)SafePath(decoded,budget);string parent=Path.GetDirectoryName(file.Key)?.Replace('\\','/')??string.Empty;string linked=path.Length==0?file.Key:parent.Length==0?decoded:parent+"/"+decoded;SafePath(linked,budget);if(!output.Files.TryGetValue(linked,out string? page))throw new ArgumentException("Local link must identify an emitted file.");if(at>=0&&!page.Contains("id=\""+System.Net.WebUtility.HtmlEncode(href[(at+1)..])+"\"",StringComparison.Ordinal))throw new ArgumentException("Local fragment must exist.");
  }
 }
}