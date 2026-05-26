using System.Net;
using System.Text;
using CopilotChatbot.Models;
using Markdig;

namespace CopilotChatbot.Services;

public sealed class HtmlRenderer
{
    private readonly MarkdownPipeline _markdown = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    // All theme-sensitive colours in one place.
    // Used by RenderDocument (baked into :root) and GetThemeUpdateScript (injected via JS).
    private static Dictionary<string, string> GetThemeVarMap(bool dark) => new()
    {
        ["--bg"]          = dark ? "#111827" : "#FFFFFF",
        ["--card"]        = dark ? "#161B22" : "#FFFFFF",
        ["--border"]      = dark ? "#30363D" : "#D0D7DE",
        ["--text"]        = dark ? "#E6EDF3" : "#1F2328",
        ["--muted"]       = dark ? "#8B949E" : "#57606A",
        ["--btn-bg"]      = dark ? "#21262D" : "#F6F8FA",
        ["--icon-dim"]    = dark ? "rgba(230,237,243,0.45)" : "rgba(87,96,106,0.50)",
        ["--icon-hover"]  = dark ? "#E6EDF3" : "#1F2328",
        ["--code-bg"]     = dark ? "#0D1117" : "#F6F8FA",
        ["--th-bg"]       = dark ? "#21262D" : "#F0F3F6",
        ["--tr-even"]     = dark ? "#0D1117" : "#F8FAFC",
        ["--link"]        = dark ? "#58A6FF" : "#0969DA",
        ["--user-head"]   = dark ? "#0D2A4D" : "#EFF6FF",
        ["--user-avatar"] = dark ? "#1B4F8A" : "#3B82F6",
        ["--user-label"]  = dark ? "#60A5FA" : "#1D4ED8",
        ["--asst-head"]   = dark ? "#0D2E1F" : "#F0FDF4",
        ["--asst-avatar"] = dark ? "#166534" : "#22C55E",
        ["--asst-label"]  = dark ? "#4ADE80" : "#15803D",
        ["--rsn-head"]    = dark ? "#2D1F00" : "#FFFBEB",
        ["--rsn-avatar"]  = dark ? "#854D0E" : "#F59E0B",
        ["--rsn-label"]   = dark ? "#FCD34D" : "#92400E",
        ["--tool-head"]   = dark ? "#1E1040" : "#F5F3FF",
        ["--tool-avatar"] = dark ? "#6D28D9" : "#8B5CF6",
        ["--tool-label"]  = dark ? "#A78BFA" : "#5B21B6",
        ["--err-head"]    = dark ? "#2D0E0E" : "#FFF5F5",
        ["--err-avatar"]  = dark ? "#9B1C1C" : "#EF4444",
        ["--err-label"]   = dark ? "#FCA5A5" : "#991B1B",
        ["--sys-head"]    = dark ? "#1C2128" : "#F6F8FA",
        ["--sys-avatar"]  = dark ? "#484F58" : "#6E7781",
    };

    /// <summary>Returns a JS snippet that updates all CSS custom properties on the root element
    /// without reloading the page — preserving scroll position and <details> open state.</summary>
    public string GetThemeUpdateScript(bool dark)
    {
        var sb = new StringBuilder("(function(){var s=document.documentElement.style;");
        foreach (var (k, v) in GetThemeVarMap(dark))
            sb.Append($"s.setProperty('{k}','{v}');");
        sb.Append("})();");
        return sb.ToString();
    }

