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
        ["--scrollbar-track"] = dark ? "#0B1220" : "#F3F5F8",
        ["--scrollbar-thumb"] = dark ? "#344054" : "#C5CED8",
        ["--scrollbar-thumb-hover"] = dark ? "#46566E" : "#9BA7B5",
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
        ["--prompt-head"]  = dark ? "#2A1A04" : "#FFF7ED",
        ["--prompt-avatar"] = dark ? "#B45309" : "#F97316",
        ["--prompt-label"] = dark ? "#FDBA74" : "#C2410C",
        ["--prompt-answered-head"]  = dark ? "#0F2A1D" : "#ECFDF3",
        ["--prompt-answered-avatar"] = dark ? "#15803D" : "#16A34A",
        ["--prompt-answered-label"] = dark ? "#86EFAC" : "#166534",
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
html { scrollbar-color:var(--scrollbar-thumb) var(--scrollbar-track); scrollbar-width:thin; }
::-webkit-scrollbar { width:12px; height:12px; }
::-webkit-scrollbar-track { background:var(--scrollbar-track); }
::-webkit-scrollbar-thumb { background:var(--scrollbar-thumb); border:3px solid var(--scrollbar-track); border-radius:999px; }
::-webkit-scrollbar-thumb:hover { background:var(--scrollbar-thumb-hover); }
main { padding:10px 14px; }
.history-save-btn { position:fixed; top:12px; right:16px; z-index:20; width:32px; height:32px; border:1px solid var(--border); border-radius:50%; background:var(--card); color:var(--icon-dim); cursor:pointer; display:flex; align-items:center; justify-content:center; box-shadow:0 2px 10px rgba(0,0,0,.18); opacity:.86; }
.history-save-btn:hover { color:var(--icon-hover); background:var(--btn-bg); opacity:1; }
.history-save-btn:active { transform:translateY(1px); }
main.streaming + .history-save-btn { display:none; }
.user-copy-btn { margin-left:6px; border:0; background:transparent; color:var(--icon-dim); padding:2px; cursor:pointer; line-height:0; display:inline-flex; vertical-align:text-bottom; border-radius:4px; }
.user-copy-btn:hover { color:var(--icon-hover); background:var(--btn-bg); }
.msg { margin:0 0 7px 0; border:1px solid var(--border); border-radius:8px; background:var(--card); overflow:hidden; box-shadow:0 1px 2px rgba(0,0,0,.07); }
.turn-responses { padding:0 8px 8px 8px; }
.turn-responses .msg { margin:7px 0 0 0; box-shadow:none; }
.head, details > summary.head { display:flex; align-items:center; gap:5px; padding:3px 8px; border-bottom:1px solid var(--border); font-size:11px; color:var(--muted); }
details > summary.head { cursor:pointer; user-select:none; list-style:none; border-bottom:0; }
details > summary.head::-webkit-details-marker { display:none; }
details[open] > summary.head { border-bottom:1px solid var(--border); }
.xicon { font-size:8px; color:var(--icon-dim); transition:transform .14s; flex-shrink:0; }
details[open] > summary.head .xicon { transform:rotate(90deg); }
.avatar { width:17px; height:17px; border-radius:50%; display:flex; align-items:center; justify-content:center; font-size:8px; flex-shrink:0; font-weight:800; }
.kind-label { font-weight:700; font-size:10px; letter-spacing:.04em; text-transform:uppercase; flex-shrink:0; }
.preview { font-size:10px; opacity:.7; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; flex:1; min-width:0; }
details[open] > summary.head .preview { display:none; }
.ts { margin-left:auto; font-size:10px; opacity:.6; flex-shrink:0; }
.dur { font-size:10px; opacity:.55; flex-shrink:0; }
details > summary.head .ts { margin-left:0; }
details[open] > summary.head .ts { margin-left:auto; }
.article-btn { border:none; background:transparent; color:var(--icon-dim); padding:1px 3px; cursor:pointer; line-height:0; display:inline-flex; align-items:center; flex-shrink:0; border-radius:4px; }
.open-btn { margin-left:auto; }
details > summary.head .open-btn { margin-left:6px; }
.article-btn:hover { color:var(--icon-hover); background:var(--btn-bg); }
main.streaming .article-btn { pointer-events:none; color:var(--icon-dim); opacity:.35; cursor:not-allowed; }
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
.frame-popout-btn { position:absolute; top:6px; right:6px; z-index:2; border:1px solid var(--border); background:var(--card); color:var(--icon-dim); padding:5px; cursor:pointer; line-height:0; display:inline-flex; align-items:center; border-radius:5px; opacity:.78; box-shadow:0 1px 3px rgba(0,0,0,.12); }
.frame-popout-btn:hover { color:var(--icon-hover); opacity:1; background:var(--code-bg); }
.live-frame .spinner-wrap { min-height:220px; display:flex; align-items:center; justify-content:center; }
.live-frame .spinner { width:24px; height:24px; border-radius:50%; border:3px solid rgba(127,127,127,.28); border-top-color:var(--link); animation:spin .8s linear infinite; }
.live-frame iframe.live-iframe { width:100%; border:0; min-height:220px; resize:vertical; overflow:auto; display:block; visibility:hidden; }
.live-frame.ready .spinner-wrap { display:none; }
.live-frame.ready iframe.live-iframe { visibility:visible; }
@keyframes spin { to { transform:rotate(360deg); } }
.prompt-card { display:flex; flex-direction:column; gap:8px; }
.prompt-question { white-space:pre-wrap; overflow-wrap:anywhere; }
.prompt-details { color:var(--muted); font-size:12px; white-space:pre-wrap; overflow-wrap:anywhere; }
.prompt-actions { display:flex; flex-wrap:wrap; gap:6px; align-items:center; }
.prompt-actions.choice-list { flex-direction:column; align-items:stretch; }
.prompt-actions.choice-list .prompt-btn { width:100%; text-align:left; line-height:1.25; }
.prompt-btn { border:1px solid var(--border); border-radius:6px; background:var(--btn-bg); color:var(--text); padding:6px 10px; font:600 12px/1 -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; cursor:pointer; }
.prompt-btn.primary { background:var(--link); border-color:var(--link); color:#fff; }
.prompt-btn.danger { color:var(--err-label); }
.prompt-btn:disabled, .prompt-input:disabled { opacity:.55; cursor:not-allowed; }
.prompt-input-row { display:flex; gap:6px; align-items:flex-end; }
.prompt-input { min-width:220px; min-height:68px; max-height:220px; resize:vertical; flex:1; border:1px solid var(--border); border-radius:6px; background:var(--card); color:var(--text); padding:7px 9px; font:13px/1.35 -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; }
.prompt-answer { color:var(--muted); font-size:12px; }
.agent-card { display:flex; flex-direction:column; gap:10px; }
.agent-bulk-row { display:flex; flex-wrap:wrap; gap:6px; }
.agent-list { display:flex; flex-direction:column; gap:6px; }
.agent-row { display:grid; grid-template-columns:22px 1fr; gap:7px; align-items:start; padding:7px 8px; border:1px solid var(--border); border-radius:6px; background:var(--bg); }
.agent-row input { margin-top:2px; }
.agent-name { font-weight:700; font-size:13px; }
.agent-meta { color:var(--muted); font-size:11px; margin-top:1px; }
.agent-desc { color:var(--muted); font-size:12px; margin-top:3px; white-space:pre-wrap; overflow-wrap:anywhere; }
.agent-default-row { display:flex; flex-wrap:wrap; gap:7px; align-items:center; }
.agent-default-row label { font-size:12px; font-weight:700; }
.agent-select { min-width:220px; max-width:100%; border:1px solid var(--border); border-radius:6px; background:var(--card); color:var(--text); padding:6px 8px; font:13px/1.2 -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; }
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
.prompt .head, .prompt details > summary.head { background:var(--prompt-head); }
.prompt .avatar { background:var(--prompt-avatar); color:#FFF; }
.prompt .kind-label { color:var(--prompt-label); }
.prompt.answered .head, .prompt.answered details > summary.head { background:var(--prompt-answered-head); }
.prompt.answered .avatar { background:var(--prompt-answered-avatar); color:#FFF; }
.prompt.answered .kind-label { color:var(--prompt-answered-label); }
.error .head, .error details > summary.head { background:var(--err-head); }
.error .avatar { background:var(--err-avatar); color:#FFF; }
.error .kind-label { color:var(--err-label); }
.system .head, .system details > summary.head { background:var(--sys-head); }
.system .avatar { background:var(--sys-avatar); color:#FFF; }
.system .kind-label { color:var(--muted); }
</style>
</head>
<body><main>{{messagesHtml}}</main>
<button class="history-save-btn" title="Export chat history" aria-label="Export chat history" data-save-history="1"><svg viewBox="0 0 16 16" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.65" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M3 2.5h8l2 2V13a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V2.5Z"/><path d="M5 2.5V6h6V2.5"/><path d="M5 14v-4h6v4"/></svg></button>
<script>
document.addEventListener('click', e => {
  const button = e.target.closest('[data-save-history]');
  if (!button) return;
  e.preventDefault();
  e.stopPropagation();
  chrome.webview.postMessage({ type: 'saveHistory' });
});
document.addEventListener('click', e => {
  const button = e.target.closest('[data-copy-user-id]');
  if (!button) return;
  e.preventDefault();
  e.stopPropagation();
  chrome.webview.postMessage({ type: 'copyUserMessage', id: button.dataset.copyUserId });
});
document.addEventListener('click', e => {
  const button = e.target.closest('[data-delete-id]');
  if (!button) return;
  e.preventDefault();
  e.stopPropagation();
  chrome.webview.postMessage({ type: 'deleteMessage', id: button.dataset.deleteId });
});
document.addEventListener('click', e => {
  const button = e.target.closest('[data-open-id]');
  if (!button) return;
  e.preventDefault();
  e.stopPropagation();
  chrome.webview.postMessage({ type: 'open', id: button.dataset.openId });
});
document.addEventListener('click', e => {
  const button = e.target.closest('[data-popout-frame]');
  if (!button) return;
  e.preventDefault();
  e.stopPropagation();
  const frame = button.closest('.live-frame')?.querySelector('iframe.live-iframe');
  const htmlBase64 = button.getAttribute('data-popout-html-b64') || '';
  const html = frame?.getAttribute('srcdoc') || frame?.srcdoc || '';
  if (!htmlBase64 && !html) return;
  chrome.webview.postMessage({ type: 'openFrame', htmlBase64, html });
});
document.addEventListener('click', e => {
  const button = e.target.closest('[data-prompt-submit]');
  if (!button || button.disabled) return;
  e.preventDefault();
  e.stopPropagation();
  const article = button.closest('article[data-mid]');
  if (!article) return;
  const input = article.querySelector('[data-prompt-input]');
  const mode = button.dataset.promptMode || 'choice';
  const value = mode === 'freeform' ? (input?.value || '') : (button.dataset.promptValue || '');
  chrome.webview.postMessage({ type: 'promptResponse', id: article.dataset.mid, value, mode });
});
function updateAgentDefaultOptions(article) {
  const select = article?.querySelector('[data-agent-default]');
  if (!select) return;
  const enabled = new Set(Array.from(article.querySelectorAll('[data-agent-enabled]:checked')).map(input => input.value));
  Array.from(select.options).forEach(option => {
    option.disabled = option.value !== '' && !enabled.has(option.value);
  });
  if (select.value !== '' && !enabled.has(select.value)) {
    select.value = '';
  }
}
document.addEventListener('change', e => {
  const input = e.target.closest('[data-agent-enabled]');
  if (!input) return;
  updateAgentDefaultOptions(input.closest('article[data-mid]'));
});
document.addEventListener('click', e => {
  const bulkButton = e.target.closest('[data-agent-bulk]');
  if (bulkButton && !bulkButton.disabled) {
    e.preventDefault();
    e.stopPropagation();
    const article = bulkButton.closest('article[data-mid]');
    if (!article) return;
    const checked = bulkButton.dataset.agentBulk === 'all';
    article.querySelectorAll('[data-agent-enabled]').forEach(input => {
      if (!input.disabled) input.checked = checked;
    });
    updateAgentDefaultOptions(article);
    return;
  }
});
document.addEventListener('click', e => {
  const button = e.target.closest('[data-agent-submit]');
  if (!button || button.disabled) return;
  e.preventDefault();
  e.stopPropagation();
  const article = button.closest('article[data-mid]');
  if (!article) return;
  updateAgentDefaultOptions(article);
  const enabled = Array.from(article.querySelectorAll('[data-agent-enabled]:checked')).map(input => input.value);
  const defaultAgent = article.querySelector('[data-agent-default]')?.value || '';
  chrome.webview.postMessage({ type: 'promptResponse', id: article.dataset.mid, value: JSON.stringify({ enabled, defaultAgent }), mode: 'agent' });
});
document.addEventListener('keydown', e => {
  const input = e.target.closest('[data-prompt-input]');
  if (!input || input.disabled) return;
  if (e.key !== 'Enter' || !e.ctrlKey) return;
  e.preventDefault();
  e.stopPropagation();
  const article = input.closest('article[data-mid]');
  if (!article) return;
  chrome.webview.postMessage({ type: 'promptResponse', id: article.dataset.mid, value: input.value || '', mode: 'freeform' });
});
function autoSizeLiveIframe(frame, force) {
  if (!frame) return;
  if (!force && frame.dataset.savedHeight === '1') return;
  try {
    const height = Math.max(220, frame.contentWindow.document.documentElement.scrollHeight + 10);
    frame.style.height = height + 'px';
  } catch {}
}
function reportLiveIframeHeight(frame) {
  const article = frame.closest('article[data-mid]');
  const frameKey = frame.dataset.frameKey || '';
  const height = Math.round(frame.getBoundingClientRect().height);
  if (!article || !frameKey || height < 40) return;
  const last = Number(frame.dataset.lastReportedHeight || '0');
  if (Math.abs(height - last) < 2) return;
  frame.dataset.lastReportedHeight = String(height);
  chrome.webview.postMessage({ type: 'iframeHeightChanged', id: article.dataset.mid, frameKey, height: String(height) });
}
function initLiveFrames(root) {
  (root || document).querySelectorAll('iframe.live-iframe').forEach(frame => {
    if (frame.dataset.resizeWatcher === '1') return;
    frame.dataset.resizeWatcher = '1';
    const ready = () => {
      frame.closest('.live-frame')?.classList.add('ready');
      autoSizeLiveIframe(frame, false);
      reportLiveIframeHeight(frame);
    };
    frame.addEventListener('load', ready);
    if (frame.contentDocument?.readyState === 'complete') ready();
    try {
      new ResizeObserver(() => reportLiveIframeHeight(frame)).observe(frame);
    } catch {}
  });
}
requestAnimationFrame(() => initLiveFrames(document));
document.addEventListener('toggle', e => {
  const details = e.target;
  if (!(details instanceof HTMLDetailsElement) || !details.open) return;
  const article = details.closest('article.msg');
  if (!article || !article.classList.contains('user') || details.parentElement !== article) return;

  const responses = Array.from(details.children).find(child => child.classList?.contains('turn-responses'));
  if (!responses) return;

  const nestedArticles = Array.from(responses.children)
    .filter(child => child.classList?.contains('msg'));
  if (nestedArticles.length === 0) return;
  const allCollapsed = nestedArticles.every(item => {
    const nestedDetails = Array.from(item.children).find(child => child instanceof HTMLDetailsElement);
    return !nestedDetails?.open;
  });
  const lastResponse = nestedArticles.findLast(item => item.classList.contains('assistant'));
  const lastResponseDetails = lastResponse
    ? Array.from(lastResponse.children).find(child => child instanceof HTMLDetailsElement)
    : null;
  if (allCollapsed && lastResponseDetails) {
    lastResponseDetails.open = true;
  }
}, true);
</script>
</body>
</html>
""";
    }

    public string RenderBody(IEnumerable<ChatMessage> messages, bool darkTheme)
    {
        var body = new StringBuilder();
        ChatMessage? currentUser = null;
        var responses = new List<ChatMessage>();

        foreach (var message in messages)
        {
            if (message.Kind is ChatMessageKind.User)
            {
                if (currentUser is not null)
                {
                    body.Append(RenderUserTurn(currentUser, responses, darkTheme));
                    responses.Clear();
                }

                currentUser = message;
            }
            else if (currentUser is null)
            {
                body.Append(RenderMessage(message, darkTheme));
            }
            else
            {
                responses.Add(message);
            }
        }

        if (currentUser is not null)
        {
            body.Append(RenderUserTurn(currentUser, responses, darkTheme));
        }

        return body.ToString();
    }

    public string RenderMessageFragment(ChatMessage message, bool darkTheme) => RenderMessage(message, darkTheme);

    public string RenderTurnFragment(ChatMessage message, IEnumerable<ChatMessage> responses, bool darkTheme) =>
        message.Kind is ChatMessageKind.User
            ? RenderUserTurn(message, responses, darkTheme)
            : RenderMessage(message, darkTheme);

    public string RenderStandalone(ChatMessage message, bool darkTheme = false) => RenderFrameSource(message, includeDocumentShell: true, darkTheme: darkTheme);

    private string RenderMessage(ChatMessage message, bool darkTheme)
    {
        var css = message.Kind.ToString().ToLowerInvariant();
        if (message.Kind is ChatMessageKind.Prompt && message.Prompt?.IsAnswered == true)
        {
            css += " answered";
        }
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
            ChatMessageKind.Prompt    => ("?",  "Input Needed"),
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
        bool openByDefault = message.Kind is ChatMessageKind.User or ChatMessageKind.Assistant ||
                             message.Kind is ChatMessageKind.Prompt && message.Prompt?.IsAnswered != true;

        if (collapsible)
        {
            var preview = WebUtility.HtmlEncode(GetPreview(message.Content));
            var openAttr = openByDefault ? " open" : "";
            var forceClosedAttr = message.Kind is ChatMessageKind.Prompt && message.Prompt?.IsAnswered == true
                ? " data-force-closed=\"1\""
                : "";
            return $$"""
<article id="msg-{{msgIdHtml}}" class="msg {{css}}" data-mid="{{msgIdHtml}}"{{forceClosedAttr}}>
  <details{{openAttr}}>
    <summary class="head">
      <span class="xicon">▶</span>
      <div class="avatar">{{avatarHtml}}</div>
      <span class="kind-label">{{kindHtml}}</span>
      <span class="preview">{{preview}}</span>
      <span class="ts">{{time}}</span>{{(durHtml.Length > 0 ? $"\n      <span class=\"dur\">· {durHtml}</span>" : "")}}
      {{RenderDeleteButton(message.Kind, msgIdHtml)}}<button class="article-btn open-btn" data-open-id="{{msgIdHtml}}" title="Open" aria-label="Open"><svg viewBox="0 0 16 16" width="11" height="11" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M7 3H3a1 1 0 0 0-1 1v9a1 1 0 0 0 1 1h9a1 1 0 0 0 1-1V9M10 2h4m0 0v4m0-4L7.5 8.5"/></svg></button>
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
    {{RenderDeleteButton(message.Kind, msgIdHtml)}}<button class="article-btn open-btn" data-open-id="{{msgIdHtml}}" title="Open" aria-label="Open"><svg viewBox="0 0 16 16" width="11" height="11" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M7 3H3a1 1 0 0 0-1 1v9a1 1 0 0 0 1 1h9a1 1 0 0 0 1-1V9M10 2h4m0 0v4m0-4L7.5 8.5"/></svg></button>
  </div>
  <div class="content"><div class="frame-body">{{contentHtml}}</div></div>
</article>
""";
    }

    private string RenderUserTurn(ChatMessage userMessage, IEnumerable<ChatMessage> responses, bool darkTheme)
    {
        var css = userMessage.Kind.ToString().ToLowerInvariant();
        var time = WebUtility.HtmlEncode(userMessage.CreatedAt.ToString("g"));
        var durHtml = FormatDuration(userMessage);
        var contentHtml = RenderInlineContent(userMessage);
        var avatarHtml = WebUtility.HtmlEncode("U");
        var kindHtml = WebUtility.HtmlEncode("You");
        var msgId = userMessage.Id;
        var msgIdHtml = WebUtility.HtmlEncode(msgId);
        var preview = WebUtility.HtmlEncode(GetPreview(userMessage.Content));
        var responseHtml = new StringBuilder();

        foreach (var response in responses)
        {
            responseHtml.Append(RenderMessage(response, darkTheme));
        }

        var responsesBlock = responseHtml.Length == 0
            ? ""
            : $"""
    <div class="turn-responses">
{responseHtml}
    </div>
""";

        return $$"""
<article id="msg-{{msgIdHtml}}" class="msg {{css}}" data-mid="{{msgIdHtml}}">
  <details open>
    <summary class="head">
      <span class="xicon">▶</span>
      <div class="avatar">{{avatarHtml}}</div>
      <span class="kind-label">{{kindHtml}}</span>
      <span class="preview">{{preview}}</span>
      <span class="ts">{{time}}</span>{{(durHtml.Length > 0 ? $"\n      <span class=\"dur\">· {durHtml}</span>" : "")}}
      {{RenderDeleteButton(userMessage.Kind, msgIdHtml)}}<button class="article-btn open-btn" data-open-id="{{msgIdHtml}}" title="Open" aria-label="Open"><svg viewBox="0 0 16 16" width="11" height="11" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M7 3H3a1 1 0 0 0-1 1v9a1 1 0 0 0 1 1h9a1 1 0 0 0 1-1V9M10 2h4m0 0v4m0-4L7.5 8.5"/></svg></button>
    </summary>
    <div class="content"><div class="frame-body">{{contentHtml}}</div></div>
{{responsesBlock}}
  </details>
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
            return RenderUserContent(message);
        if (message.Kind is ChatMessageKind.Prompt && message.Prompt is not null)
            return RenderPromptContent(message);
        if (message.Kind is ChatMessageKind.Intent or ChatMessageKind.Tool or ChatMessageKind.Error)
            return $"<pre style=\"white-space:pre-wrap;overflow-wrap:anywhere;word-break:break-word\">{WebUtility.HtmlEncode(message.Content)}</pre>";
        var html = Markdown.ToHtml(message.Content, _markdown);
        return InjectLiveHtmlBlocks(html, message.IframeHeights ?? []);
    }

    private static string RenderUserContent(ChatMessage message)
    {
        var content = WebUtility.HtmlEncode(message.Content);
        var id = WebUtility.HtmlEncode(message.Id);
        return $$"""
<p style="margin:0;white-space:pre-wrap;overflow-wrap:anywhere;word-break:break-word">{{content}}<button class="user-copy-btn" data-copy-user-id="{{id}}" title="Copy message" aria-label="Copy message"><svg viewBox="0 0 16 16" width="13" height="13" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="5" y="5" width="8" height="8" rx="1.4"/><path d="M3 11V3.8A.8.8 0 0 1 3.8 3H11"/></svg></button></p>
""";
    }

    private static string RenderPromptContent(ChatMessage message)
    {
        var prompt = message.Prompt!;
        if (prompt.Type.Equals("agent", StringComparison.OrdinalIgnoreCase))
        {
            return RenderAgentPromptContent(message, prompt);
        }

        var disabled = prompt.IsAnswered ? " disabled" : "";
        var question = WebUtility.HtmlEncode(message.Content);
        var answer = prompt.IsAnswered
            ? $"<div class=\"prompt-answer\">Submitted: {WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(prompt.Answer) ? "(empty)" : prompt.Answer)}</div>"
            : "";
        var buttons = new StringBuilder();
        var actionClass = "prompt-actions";

        if (prompt.Type.Equals("permission", StringComparison.OrdinalIgnoreCase))
        {
            buttons.Append(RenderPromptButton("Deny", "Deny", "choice", "danger", disabled));
            buttons.Append(RenderPromptButton("Allow once", "AllowOnce", "choice", "primary", disabled));
            buttons.Append(RenderPromptButton("Allow for session", "AllowForSession", "choice", "", disabled));
            buttons.Append(RenderPromptButton("Save setting", "SaveToSettings", "choice", "", disabled));
        }
        else
        {
            actionClass = "prompt-actions choice-list";
            for (var i = 0; i < prompt.Choices.Count; i++)
            {
                var choice = prompt.Choices[i];
                buttons.Append(RenderPromptButton(choice, choice, "choice", i == 0 && !prompt.AllowFreeform ? "primary" : "", disabled));
            }
        }

        var input = prompt.AllowFreeform
            ? $$"""
<div class="prompt-input-row">
  <textarea class="prompt-input" data-prompt-input="1"{{disabled}} placeholder="Optional answer"></textarea>
  {{RenderPromptButton("Submit", "", "freeform", prompt.Choices.Count == 0 ? "primary" : "", disabled)}}
</div>
"""
            : "";

        return $$"""
<div class="prompt-card">
  <div class="prompt-question">{{question}}</div>
  <div class="{{actionClass}}">{{buttons}}</div>
  {{input}}
  {{answer}}
</div>
""";
    }

    private static string RenderAgentPromptContent(ChatMessage message, ChatPromptState prompt)
    {
        var disabled = prompt.IsAnswered ? " disabled" : "";
        var question = WebUtility.HtmlEncode(message.Content);
        var enabledNames = prompt.AgentOptions
            .Where(agent => agent.IsEnabled)
            .Select(agent => agent.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var defaultAgent = prompt.DefaultAgentName ?? "";
        if (!string.IsNullOrWhiteSpace(defaultAgent) && !enabledNames.Contains(defaultAgent))
        {
            defaultAgent = "";
        }

        var rows = new StringBuilder();
        foreach (var agent in prompt.AgentOptions.OrderBy(agent => agent.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var checkedAttr = agent.IsEnabled ? " checked" : "";
            var name = WebUtility.HtmlEncode(agent.Name);
            var display = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(agent.DisplayName) ? agent.Name : agent.DisplayName);
            var source = WebUtility.HtmlEncode(agent.Source);
            var description = string.IsNullOrWhiteSpace(agent.Description)
                ? ""
                : $"<div class=\"agent-desc\">{WebUtility.HtmlEncode(agent.Description)}</div>";
            rows.Append($$"""
<label class="agent-row">
  <input type="checkbox" data-agent-enabled="1" value="{{name}}"{{checkedAttr}}{{disabled}}>
  <span>
    <span class="agent-name">{{display}}</span>
    <span class="agent-meta">{{name}}{{(string.IsNullOrWhiteSpace(source) ? "" : $" · {source}")}}</span>
    {{description}}
  </span>
</label>
""");
        }

        var options = new StringBuilder();
        options.Append($"<option value=\"\"{(string.IsNullOrWhiteSpace(defaultAgent) ? " selected" : "")}>(default agent)</option>");
        foreach (var agent in prompt.AgentOptions.OrderBy(agent => agent.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var value = WebUtility.HtmlEncode(agent.Name);
            var label = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(agent.DisplayName) ? agent.Name : agent.DisplayName);
            var selected = agent.Name.Equals(defaultAgent, StringComparison.OrdinalIgnoreCase) ? " selected" : "";
            var optionDisabled = agent.IsEnabled ? "" : " disabled";
            options.Append($"<option value=\"{value}\"{selected}{optionDisabled}>{label}</option>");
        }

        var answer = prompt.IsAnswered
            ? $"<div class=\"prompt-answer\">Submitted: {WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(prompt.Answer) ? "(empty)" : prompt.Answer)}</div>"
            : "";
        var bulkButtons = $$"""
<div class="agent-bulk-row">
  <button class="prompt-btn" data-agent-bulk="all"{{disabled}}>Select all</button>
  <button class="prompt-btn" data-agent-bulk="none"{{disabled}}>Select none</button>
</div>
""";

        return $$"""
<div class="prompt-card agent-card">
  <div class="prompt-question">{{question}}</div>
  {{bulkButtons}}
  <div class="agent-list">{{rows}}</div>
  {{bulkButtons}}
  <div class="agent-default-row">
    <label for="agent-default-{{WebUtility.HtmlEncode(message.Id)}}">Default agent</label>
    <select id="agent-default-{{WebUtility.HtmlEncode(message.Id)}}" class="agent-select" data-agent-default="1"{{disabled}}>{{options}}</select>
    <button class="prompt-btn primary" data-agent-submit="1"{{disabled}}>Apply</button>
  </div>
  {{answer}}
</div>
""";
    }

    private static string RenderPromptButton(string label, string value, string mode, string cssClass, string disabled)
    {
        var classAttr = string.IsNullOrWhiteSpace(cssClass) ? "prompt-btn" : $"prompt-btn {cssClass}";
        return $"<button class=\"{classAttr}\" data-prompt-submit=\"1\" data-prompt-mode=\"{WebUtility.HtmlEncode(mode)}\" data-prompt-value=\"{WebUtility.HtmlEncode(value)}\"{disabled}>{WebUtility.HtmlEncode(label)}</button>";
    }

    private static string RenderDeleteButton(ChatMessageKind kind, string msgIdHtml)
    {
        if (kind is not (ChatMessageKind.User or ChatMessageKind.Assistant))
        {
            return "";
        }

        return $"""<button class="article-btn delete-btn" data-delete-id="{msgIdHtml}" title="Delete" aria-label="Delete"><svg viewBox="0 0 16 16" width="11" height="11" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M2.5 4h11"/><path d="M6 2.5h4"/><path d="M5 4l.5 9h5L11 4"/><path d="M7 6.5v4M9 6.5v4"/></svg></button>""";
    }

    // Replace fenced HTML code blocks with live iframes.
    private static string InjectLiveHtmlBlocks(string markdigOutput, IReadOnlyDictionary<string, double>? iframeHeights = null)
    {
        // Markdig renders ```html as: <pre><code class="language-html">...escaped html...</code></pre>
        var frameIndex = 0;
        return System.Text.RegularExpressions.Regex.Replace(
            markdigOutput,
            @"<pre><code class=""language-html"">([\s\S]*?)</code></pre>",
            m =>
            {
                var frameKey = $"html-{frameIndex++}";
                var decoded = WebUtility.HtmlDecode(m.Groups[1].Value);
                var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(decoded));
                var srcdoc = decoded
                    .Replace("&", "&amp;")
                    .Replace("\"", "&quot;");
                var height = iframeHeights is not null && iframeHeights.TryGetValue(frameKey, out var savedHeight)
                    ? Math.Clamp(savedHeight, 80, 5000)
                    : 0;
                var heightAttr = height > 0
                    ? $" style=\"height:{height:0}px\" data-saved-height=\"1\""
                    : "";
                return $"""
<div style="margin:.5em 0">
  <div class="live-frame">
    <button class="frame-popout-btn" data-popout-frame="1" data-popout-html-b64="{payload}" title="Pop out preview"><svg viewBox="0 0 16 16" width="12" height="12" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M7 3H3a1 1 0 0 0-1 1v9a1 1 0 0 0 1 1h9a1 1 0 0 0 1-1V9M10 2h4m0 0v4m0-4L7.5 8.5"/></svg></button>
    <div class="spinner-wrap"><div class="spinner" aria-hidden="true"></div></div>
    <iframe class="live-iframe" data-frame-key="{frameKey}" srcdoc="{srcdoc}" sandbox="allow-scripts allow-same-origin"{heightAttr}></iframe>
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
            : message.Kind is ChatMessageKind.Prompt && message.Prompt is not null
                ? RenderPromptContent(message)
            : message.Kind is ChatMessageKind.Intent or ChatMessageKind.Tool or ChatMessageKind.Error
                ? $"<pre style=\"white-space:pre-wrap;overflow-wrap:anywhere;word-break:break-word\">{WebUtility.HtmlEncode(message.Content)}</pre>"
                : InjectLiveHtmlBlocks(Markdown.ToHtml(message.Content, _markdown), message.IframeHeights ?? []);

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
