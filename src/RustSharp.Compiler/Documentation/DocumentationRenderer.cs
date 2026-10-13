using System.Net;
using System.Text;
using RustSharp.Syntax;
namespace RustSharp.Compiler.Documentation;
public static class DocumentationRenderer
{
    public static DocumentationOutput Render(DocumentationModel model, DocumentationOptions options, CancellationToken cancellationToken = default) { try { return RenderCore(model, options, cancellationToken); } catch (DocumentationLimitException) { return new(null, new[] { new Diagnostic("RSTD0001", "Documentation budget exceeded.", new(0, 0)) }); } }
    private static DocumentationOutput RenderCore(DocumentationModel model, DocumentationOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(options); var budget = new DocumentationBudget(options, cancellationToken);
        var errors = new List<Diagnostic>(); var html = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"></head><body>"); int links = 0;
        foreach (DocumentationItem item in model.Items)
        {
            budget.Step(); string path = item.Source.Document.Path.Replace('\\', '/');
            if (Path.IsPathRooted(path) || path.IndexOfAny([':', '?', '#', '\0']) >= 0 || path.Split('/').Any(static part => part is ".." or "." or "")) { errors.Add(new("RSTD1003", "Unsafe source path.", item.Source.Span) { SourcePath = item.Source.Document.Path }); continue; }
            html.Append("<section id=\"").Append(WebUtility.HtmlEncode(Uri.EscapeDataString(item.Identity))).Append("\"><h2>").Append(WebUtility.HtmlEncode(item.Name)).Append("</h2><pre>").Append(WebUtility.HtmlEncode(item.Signature)).Append("</pre><a href=\"").Append(WebUtility.HtmlEncode(string.Join("/", path.Split('/').Select(Uri.EscapeDataString)) + "#L" + (1 + item.Source.Document.Text[..item.Source.Span.Start].Count(static c => c == '\n')) + "-span-" + item.Source.Span.Start + "-" + item.Source.Span.Length)).Append("\">source</a><p>");
            int cursor = 0;
            while (cursor < item.Comment.Length)
            {
                budget.Step(); int open = item.Comment.IndexOf('[', cursor); if (open < 0) { html.Append(WebUtility.HtmlEncode(item.Comment[cursor..])); break; }
                html.Append(WebUtility.HtmlEncode(item.Comment[cursor..open])); int close = item.Comment.IndexOf(']', open + 1); if (close < 0) { html.Append(WebUtility.HtmlEncode(item.Comment[open..])); break; }
                if (++links > options.MaximumLinks) throw new DocumentationLimitException(); string target = item.Comment[(open + 1)..close]; string? href = null;
                if (!target.Contains("::", StringComparison.Ordinal) && Uri.TryCreate(target, UriKind.Absolute, out Uri? uri)) { if (uri.Scheme == Uri.UriSchemeHttps) href = uri.AbsoluteUri; else errors.Add(new("RSTD1003", "Unsafe documentation link scheme.", item.Source.Span) { SourcePath = item.Source.Document.Path }); }
                else { DocumentationItem[] matches = (model.Aliases.TryGetValue(target, out var aliasItems) ? aliasItems : Array.Empty<DocumentationItem>()).Concat(model.Items.Where(candidate => candidate.Identity == (target.StartsWith("::", StringComparison.Ordinal) ? "crate" + target : target) || candidate.Name == target)).Distinct().ToArray(); if (matches.Length == 1) href = "#" + Uri.EscapeDataString(matches[0].Identity); else errors.Add(new(matches.Length == 0 ? "RSTD1001" : "RSTD1002", "Missing or ambiguous documentation link: " + target, item.Source.Span) { SourcePath = item.Source.Document.Path }); }
                if (href is not null) html.Append("<a href=\"").Append(WebUtility.HtmlEncode(href)).Append("\">").Append(WebUtility.HtmlEncode(target)).Append("</a>"); cursor = close + 1;
            }
            html.Append("</p></section>"); if (Encoding.UTF8.GetByteCount(html.ToString()) > options.MaximumOutputBytes) throw new DocumentationLimitException();
        }
        html.Append("</body></html>"); if (Encoding.UTF8.GetByteCount(html.ToString()) > options.MaximumOutputBytes) throw new DocumentationLimitException(); return errors.Count == 0 ? new(html.ToString(), Array.Empty<Diagnostic>()) : new(null, errors.AsReadOnly());
    }
}