    public string RenderDocument(IEnumerable<ChatMessage> messages, bool darkTheme)
    {
        var messagesHtml = RenderBody(messages, darkTheme);
        var rootVars = ":root{" + string.Join("", GetThemeVarMap(darkTheme).Select(kv => $"{kv.Key}:{kv.Value};")) + "}";

        return $$"""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<style>
{{rootVars}}
*, *::before, *::after { box-sizing: border-box; }
html, body { margin:0; padding:0; background:var(--bg); color:var(--text); font-family:-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; }
main { padding:10px 14px; }
.msg { margin:0 0 7px 0; border:1px solid var(--border); border-radius:8px; background:var(--card); overflow:hidden; box-shadow:0 1px 2px rgba(0,0,0,.07); }
.head, details > summary.head { display:flex; align-items:center; gap:5px; padding:3px 8px; border-bottom:1px solid var(--border); font-size:11px; color:var(--muted); }
details > summary.head { cursor:pointer; user-select:none; list-style:none; border-bottom:0; }
details > summary.head::-webkit-details-marker { display:none; }
details[open] > summary.head { border-bottom:1px solid var(--border); }
.xicon { font-size:8px; color:var(--icon-dim); transition:transform .14s; flex-shrink:0; }
details[open] .xicon { transform:rotate(90deg); }
.avatar { width:17px; height:17px; border-radius:50%; display:flex; align-items:center; justify-content:center; font-size:8px; flex-shrink:0; font-weight:800; }
.kind-label { font-weight:700; font-size:10px; letter-spacing:.04em; text-transform:uppercase; flex-shrink:0; }
.preview { font-size:10px; opacity:.7; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; flex:1; min-width:0; }
details[open] .preview { display:none; }
.ts { margin-left:auto; font-size:10px; opacity:.6; flex-shrink:0; }
.dur { font-size:10px; opacity:.55; flex-shrink:0; }
details > summary.head .ts { margin-left:0; }
details[open] > summary.head .ts { margin-left:auto; }
.open-btn { border:none; background:transparent; color:var(--icon-dim); padding:1px 3px; cursor:pointer; line-height:0; display:inline-flex; align-items:center; margin-left:auto; flex-shrink:0; }
details > summary.head .open-btn { margin-left:6px; }
.open-btn:hover { color:var(--icon-hover); }
main.streaming .open-btn { pointer-events:none; color:var(--icon-dim); opacity:.35; cursor:not-allowed; }
.content { padding:0; }
.frame-body { margin:0; padding:8px 12px; color:var(--text); background:var(--card); font:14px/1.42 -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; overflow-wrap:anywhere; }
.frame-body h1,.frame-body h2,.frame-body h3,.frame-body h4,.frame-body h5,.frame-body h6 { margin:.7em 0 .35em; line-height:1.25; font-weight:700; }
.frame-body h1 { font-size:1.45em; } .frame-body h2 { font-size:1.22em; } .frame-body h3 { font-size:1.08em; } .frame-body h4 { font-size:1em; }
.frame-body p { margin:.25em 0 .55em; }
.frame-body pre { background:var(--code-bg); border:1px solid var(--border); border-radius:8px; padding:10px 12px; overflow-x:auto; margin:.5em 0; }
.frame-body pre, .frame-body code { font-family:ui-monospace, 'Cascadia Code', Consolas, monospace; font-size:13px; color:var(--text); }
.frame-body code:not(pre code) { background:var(--code-bg); border:1px solid var(--border); border-radius:4px; padding:1px 5px; font-size:12.5px; }
.frame-body blockquote { border-left:3px solid var(--border); margin:.5em 0; padding:3px 0 3px 12px; color:var(--muted); }
.frame-body table { border-collapse:collapse; width:100%; margin:.5em 0; font-size:13px; }
.frame-body th { background:var(--th-bg); font-weight:600; text-align:left; }
.frame-body td, .frame-body th { border:1px solid var(--border); padding:6px 10px; }
.frame-body tr:nth-child(even) td { background:var(--tr-even); }
.frame-body a { color:var(--link); text-decoration:none; }
.frame-body a:hover { text-decoration:underline; }
.frame-body ul, .frame-body ol { padding-left:1.55em; margin:.25em 0 .55em; }
.frame-body li { margin:.18em 0; }
.frame-body hr { border:0; border-top:1px solid var(--border); margin:.8em 0; }
.frame-body img { max-width:100%; border-radius:6px; }
.live-frame { margin:.5em 0; border:1px solid var(--border); border-radius:6px; background:var(--card); overflow:hidden; position:relative; }
.live-frame .spinner-wrap { min-height:220px; display:flex; align-items:center; justify-content:center; }
.live-frame .spinner { width:24px; height:24px; border-radius:50%; border:3px solid rgba(127,127,127,.28); border-top-color:var(--link); animation:spin .8s linear infinite; }
.live-frame iframe.live-iframe { width:100%; border:0; min-height:220px; resize:vertical; overflow:auto; display:block; visibility:hidden; }
.live-frame.ready .spinner-wrap { display:none; }
.live-frame.ready iframe.live-iframe { visibility:visible; }
@keyframes spin { to { transform:rotate(360deg); } }
.frame-body > :first-child { margin-top:0; }
.frame-body > :last-child { margin-bottom:0; }
.user .head, .user details > summary.head { background:var(--user-head); }
.user .avatar { background:var(--user-avatar); color:#FFF; }
.user .kind-label { color:var(--user-label); }
.assistant .head, .assistant details > summary.head { background:var(--asst-head); }
.assistant .avatar { background:var(--asst-avatar); color:#FFF; }
.assistant .kind-label { color:var(--asst-label); }
.reasoning .head, .reasoning details > summary.head { background:var(--rsn-head); }
.reasoning .avatar { background:var(--rsn-avatar); color:#FFF; }
.reasoning .kind-label { color:var(--rsn-label); }
.tool .head, .tool details > summary.head, .intent .head, .intent details > summary.head { background:var(--tool-head); }
.tool .avatar, .intent .avatar { background:var(--tool-avatar); color:#FFF; }
.tool .kind-label, .intent .kind-label { color:var(--tool-label); }
.error .head, .error details > summary.head { background:var(--err-head); }
.error .avatar { background:var(--err-avatar); color:#FFF; }
.error .kind-label { color:var(--err-label); }
.system .head, .system details > summary.head { background:var(--sys-head); }
.system .avatar { background:var(--sys-avatar); color:#FFF; }
.system .kind-label { color:var(--muted); }
</style>
</head>
<body><main>{{messagesHtml}}</main>
<script>
document.addEventListener('click', e => {
  const button = e.target.closest('[data-open-id]');
  if (!button) return;
  e.preventDefault();
  e.stopPropagation();
  chrome.webview.postMessage({ type: 'open', id: button.dataset.openId });
});
</script>
</body>
</html>
""";
    }

