using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AsterismApp;

public static class MarkdownPreviewRenderer
{
    public const string VaultAssetHostName = "asterism-vault.local";
    private static readonly MarkdownPipeline Pipeline = CreatePipeline();

    private static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseYamlFrontMatter()
            .UseSoftlineBreakAsHardlineBreak()
            .UsePreciseSourceLocation();
        // Generic attributes interpret LaTeX groups such as \bar{x} as heading attributes.
        // Asterism does not expose that Markdown extension, so favor intact math everywhere.
        var genericAttributes = builder.Extensions.FirstOrDefault(extension =>
            extension is Markdig.Extensions.GenericAttributes.GenericAttributesExtension);
        if (genericAttributes is not null) builder.Extensions.Remove(genericAttributes);
        return builder.Build();
    }

    public static string Render(
        string markdown,
        string vaultPath,
        Func<string, string?>? resolveNote = null,
        IReadOnlyDictionary<string, bool>? foldStates = null,
        double initialScrollY = 0,
        double fontScale = 1,
        string accentColor = "#D1AF61",
        string surfaceTheme = "dark",
        PreviewEditSession? editSession = null)
    {
        var body = CodeSyntaxHighlighter.HighlightBlocks(RenderBody(markdown, vaultPath, resolveNote, 0, editSession));
        body = Regex.Replace(body, "<script[^>]*>.*?</script>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = Regex.Replace(body, "\\s+on[a-z]+\\s*=\\s*(['\"]).*?\\1", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = Regex.Replace(body, "href=(['\"])javascript:.*?\\1", "href=\"#\"", RegexOptions.IgnoreCase);
        body = Regex.Replace(body, "href=\"(?![a-z]+:|#)([^\"]+?)(?:\\.md)?(?:#[^\"]*)?\"", match => $"href=\"node-note://note/{Uri.EscapeDataString(WebUtility.HtmlDecode(match.Groups[1].Value).Replace("%20", " "))}\"");
        fontScale = Math.Clamp(fontScale, .8, 1.4);
        if (!Regex.IsMatch(accentColor, "^#[0-9a-fA-F]{6}$")) accentColor = "#D1AF61";
        return HtmlShell(body, foldStates, initialScrollY, fontScale, accentColor, surfaceTheme, editSession?.Id);
    }

    public static string RenderTitle(string title, double fontScale = 1, string surfaceTheme = "dark")
    {
        fontScale = Math.Clamp(fontScale, .8, 1.4);
        var surface = CssSurfaceFor(surfaceTheme);
        var colorScheme = surface.IsLight ? "light" : "dark";
        var scale = fontScale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var safeTitle = WebUtility.HtmlEncode(NormalizeMathDelimiters(MarkdownText.NormalizeTitle(title)));
        return $$"""
        <!doctype html>
        <html>
        <head>
          <meta charset="utf-8">
          <meta name="color-scheme" content="{{colorScheme}}">
          <link rel="stylesheet" href="https://node-assets.local/katex.min.css">
          <script src="https://node-assets.local/katex.min.js"></script>
          <script src="https://node-assets.local/auto-render.min.js"></script>
          <style>
            *{box-sizing:border-box}html,body{width:100%;height:100%;margin:0;overflow:hidden;background:{{surface.DocumentBackground}};color:{{(surface.IsLight ? "#111111" : "#F0F0F0")}}}
            body{display:flex;align-items:center;font:700 calc(13.3px * {{scale}})/1.35 'Segoe UI Variable Text','Segoe UI','Malgun Gothic',sans-serif;letter-spacing:-.012em;white-space:nowrap}
            #title{min-width:0;max-width:100%;overflow:hidden;text-overflow:ellipsis}.katex{font-size:1em}
          </style>
        </head>
        <body><div id="title">{{safeTitle}}</div>
          <script>
            if (window.renderMathInElement) renderMathInElement(document.getElementById('title'), {
              delimiters: [
                { left: '$$', right: '$$', display: false },
                { left: '\\[', right: '\\]', display: false },
                { left: '$', right: '$', display: false },
                { left: '\\(', right: '\\)', display: false }
              ],
              throwOnError: false, strict: false, trust: false
            });
          </script>
        </body>
        </html>
        """;
    }

    public static string RenderNotebook(string path, string vaultPath, double scrollY = 0,
        double fontScale = 1, string accentColor = "#D1AF61", string surfaceTheme = "dark")
    {
        var body = NotebookPreviewRenderer.RenderBody(NotebookDocumentService.ReadDocument(path), vaultPath, path);
        if (!Regex.IsMatch(accentColor, "^#[0-9a-fA-F]{6}$")) accentColor = "#D1AF61";
        return HtmlShell(body, null, scrollY, Math.Clamp(fontScale, .8, 1.4), accentColor, surfaceTheme, null);
    }

    private static string RenderBody(string markdown, string vaultPath, Func<string, string?>? resolveNote, int depth, PreviewEditSession? editSession = null)
    {
        markdown = MarkdownText.NormalizeNewlines(markdown);
        var prepared = Prepare(markdown, vaultPath, resolveNote, depth);
        var document = Markdown.Parse(prepared.Text, Pipeline);
        if (depth == 0)
        {
            foreach (var block in document.Descendants<Block>())
            {
                if (block.Line < 0 || block.Line >= prepared.SourceOffsets.Count) continue;
                var attributes = block.GetAttributes();
                attributes.AddClass("source-position");
                attributes.AddProperty("data-source-offset", prepared.SourceOffsets[block.Line].ToString());
            }
        }
        if (editSession is null) return Markdown.ToHtml(document, Pipeline);
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.ObjectRenderers.Insert(0, new EditableLiteralRenderer(markdown, prepared, editSession));
        renderer.Render(document);
        return writer.ToString();
    }

    private sealed class EditableLiteralRenderer(string source, PreparedMarkdown prepared, PreviewEditSession session)
        : HtmlObjectRenderer<LiteralInline>
    {
        private readonly string[] _lines = prepared.Text.Split('\n');
        private readonly int[] _lineStarts = GetLineStarts(prepared.Text);
        private readonly HashSet<LiteralInline> _combined = [];

        private static int[] GetLineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (var index = 0; index < text.Length; index++)
                if (text[index] == '\n') starts.Add(index + 1);
            return starts.ToArray();
        }

        protected override void Write(HtmlRenderer renderer, LiteralInline literal)
        {
            if (_combined.Contains(literal)) return;
            var text = literal.Content.ToString();
            var start = EditableStart(literal, text);
            if (start >= 0)
            {
                var length = literal.Span.Length;
                // Markdig splits escaped punctuation into adjacent literals; keep one editing field.
                for (var next = literal.NextSibling as LiteralInline; next is not null; next = next.NextSibling as LiteralInline)
                {
                    var nextText = next.Content.ToString();
                    if (EditableStart(next, nextText) != start + length) break;
                    text += nextText;
                    length += next.Span.Length;
                    _combined.Add(next);
                }
                session.Register(start, source.Substring(start, length));
                renderer.Write($"<span class=\"editable-text\" data-edit-start=\"{start}\" data-edit-length=\"{length}\" title=\"더블클릭하여 텍스트 수정 · Alt+클릭으로 원문 이동\">");
            }
            renderer.WriteEscape(text);
            if (start >= 0) renderer.Write("</span>");
        }

        private int EditableStart(LiteralInline literal, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || literal.Line < 0 || literal.Line >= prepared.SourceOffsets.Count)
                return -1;
            for (var parent = literal.Parent; parent is not null; parent = parent.Parent)
                if (parent.GetType() != typeof(ContainerInline) && parent is not EmphasisInline) return -1;
            var container = literal.Parent;
            while (container?.Parent is not null) container = container.Parent;
            if (container?.ParentBlock is not (ParagraphBlock or HeadingBlock)) return -1;
            var line = _lines[literal.Line];
            // Transclusions, callouts, HTML and math have transformed coordinates or their own renderer.
            if (line.IndexOfAny(['$', '<']) >= 0) return -1;
            var originalLineStart = prepared.SourceOffsets[literal.Line];
            var originalLineEnd = source.IndexOf('\n', originalLineStart);
            if (originalLineEnd < 0) originalLineEnd = source.Length;
            if (source[originalLineStart..originalLineEnd] != line) return -1;
            var column = literal.Span.Start - _lineStarts[literal.Line];
            if (column < 0 || literal.Span.Length <= 0 || column + literal.Span.Length > line.Length) return -1;
            var raw = line.Substring(column, literal.Span.Length);
            return PreviewEditSession.DecodeLiteral(raw) == text ? originalLineStart + column : -1;
        }
    }

    private sealed record PreparedMarkdown(string Text, IReadOnlyList<int> SourceOffsets);

    private static PreparedMarkdown Prepare(string markdown, string vaultPath, Func<string, string?>? resolveNote, int depth)
    {
        var lines = markdown.Split('\n');
        var lineOffsets = new int[lines.Length];
        var sourceOffset = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            lineOffsets[index] = sourceOffset;
            sourceOffset += lines[index].Length + 1;
        }
        var output = new StringBuilder();
        var sourceOffsets = new List<int>();
        // Parse fences/HTML/front matter before preprocessing, including nested and long fences.
        var protectedLines = new HashSet<int>();
        foreach (var block in Markdown.Parse(markdown, Pipeline).Descendants<Block>())
        {
            if (block is not (FencedCodeBlock or HtmlBlock or Markdig.Extensions.Yaml.YamlFrontMatterBlock)) continue;
            var last = block.Line + markdown.AsSpan(block.Span.Start, block.Span.Length).Count('\n');
            for (var lineIndex = block.Line; lineIndex <= last; lineIndex++) protectedLines.Add(lineIndex);
        }
        var comment = false;
        var mathBlock = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();
            if (protectedLines.Contains(index))
            {
                AppendMappedLine(output, sourceOffsets, line, lineOffsets[index]);
                continue;
            }
            if (trimmed is "$$" or "\\[" or "\\]") mathBlock = !mathBlock;
            if (!comment && !mathBlock && TextDiagramRenderer.TryRead(lines, index, out var diagramEnd)
                && !Enumerable.Range(index, diagramEnd - index).Any(protectedLines.Contains))
            {
                var diagram = string.Join('\n', lines[index..diagramEnd]);
                var endOffset = lineOffsets[diagramEnd - 1] + lines[diagramEnd - 1].Length;
                // Empty lines isolate raw HTML even when the source has no surrounding blank lines.
                AppendMappedLine(output, sourceOffsets, "", lineOffsets[index]);
                AppendMappedLine(output, sourceOffsets, TextDiagramRenderer.Render(diagram,
                    depth == 0 ? lineOffsets[index] : null, depth == 0 ? endOffset : null), lineOffsets[index]);
                AppendMappedLine(output, sourceOffsets, "", endOffset);
                index = diagramEnd - 1;
                continue;
            }

            line = NormalizeMathDelimiters(line);
            line = RemoveComments(line, ref comment);
            var callout = Regex.Match(line, "^>\\s*\\[!([a-zA-Z0-9_-]+)\\]([+-])?\\s*(.*)$");
            if (callout.Success)
            {
                var calloutSourceOffset = lineOffsets[index];
                var body = new StringBuilder();
                while (index + 1 < lines.Length && Regex.IsMatch(lines[index + 1], "^>"))
                {
                    index++;
                    body.AppendLine(Regex.Replace(lines[index], "^>\\s?", ""));
                }
                var type = callout.Groups[1].Value.ToLowerInvariant();
                var title = callout.Groups[3].Value.Trim();
                if (title.Length == 0) title = char.ToUpperInvariant(type[0]) + type[1..];
                var inner = depth < 4 ? RenderBody(body.ToString(), vaultPath, resolveNote, depth + 1) : WebUtility.HtmlEncode(body.ToString());
                var fold = callout.Groups[2].Value;
                if (fold.Length > 0)
                    AppendMappedLine(output, sourceOffsets, $"<details class=\"callout\" data-source-offset=\"{calloutSourceOffset}\" data-callout=\"{WebUtility.HtmlEncode(type)}\"{(fold == "+" ? " open" : "")}><summary>{WebUtility.HtmlEncode(title)}</summary><div class=\"callout-content\">{inner}</div></details>", calloutSourceOffset);
                else
                    AppendMappedLine(output, sourceOffsets, $"<aside class=\"callout\" data-source-offset=\"{calloutSourceOffset}\" data-callout=\"{WebUtility.HtmlEncode(type)}\"><div class=\"callout-title\">{WebUtility.HtmlEncode(title)}</div><div class=\"callout-content\">{inner}</div></aside>", calloutSourceOffset);
                continue;
            }
            AppendMappedLine(output, sourceOffsets, TransformWikiLinks(line, vaultPath, resolveNote, depth), lineOffsets[index]);
        }
        return new PreparedMarkdown(output.ToString(), sourceOffsets);
    }

    private static void AppendMappedLine(StringBuilder output, List<int> sourceOffsets, string text, int sourceOffset)
    {
        var normalized = MarkdownText.NormalizeNewlines(text);
        output.Append(normalized).Append('\n');
        for (var index = 0; index <= normalized.Count(character => character == '\n'); index++)
            sourceOffsets.Add(sourceOffset);
    }

    private static string NormalizeMathDelimiters(string line)
    {
        var blockStart = Regex.Match(line, "^(\\s*)\\\\\\[\\s*$");
        if (blockStart.Success) return blockStart.Groups[1].Value + "$$";
        var blockEnd = Regex.Match(line, "^(\\s*)\\\\\\]\\s*$");
        if (blockEnd.Success) return blockEnd.Groups[1].Value + "$$";
        return Regex.Replace(line, "\\\\\\((.+?)\\\\\\)", match => "$" + match.Groups[1].Value + "$");
    }

    private static string RemoveComments(string line, ref bool inComment)
    {
        var output = new StringBuilder();
        var inlineCode = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] == '`') { inlineCode = !inlineCode; if (!inComment) output.Append(line[index]); continue; }
            if (!inlineCode && index + 1 < line.Length && line[index] == '%' && line[index + 1] == '%') { inComment = !inComment; index++; continue; }
            if (!inComment) output.Append(line[index]);
        }
        return output.ToString();
    }

    private static string TransformWikiLinks(string line, string vaultPath, Func<string, string?>? resolveNote, int depth)
    {
        return Regex.Replace(line, "(`+[^`]*`+)|(!?\\[\\[([^\\]]+)\\]\\])", match =>
        {
            if (match.Value.StartsWith('`')) return match.Value;
            var embed = match.Value.StartsWith('!');
            var parts = match.Groups[3].Value.Split('|', 2);
            var target = parts[0].Trim();
            var noteName = target.Split('#')[0];
            if (noteName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) noteName = noteName[..^3];
            var label = parts.Length > 1 ? parts[1].Trim() : target;
            if (!embed) return $"<a class=\"internal-link\" href=\"node-note://note/{Uri.EscapeDataString(noteName)}\">{WebUtility.HtmlEncode(label)}</a>";

            if (IsImage(target) && TryImageSource(vaultPath, target, out var source))
            {
                var dimensions = parts.Length > 1 ? ImageDimensionAttributes(parts[1]) : "";
                return $"<img class=\"internal-image\" src=\"{WebUtility.HtmlEncode(source)}\" alt=\"{WebUtility.HtmlEncode(noteName)}\"{dimensions}>";
            }
            var body = depth < 3 ? resolveNote?.Invoke(noteName) : null;
            return body is null
                ? $"<span class=\"missing-embed\">![[{WebUtility.HtmlEncode(label)}]]</span>"
                : $"<section class=\"note-embed\"><header>{WebUtility.HtmlEncode(label)}</header>{RenderBody(body, vaultPath, resolveNote, depth + 1)}</section>";
        });
    }

    private static string ImageDimensionAttributes(string value)
    {
        var match = Regex.Match(value, "^\\s*(\\d{1,5})(?:\\s*[xX×]\\s*(\\d{1,5}))?\\s*$");
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, out var width)
            || width is < 1 or > 8192)
            return "";

        if (!match.Groups[2].Success) return $" width=\"{width}\"";
        if (!int.TryParse(match.Groups[2].Value, out var height)
            || height is < 1 or > 8192)
            return "";
        return $" width=\"{width}\" height=\"{height}\"";
    }

    private static bool IsImage(string target) => new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg" }.Contains(Path.GetExtension(target.Split('#')[0]), StringComparer.OrdinalIgnoreCase);

    private static bool TryImageSource(string vaultPath, string target, out string source)
    {
        source = "";
        try
        {
            var name = target.Split('#')[0];
            var root = Path.GetFullPath(vaultPath).TrimEnd(Path.DirectorySeparatorChar);
            var rootPrefix = root + Path.DirectorySeparatorChar;
            var direct = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            var path = direct.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) && File.Exists(direct)
                ? direct
                : Directory.EnumerateFiles(root, Path.GetFileName(name), SearchOption.AllDirectories).FirstOrDefault();
            if (path is null) return false;
            var relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
            var encodedPath = string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));
            source = $"https://{VaultAssetHostName}/{encodedPath}";
            return true;
        }
        catch { return false; }
    }

    private static string HtmlShell(
        string body,
        IReadOnlyDictionary<string, bool>? foldStates,
        double initialScrollY,
        double fontScale,
        string accentColor,
        string surfaceTheme,
        string? editSessionId)
    {
        var surface = CssSurfaceFor(surfaceTheme);
        var serializedFoldStates = JsonSerializer.Serialize(foldStates ?? new Dictionary<string, bool>());
        var serializedEditSession = JsonSerializer.Serialize(editSessionId);
        var serializedScrollY = JsonSerializer.Serialize(double.IsFinite(initialScrollY) && initialScrollY > 0 ? initialScrollY : 0);
        var serializedFontScale = fontScale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var colorScheme = surface.IsLight ? "light" : "dark";
        var page = surface.DocumentBackground;
        var app = surface.AppBackground;
        var card = surface.CardBackground;
        var border = surface.Border;
        var primary = surface.PrimaryText;
        var secondary = surface.SecondaryText;
        var placeholder = surface.PlaceholderText;
        var hover = surface.HoverBackground;
        var pressed = surface.PressedBackground;
        var scroll = surface.ScrollThumb;
        var scrollHover = surface.ScrollThumbHover;
        var scrollPressed = surface.ScrollThumbPressed;
        var strong = surface.IsLight ? "#111111" : "#F0F0F0";
        var syntaxKeyword = surface.IsLight ? "#AF00DB" : "#C586C0";
        var syntaxType = surface.IsLight ? "#267F99" : "#4EC9B0";
        var syntaxFunction = surface.IsLight ? "#795E26" : "#DCDCAA";
        var syntaxString = surface.IsLight ? "#A31515" : "#CE9178";
        var syntaxNumber = surface.IsLight ? "#098658" : "#B5CEA8";
        var syntaxComment = surface.IsLight ? "#008000" : "#6A9955";
        return $$"""
        <!doctype html>
        <html>
        <head>
          <meta charset="utf-8">
          <meta name="color-scheme" content="dark">
          <link rel="stylesheet" href="https://node-assets.local/katex.min.css">
          <script src="https://node-assets.local/katex.min.js"></script>
          <script src="https://node-assets.local/auto-render.min.js"></script>
          <style>
            :root{--font-scale:{{serializedFontScale}};--accent:{{accentColor}};--page:{{page}};--app:{{app}};--card:{{card}};--border:{{border}};--primary:{{primary}};--secondary:{{secondary}};--placeholder:{{placeholder}};--hover:{{hover}};--pressed:{{pressed}};--strong:{{strong}}}*{box-sizing:border-box}html{background:var(--page);color-scheme:{{colorScheme}};scrollbar-color:{{scroll}} transparent;scrollbar-width:thin}
            *::-webkit-scrollbar{width:8px;height:8px}*::-webkit-scrollbar-track{background:transparent}*::-webkit-scrollbar-thumb{min-height:28px;background:{{scroll}};background-clip:padding-box;border:2px solid transparent;border-radius:999px}*::-webkit-scrollbar-thumb:hover{background:{{scrollHover}};background-clip:padding-box}*::-webkit-scrollbar-thumb:active{background:{{scrollPressed}};background-clip:padding-box}*::-webkit-scrollbar-button{width:0;height:0;background:transparent}*::-webkit-scrollbar-corner{background:transparent}
            body{margin:0 auto;max-width:820px;padding:22px 34px 72px;color:var(--primary);background:var(--page);font:calc(10.5px * var(--font-scale))/1.72 'Segoe UI Variable Text','Segoe UI','Malgun Gothic',sans-serif;word-break:keep-all;overflow-wrap:anywhere}
            h1,h2,h3,h4,h5,h6{color:var(--strong);font-weight:700;letter-spacing:-.012em}
            h1{font-size:calc(13.3px * var(--font-scale));line-height:1.32;margin:0 0 20px}h2{font-size:calc(11.9px * var(--font-scale));line-height:1.38;margin:30px 0 11px;padding-bottom:7px;border-bottom:1px solid #303030}h3{font-size:calc(10.5px * var(--font-scale));line-height:1.45;margin:24px 0 8px}h4{font-size:calc(9.8px * var(--font-scale));margin:20px 0 7px}h5{font-size:calc(9.1px * var(--font-scale));margin:18px 0 6px}h6{font-size:calc(8.4px * var(--font-scale));margin:16px 0 5px}
            p{margin:0 0 14px}strong{font-weight:700;color:var(--strong)}em{color:var(--secondary)}ul,ol{margin:5px 0 18px;padding-left:24px}li{margin:4px 0;padding-left:2px}li>p{margin:0}
            a{color:var(--primary);text-decoration:underline;text-decoration-color:var(--secondary)}.internal-link{color:var(--accent);font-weight:600;text-decoration:none}.internal-link:hover,a:hover{text-decoration:underline}mark{background:color-mix(in srgb,var(--accent) 28%,var(--card));color:var(--strong);padding:1px 3px;border-radius:2px}
            blockquote{margin:18px 0;padding:10px 15px;border-left:3px solid var(--secondary);color:var(--secondary);background:var(--card);border-radius:0 5px 5px 0}blockquote>:last-child{margin-bottom:0}hr{border:0;border-top:1px solid var(--border);margin:28px 0}
            table{border-collapse:collapse;width:max-content;max-width:100%;margin:18px 0 24px;font-size:calc(9.8px * var(--font-scale))}thead{border-bottom:1px solid var(--border)}th,td{padding:9px 10px;text-align:left;vertical-align:top;border-bottom:1px solid var(--border)}th{color:var(--strong);font-weight:700;background:var(--card)}tbody tr:hover{background:var(--hover)}
            pre{position:relative;overflow:auto;margin:18px 0 22px;padding:25px 18px 17px;background:var(--app);border:1px solid var(--border);border-radius:8px;white-space:pre-wrap;word-break:normal}pre::before{content:attr(data-language);position:absolute;top:6px;right:9px;color:var(--placeholder);font:calc(7.7px * var(--font-scale))/1 'Cascadia Mono','Consolas',monospace;letter-spacing:.06em}code{font:calc(9.1px * var(--font-scale))/1.62 'Cascadia Mono','Consolas',monospace}p code,li code,td code{padding:2px 5px;color:var(--primary);background:var(--hover);border-radius:3px;white-space:normal}.tok-keyword{color:{{syntaxKeyword}}}.tok-type{color:{{syntaxType}}}.tok-function{color:{{syntaxFunction}}}.tok-string{color:{{syntaxString}}}.tok-number{color:{{syntaxNumber}}}.tok-comment{color:{{syntaxComment}};font-style:italic;opacity:.82}.tok-operator{color:var(--primary)}
            .math{max-width:100%;overflow-x:auto;overflow-y:hidden}.katex-display{margin:18px 0;overflow-x:auto;overflow-y:hidden;padding:3px 0}.katex{font-size:1.05em}
            .task-list-item{list-style:none}.task-list-item input{width:14px;height:14px;margin:0 8px 0 -22px;accent-color:var(--accent)}.callout{display:block;margin:18px 0;padding:13px 15px;border:1px solid var(--border);border-left:4px solid var(--secondary);border-radius:6px;background:var(--card)}.callout-title,.callout summary{font-weight:700;color:var(--primary)}.callout-content>:last-child{margin-bottom:0}
            .note-embed{margin:18px 0;padding:14px 16px;border:1px solid var(--border);border-radius:6px;background:var(--card)}.note-embed>header{margin-bottom:10px;color:var(--accent);font-weight:700}.internal-image{display:block;max-width:100%;height:auto;margin:18px auto;border-radius:6px}.missing-embed{color:#B47835}.footnotes{font-size:calc(9.1px * var(--font-scale));color:var(--secondary)}
            .fold-tools{position:sticky;top:0;z-index:10;display:flex;justify-content:flex-end;gap:6px;margin:0 0 14px;padding:7px 0;background:var(--page);backdrop-filter:blur(8px)}
            .fold-tools button{appearance:none;border:1px solid var(--border);border-radius:4px;background:var(--card);color:var(--primary);padding:5px 9px;font:calc(8.4px * var(--font-scale)) 'Segoe UI Variable Text','Segoe UI',sans-serif;cursor:pointer}.fold-tools button:hover{background:var(--pressed)}
            .md-section{margin:0;border-bottom:1px solid var(--border)}.md-section>.md-summary{display:flex;align-items:flex-start;gap:8px;padding:2px 0;list-style:none;cursor:pointer;user-select:none}.md-section>.md-summary::-webkit-details-marker{display:none}.md-section>.md-summary::before{content:'›';flex:0 0 13px;margin-top:7px;color:var(--secondary);font-size:12.6px;line-height:1;transition:transform .14s ease}.md-section[open]>.md-summary::before{transform:rotate(90deg)}
            .md-section>.md-summary>h1,.md-section>.md-summary>h2,.md-section>.md-summary>h3,.md-section>.md-summary>h4,.md-section>.md-summary>h5,.md-section>.md-summary>h6{flex:1;margin:0;padding:7px 0;border:0}.md-section[data-level='1']>.md-summary>h1{font-size:calc(13.3px * var(--font-scale))}.md-section[data-level='2']>.md-summary>h2{font-size:calc(11.9px * var(--font-scale))}.md-section[data-level='3']>.md-summary>h3{font-size:calc(10.5px * var(--font-scale))}.md-section[data-level='4']>.md-summary>h4{font-size:calc(9.8px * var(--font-scale))}.md-section[data-level='5']>.md-summary>h5{font-size:calc(9.1px * var(--font-scale))}.md-section[data-level='6']>.md-summary>h6{font-size:calc(8.4px * var(--font-scale))}
            .md-section>.md-section-body{padding:11px 0 15px 21px}.md-section>.md-section-body>.md-section{border-bottom:0;border-top:1px solid var(--border)}.md-section>.md-section-body>:last-child{margin-bottom:0}
            .source-hover{outline:1px solid color-mix(in srgb,var(--accent) 45%,transparent);outline-offset:3px;border-radius:3px;cursor:text}
            .editable-text{cursor:text}.inline-text-input{font:inherit;color:inherit;background:var(--card);border:1px solid var(--accent);border-radius:3px;padding:2px 4px;max-width:100%;outline:none}.inline-edit-help{position:fixed;bottom:10px;left:10px;z-index:100;background:var(--card);color:var(--primary);border:1px solid var(--border);border-radius:5px;padding:6px 10px;font:12px 'Segoe UI',sans-serif}
            pre.text-diagram{padding:18px 20px;max-width:100%;overflow-x:auto;white-space:pre;overflow-wrap:normal;word-break:normal;tab-size:4}pre.text-diagram::before{content:none}pre.text-diagram code{font-family:'D2Coding','NanumGothicCoding','Cascadia Mono','Consolas','GulimChe',monospace;font-size:calc(10.5px * var(--font-scale));line-height:1.6;font-variant-ligatures:none;letter-spacing:0;white-space:pre;overflow-wrap:normal;word-break:normal}.diagram-connector{color:var(--secondary)}.diagram-wide{display:inline-block;text-align:center}
            .notebook-info,.notebook-prompt{color:var(--secondary);font-size:calc(9px * var(--font-scale));margin:0 0 12px}.notebook-cell{margin:20px 0;padding:0 0 18px;border-bottom:1px solid var(--border)}.notebook-cell>pre{white-space:pre;overflow-wrap:normal}.notebook-cell img{display:block;max-width:100%;height:auto}.notebook-output{padding:14px 16px;background:var(--card);white-space:pre!important;font-family:'Cascadia Mono','Consolas',monospace;overflow:auto}.notebook-output::before{content:none}.notebook-error{color:#D47878}.notebook-html-output{width:100%;height:320px;max-height:55vh;border:1px solid var(--border);border-radius:6px;background:white}
            @media(max-width:700px){body{padding:18px 20px 56px}table{font-size:calc(8.4px * var(--font-scale))}th,td{padding:7px 6px}.md-section>.md-section-body{padding-left:17px} }
          </style>
        </head>
        <body>
          {{body}}
          <script>
            (() => {
              const root = document.body;
              const initialFoldStates = {{serializedFoldStates}};
              const initialScrollY = {{serializedScrollY}};
              const editSession = {{serializedEditSession}};
              let activeEdit = null;
              const finishEdit = commit => {
                if (!activeEdit) return;
                const { span, input, original, help } = activeEdit;
                const value = input.value;
                if (commit && (!value.trim() || /[\r\n\t$]/.test(value))) {
                  help.textContent = '빈 내용·줄바꿈·수식은 아래 원문 편집창에서 수정해주세요. Esc: 취소';
                  input.focus();
                  return;
                }
                activeEdit = null;
                span.textContent = original;
                help.remove();
                if (commit && value !== original) {
                  window.chrome.webview.postMessage({ type: 'inline-edit', session: editSession, start: Number(span.dataset.editStart), text: value });
                }
              };
              document.addEventListener('keydown', event => {
                if (activeEdit) {
                  if (event.isComposing) return;
                  if (event.key === 'Escape' || (event.key === 'Enter' && event.ctrlKey)) {
                    event.preventDefault();
                    event.stopPropagation();
                    finishEdit(event.key !== 'Escape');
                  }
                  return;
                }
                if (event.ctrlKey && !event.altKey && !event.metaKey && event.key.toLowerCase() === 'g') {
                  event.preventDefault();
                  event.stopPropagation();
                  window.chrome.webview.postMessage({ type: 'workspace-mode-toggle' });
                } else if (event.key === 'Escape') {
                  window.chrome.webview.postMessage({ type: 'workspace-mode-document' });
                }
              }, true);
              const originalNodes = Array.from(root.childNodes);
              const stack = [];
              const foldKeyCounts = new Map();
              let sectionCount = 0;
              for (const node of originalNodes) {
                const heading = node.nodeType === Node.ELEMENT_NODE && /^H[1-6]$/.test(node.tagName) ? node : null;
                if (heading) {
                  const level = Number(heading.tagName.substring(1));
                  while (stack.length && stack[stack.length - 1].level >= level) stack.pop();
                  const details = document.createElement('details');
                  details.className = 'md-section';
                  const foldKeyBase = `${level}:${heading.textContent.trim()}`;
                  const foldKeyCount = (foldKeyCounts.get(foldKeyBase) || 0) + 1;
                  foldKeyCounts.set(foldKeyBase, foldKeyCount);
                  const foldKey = `${foldKeyBase}#${foldKeyCount}`;
                  details.dataset.foldKey = foldKey;
                  details.open = Object.hasOwn(initialFoldStates, foldKey) ? initialFoldStates[foldKey] : true;
                  details.addEventListener('toggle', () => {
                    window.chrome.webview.postMessage({ type: 'fold-state', key: foldKey, open: details.open });
                  });
                  details.dataset.level = String(level);
                  const summary = document.createElement('summary');
                  summary.className = 'md-summary';
                  const sectionBody = document.createElement('div');
                  sectionBody.className = 'md-section-body';
                  heading.title = '클릭해서 접기 또는 펼치기';
                  summary.appendChild(heading);
                  details.append(summary, sectionBody);
                  (stack.length ? stack[stack.length - 1].body : root).appendChild(details);
                  stack.push({ level, body: sectionBody });
                  sectionCount++;
                } else {
                  (stack.length ? stack[stack.length - 1].body : root).appendChild(node);
                }
              }
              const sourceTarget = event => {
                const element = event.target instanceof Element ? event.target : event.target.parentElement;
                if (activeEdit || !element || element.closest('.fold-tools,a,button,input,textarea,select,summary,img')) return null;
                if (window.getSelection()?.toString()) return null;
                return element.closest('.editable-text') ?? element.closest('[data-source-offset]');
              };
              const sourceOffsets = Array.from(new Set(Array.from(document.querySelectorAll('[data-source-offset]'))
                .map(element => Number(element.dataset.sourceOffset))
                .filter(offset => Number.isInteger(offset) && offset >= 0)))
                .sort((left, right) => left - right);
              const postEditorHover = element => {
                if (!element) {
                  window.chrome.webview.postMessage({ type: 'hover-editor-clear' });
                  return;
                }
                const offset = Number(element.dataset.editStart ?? element.dataset.sourceOffset);
                if (!Number.isInteger(offset) || offset < 0) return;
                const endOffset = element.dataset.editLength ? offset + Number(element.dataset.editLength) : element.dataset.sourceEnd ? Number(element.dataset.sourceEnd) : sourceOffsets.find(candidate => candidate > offset) ?? -1;
                window.chrome.webview.postMessage({ type: 'hover-editor', offset, endOffset });
              };
              let hoveredSource = null;
              document.addEventListener('pointermove', event => {
                const next = sourceTarget(event);
                if (next === hoveredSource) return;
                hoveredSource?.classList.remove('source-hover');
                hoveredSource = next;
                hoveredSource?.classList.add('source-hover');
                postEditorHover(hoveredSource);
              }, { passive: true });
              document.addEventListener('pointerleave', () => {
                hoveredSource?.classList.remove('source-hover');
                hoveredSource = null;
                postEditorHover(null);
              });
              document.addEventListener('click', event => {
                if (activeEdit || event.target.closest?.('.inline-text-input')) return;
                if (event.target.closest?.('.editable-text') && !event.altKey) {
                  event.preventDefault();
                  return;
                }
                const element = event.altKey ? event.target.closest?.('.editable-text') ?? sourceTarget(event) : sourceTarget(event);
                if (!element) return;
                const offset = Number(element.dataset.editStart ?? element.dataset.sourceOffset);
                if (!Number.isInteger(offset) || offset < 0) return;
                event.preventDefault();
                event.stopPropagation();
                window.chrome.webview.postMessage({ type: 'focus-editor', offset });
              });
              document.addEventListener('dblclick', event => {
                const span = event.target.closest?.('.editable-text');
                if (!editSession || !span || activeEdit || span.closest('a,pre,code,.katex')) return;
                event.preventDefault();
                event.stopPropagation();
                hoveredSource?.classList.remove('source-hover');
                hoveredSource = null;
                postEditorHover(null);
                const original = span.textContent;
                const width = Math.max(140, span.getBoundingClientRect().width + 24);
                const input = document.createElement('input');
                input.type = 'text';
                input.className = 'inline-text-input';
                input.setAttribute('aria-label', '미리보기 텍스트 수정');
                input.value = original;
                input.maxLength = 16000;
                input.style.width = `${width}px`;
                const help = document.createElement('div');
                help.className = 'inline-edit-help';
                help.setAttribute('role', 'status');
                help.textContent = '텍스트 수정 · Ctrl+Enter 또는 바깥 클릭: 반영 · Esc: 취소';
                activeEdit = { span, input, original, help };
                span.replaceChildren(input);
                root.append(help);
                input.addEventListener('blur', () => finishEdit(true));
                input.focus();
                input.select();
              });
              const elementOf = node => node instanceof Element ? node : node?.parentElement;
              document.addEventListener('copy', event => {
                const selection = window.getSelection();
                if (!selection || selection.isCollapsed) return;
                const startPre = elementOf(selection.anchorNode)?.closest('pre');
                const endPre = elementOf(selection.focusNode)?.closest('pre');
                if (!startPre || startPre !== endPre) return;
                event.preventDefault();
                if (startPre.classList.contains('text-diagram')) {
                  event.clipboardData.setData('text/plain', selection.getRangeAt(0).cloneContents().textContent);
                  return;
                }
                event.clipboardData.setData('text/plain', selection.toString());
              });
              if (sectionCount) {
                const tools = document.createElement('nav');
                tools.className = 'fold-tools';
                tools.setAttribute('aria-label', '문서 접기 도구');
                const expand = document.createElement('button');
                expand.type = 'button';
                expand.textContent = '모두 펼치기';
                expand.addEventListener('click', () => document.querySelectorAll('.md-section').forEach(section => section.open = true));
                const collapse = document.createElement('button');
                collapse.type = 'button';
                collapse.textContent = '모두 접기';
                collapse.addEventListener('click', () => document.querySelectorAll('.md-section').forEach(section => section.open = false));
                tools.append(expand, collapse);
                root.prepend(tools);
              }

              if (window.renderMathInElement) {
                window.renderMathInElement(root, {
                  delimiters: [
                    { left: '$$', right: '$$', display: true },
                    { left: '\\[', right: '\\]', display: true },
                    { left: '$', right: '$', display: false },
                    { left: '\\(', right: '\\)', display: false }
                  ],
                  throwOnError: false,
                  strict: false,
                  trust: false
                });
              }

              window.scrollTo(0, initialScrollY);
              requestAnimationFrame(() => {
                window.scrollTo(0, initialScrollY);
                let scrollFrame = 0;
                window.addEventListener('scroll', () => {
                  if (scrollFrame) return;
                  scrollFrame = requestAnimationFrame(() => {
                    scrollFrame = 0;
                    const maxY = Math.max(0, document.documentElement.scrollHeight - window.innerHeight);
                    window.chrome.webview.postMessage({ type: 'preview-scroll', y: window.scrollY, maxY });
                  });
                }, { passive: true });
              });
            })();
          </script>
        </body>
        </html>
        """;
    }

    private sealed record CssSurface(
        bool IsLight,
        string AppBackground,
        string DocumentBackground,
        string CardBackground,
        string Border,
        string PrimaryText,
        string SecondaryText,
        string PlaceholderText,
        string HoverBackground,
        string PressedBackground,
        string ScrollThumb,
        string ScrollThumbHover,
        string ScrollThumbPressed);

    private static CssSurface CssSurfaceFor(string? key) => key?.ToLowerInvariant() switch
    {
        "light" => new(true, "#F3F3F3", "#FFFFFF", "#F5F5F5", "#D8D8D8", "#252525", "#686868", "#7A7A7A", "#EAEAEA", "#E0E0E0", "#A8A8A8", "#8E8E8E", "#777777"),
        "midnight" => new(false, "#0F1115", "#12151A", "#1B1F26", "#2A3039", "#D9DDE5", "#929AA7", "#7F8793", "#20252D", "#292F39", "#4B5563", "#667180", "#788493"),
        _ => new(false, "#181818", "#1E1E1E", "#252526", "#303030", "#D4D4D4", "#969696", "#858585", "#282828", "#333333", "#555555", "#707070", "#808080")
    };
}
