using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace AsterismApp;

public static class NotebookPreviewRenderer
{
    // Notebook HTML is untrusted. Markdown is rendered with raw HTML disabled;
    // rich HTML outputs live in sandboxed frames with their own restrictive CSP.
    private static readonly MarkdownPipeline Pipeline = CreatePipeline();
    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml();
        var attributes = builder.Extensions.FirstOrDefault(extension => extension is Markdig.Extensions.GenericAttributes.GenericAttributesExtension);
        if (attributes is not null) builder.Extensions.Remove(attributes);
        return builder.Build();
    }

    public static string RenderBody(NotebookDocument document, string vaultPath, string notebookPath)
    {
        if (document.Error is not null) return $"<p class=\"notebook-error\">{Encode(document.Error)}</p>";
        var body = new StringBuilder($"<div class=\"notebook-info\">IPYNB · {Encode(document.Language)} · {document.Cells.Count}개 셀 · 읽기 전용 / 저장된 출력</div>");
        if (document.Cells.Count == 0) body.Append("<p>빈 노트북입니다.</p>");
        foreach (var cell in document.Cells)
        {
            body.Append("<section class=\"notebook-cell\">");
            if (cell.Kind == "code")
            {
                var language = Regex.IsMatch(document.Language, "^[a-zA-Z0-9_+.-]{1,32}$") ? document.Language : "text";
                body.Append($"<div class=\"notebook-prompt\">In [{Encode(cell.ExecutionCount.Length == 0 ? " " : cell.ExecutionCount)}]</div>");
                body.Append(CodeSyntaxHighlighter.HighlightBlocks($"<pre><code class=\"language-{language}\">{Encode(cell.Source)}</code></pre>"));
            }
            else if (cell.Kind == "markdown")
            {
                var markdown = cell.Source;
                markdown = Regex.Replace(markdown, @"\[\[([^\]\r\n]+)\]\]", match =>
                {
                    var parts = match.Groups[1].Value.Split('|', 2);
                    var label = parts[^1].Replace("[", "\\[").Replace("]", "\\]");
                    return $"[{label}](node-note://note/{Uri.EscapeDataString(parts[0])})";
                });
                var html = Markdown.ToHtml(markdown, Pipeline);
                html = Regex.Replace(html, "src=\"([^\"]*)\"", match =>
                {
                    try
                    {
                    var source = WebUtility.HtmlDecode(match.Groups[1].Value);
                    if (source.StartsWith("attachment:", StringComparison.OrdinalIgnoreCase))
                        return cell.Attachments.TryGetValue(Uri.UnescapeDataString(source[11..]), out var attachment) ? $"src=\"{Encode(attachment)}\"" : "src=\"\"";
                    if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
                        return uri.Scheme == "data" && source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) ? match.Value : "src=\"\"";
                    var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(notebookPath)!, Uri.UnescapeDataString(source)));
                    var root = Path.GetFullPath(vaultPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "src=\"\"";
                    var relative = Path.GetRelativePath(vaultPath, path).Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString);
                    return $"src=\"https://{MarkdownPreviewRenderer.VaultAssetHostName}/{string.Join("/", relative)}\"";
                    }
                    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
                    { return "src=\"\""; }
                });
                html = Regex.Replace(html, "href=\"([^\"]*)\"", match =>
                {
                    var href = WebUtility.HtmlDecode(match.Groups[1].Value);
                    if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return match.Value;
                    return uri.Scheme is "http" or "https" or "mailto" or "node-note" ? match.Value : "href=\"#\"";
                });
                body.Append(CodeSyntaxHighlighter.HighlightBlocks(html));
            }
            else body.Append($"<pre class=\"notebook-output\">{Encode(cell.Source)}</pre>");
            foreach (var output in cell.Outputs) body.Append(RenderOutput(output));
            body.Append("</section>");
        }
        return body.ToString();
    }

    private static string RenderOutput(NotebookOutput output)
    {
        if (output.Text.Length > 0)
        {
            var plain = Regex.Replace(output.Text, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
            return $"<pre class=\"notebook-output{(output.Kind == "error" ? " notebook-error" : "")}\">{Encode(plain)}</pre>";
        }
        foreach (var mime in new[] { "image/png", "image/jpeg", "image/gif" })
            if (output.Data.TryGetValue(mime, out var image))
                return $"<div class=\"notebook-output\"><img alt=\"저장된 셀 출력\" src=\"data:{mime};base64,{Encode(image.Replace("\n", "").Replace("\r", ""))}\"></div>";
        if (output.Data.TryGetValue("text/html", out var html))
        {
            var frame = "<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'\"><style>body{margin:8px;font:12px/1.6 'Segoe UI',sans-serif;color:#202020;background:white}table{border-collapse:collapse}th,td{padding:6px 10px;border-bottom:1px solid #ddd;text-align:left}img{max-width:100%}</style></head><body>" + html + "</body></html>";
            return $"<iframe class=\"notebook-html-output\" title=\"저장된 HTML 출력\" sandbox=\"\" srcdoc=\"{Encode(frame)}\"></iframe>";
        }
        if (output.Data.TryGetValue("text/plain", out var text)) return $"<pre class=\"notebook-output\">{Encode(text)}</pre>";
        if (output.Data.Count > 0) return "<p class=\"notebook-info\">대화형 출력은 실행 환경이 필요해 표시할 수 없습니다.</p>";
        return "";
    }
}