    public string RenderBody(IEnumerable<ChatMessage> messages, bool darkTheme)
    {
        var body = new StringBuilder();
        foreach (var message in messages)
            body.Append(RenderMessage(message, darkTheme));
        return body.ToString();
    }

    public string RenderMessageFragment(ChatMessage message, bool darkTheme) => RenderMessage(message, darkTheme);

    public string RenderStandalone(ChatMessage message, bool darkTheme = false) => RenderFrameSource(message, includeDocumentShell: true, darkTheme: darkTheme);

    private string RenderMessage(ChatMessage message, bool darkTheme)
    {
        var css = message.Kind.ToString().ToLowerInvariant();
        var time = WebUtility.HtmlEncode(message.CreatedAt.ToString("g"));
        var durHtml = FormatDuration(message);
        var contentHtml = RenderInlineContent(message);
        var (avatar, kindLabel) = message.Kind switch
        {
            ChatMessageKind.User      => ("U",  "You"),
            ChatMessageKind.Assistant => ("C",  "Copilot"),
            ChatMessageKind.Reasoning => ("R",  "Reasoning"),
            ChatMessageKind.Tool      => ("⚙",  "Tool"),
            ChatMessageKind.Intent    => ("→",  "Intent"),
            ChatMessageKind.Error     => ("!",  "Error"),
            _                         => ("·",  message.Kind.ToString()),
        };
        var avatarHtml = WebUtility.HtmlEncode(avatar);
        var kindHtml   = WebUtility.HtmlEncode(kindLabel);
        var msgId      = message.Id;
        var msgIdHtml  = WebUtility.HtmlEncode(msgId);

        // All messages are collapsible.
        // User and assistant messages start open; others are collapsed by default.
        bool collapsible = true;
        bool openByDefault = message.Kind is ChatMessageKind.User or ChatMessageKind.Assistant;

        if (collapsible)
        {
            var preview = WebUtility.HtmlEncode(GetPreview(message.Content));
            var openAttr = openByDefault ? " open" : "";
            return $$"""
<article id="msg-{{msgIdHtml}}" class="msg {{css}}" data-mid="{{msgIdHtml}}">
  <details{{openAttr}}>
    <summary class="head">
      <span class="xicon">▶</span>
      <div class="avatar">{{avatarHtml}}</div>
      <span class="kind-label">{{kindHtml}}</span>
      <span class="preview">{{preview}}</span>
      <span class="ts">{{time}}</span>{{(durHtml.Length > 0 ? $"\n      <span class=\"dur\">· {durHtml}</span>" : "")}}
      <button class="open-btn" data-open-id="{{msgIdHtml}}" title="Open"><svg viewBox="0 0 16 16" width="11" height="11" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M7 3H3a1 1 0 0 0-1 1v9a1 1 0 0 0 1 1h9a1 1 0 0 0 1-1V9M10 2h4m0 0v4m0-4L7.5 8.5"/></svg></button>
    </summary>
    <div class="content"><div class="frame-body">{{contentHtml}}</div></div>
  </details>
</article>
""";
        }

        return $$"""
<article id="msg-{{msgIdHtml}}" class="msg {{css}}" data-mid="{{msgIdHtml}}">
  <div class="head">
    <div class="avatar">{{avatarHtml}}</div>
    <span class="kind-label">{{kindHtml}}</span>
    <span class="ts">{{time}}</span>{{(durHtml.Length > 0 ? $"\n    <span class=\"dur\">· {durHtml}</span>" : "")}}
    <button class="open-btn" data-open-id="{{msgIdHtml}}" title="Open"><svg viewBox="0 0 16 16" width="11" height="11" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M7 3H3a1 1 0 0 0-1 1v9a1 1 0 0 0 1 1h9a1 1 0 0 0 1-1V9M10 2h4m0 0v4m0-4L7.5 8.5"/></svg></button>
  </div>
  <div class="content"><div class="frame-body">{{contentHtml}}</div></div>
</article>
""";
    }

    private static string FormatDuration(ChatMessage message)
    {
        if (message.CompletedAt is not { } completed) return "";
        var span = completed - message.CreatedAt;
        if (span.TotalSeconds < 0) return "";
        if (span.TotalSeconds < 60)
            return $"{span.TotalSeconds:0.#}s";
        return $"{(int)span.TotalMinutes}m {span.Seconds}s";
    }

    private string RenderInlineContent(ChatMessage message)
    {
        if (message.Kind is ChatMessageKind.User)
            return $"<p style=\"margin:0;white-space:pre-wrap;overflow-wrap:anywhere;word-break:break-word\">{WebUtility.HtmlEncode(message.Content)}</p>";
        if (message.Kind is ChatMessageKind.Intent or ChatMessageKind.Tool or ChatMessageKind.Error)
            return $"<pre style=\"white-space:pre-wrap;overflow-wrap:anywhere;word-break:break-word\">{WebUtility.HtmlEncode(message.Content)}</pre>";
        var html = Markdown.ToHtml(message.Content, _markdown);
        return InjectLiveHtmlBlocks(html);
    }

    // Replace fenced HTML code blocks with live iframes.
    private static string InjectLiveHtmlBlocks(string markdigOutput)
    {
        // Markdig renders ```html as: <pre><code class="language-html">...escaped html...</code></pre>
        return System.Text.RegularExpressions.Regex.Replace(
            markdigOutput,
            @"<pre><code class=""language-html"">([\s\S]*?)</code></pre>",
            m =>
            {
                var decoded = WebUtility.HtmlDecode(m.Groups[1].Value);
                var srcdoc = decoded
                    .Replace("&", "&amp;")
                    .Replace("\"", "&quot;");
                return $"""
<div style="margin:.5em 0">
  <div class="live-frame">
    <div class="spinner-wrap"><div class="spinner" aria-hidden="true"></div></div>
    <iframe class="live-iframe" srcdoc="{srcdoc}" sandbox="allow-scripts allow-same-origin" onload="this.closest(&quot;.live-frame&quot;)?.classList.add(&quot;ready&quot;)"></iframe>
  </div>
</div>
""";
            });
    }

    private static string GetPreview(string content, int maxLen = 120)
    {
        var clean = content.Replace('\r', ' ').Replace('\n', ' ');
        while (clean.Contains("  ")) clean = clean.Replace("  ", " ");
        clean = clean.Trim();
        return clean.Length <= maxLen ? clean : clean[..maxLen].TrimEnd() + "…";
    }

    private string RenderFrameSource(ChatMessage message, bool includeDocumentShell, bool darkTheme)
    {
        var inner = message.Kind is ChatMessageKind.User
            ? $"<p style=\"margin:0;white-space:pre-wrap;overflow-wrap:anywhere;word-break:break-word\">{WebUtility.HtmlEncode(message.Content)}</p>"
            : message.Kind is ChatMessageKind.Intent or ChatMessageKind.Tool or ChatMessageKind.Error
                ? $"<pre style=\"white-space:pre-wrap;overflow-wrap:anywhere;word-break:break-word\">{WebUtility.HtmlEncode(message.Content)}</pre>"
                : InjectLiveHtmlBlocks(Markdown.ToHtml(message.Content, _markdown));

        var background = darkTheme ? "#161B22" : "#FFFFFF";
        var foreground = darkTheme ? "#E6EDF3" : "#1F2328";
        var border     = darkTheme ? "#30363D"  : "#D0D7DE";
        var muted      = darkTheme ? "#8B949E"  : "#57606A";
        var link       = darkTheme ? "#58A6FF"  : "#0969DA";
        var codeBg     = darkTheme ? "#0D1117"  : "#F6F8FA";
        var codeText   = darkTheme ? "#E6EDF3"  : "#24292F";
        var tableHead  = darkTheme ? "#21262D"  : "#F0F3F6";
        var tableAlt   = darkTheme ? "#0D1117"  : "#F8FAFC";

        var css = $$"""
<style>
*, *::before, *::after { box-sizing: border-box; }
html { height:100%; }
body { margin:0; padding:9px 12px; color:{{foreground}}; background:{{background}}; font:14px/1.45 -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; overflow-wrap:anywhere; min-height:100%; }
h1,h2,h3,h4,h5,h6 { margin:.8em 0 .4em; line-height:1.3; font-weight:700; }
h1 { font-size:1.5em; } h2 { font-size:1.25em; } h3 { font-size:1.1em; } h4 { font-size:1em; }
p { margin:.35em 0 .75em; }
pre { background:{{codeBg}}; border:1px solid {{border}}; border-radius:8px; padding:13px 16px; overflow-x:auto; margin:.7em 0; }
pre, code { font-family:ui-monospace, 'Cascadia Code', Consolas, monospace; font-size:13px; color:{{codeText}}; }
code:not(pre code) { background:{{codeBg}}; border:1px solid {{border}}; border-radius:4px; padding:1px 5px; font-size:12.5px; }
blockquote { border-left:3px solid {{border}}; margin:.6em 0; padding:4px 0 4px 14px; color:{{muted}}; }
table { border-collapse:collapse; width:100%; margin:.7em 0; font-size:13px; }
th { background:{{tableHead}}; font-weight:600; text-align:left; }
td, th { border:1px solid {{border}}; padding:7px 12px; }
tr:nth-child(even) td { background:{{tableAlt}}; }
a { color:{{link}}; text-decoration:none; } a:hover { text-decoration:underline; }
ul, ol { padding-left:1.7em; margin:.35em 0 .75em; }
li { margin:.25em 0; }
hr { border:0; border-top:1px solid {{border}}; margin:1em 0; }
img { max-width:100%; border-radius:6px; }
iframe { width:100% !important; min-height:calc(100vh - 3em) !important; border:1px solid {{border}}; border-radius:6px; display:block; resize:vertical; overflow:auto; }
.live-frame { margin:.5em 0; border:1px solid {{border}}; border-radius:6px; background:{{background}}; overflow:hidden; position:relative; }
.live-frame .spinner-wrap { min-height:220px; display:flex; align-items:center; justify-content:center; }
.live-frame .spinner { width:24px; height:24px; border-radius:50%; border:3px solid rgba(127,127,127,.28); border-top-color:{{link}}; animation:spin .8s linear infinite; }
.live-frame iframe.live-iframe { width:100%; border:0; min-height:220px; resize:vertical; overflow:auto; display:block; visibility:hidden; }
.live-frame.ready .spinner-wrap { display:none; }
.live-frame.ready iframe.live-iframe { visibility:visible; }
@keyframes spin { to { transform:rotate(360deg); } }
</style>
""";

        return includeDocumentShell
            ? $"<!doctype html><html><head><meta charset=\"utf-8\">{css}</head><body>{inner}</body></html>"
            : $"{css}{inner}";
    }
}
