using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Confluir.App.Services;

/// <summary>
/// Página do editor de mensagens (WebView): um <c>contenteditable</c> com o script do próprio app.
/// A CSP só deixa rodar esse script (nonce) e não permite acesso à rede, nem para o conteúdo colado ou citado.
/// O conteúdo entra depois de carregar (<c>setContent</c>), sem o limite de tamanho de NavigateToString.
/// </summary>
/// <remarks>
/// Mensagens para o app (JSON): <c>ready</c>, <c>dirty</c> (o texto mudou), <c>content</c> (html, quando pedido),
/// <c>state</c> (formatação no cursor), <c>image</c> (imagem clicada: posição e largura),
/// <c>file</c> (arquivo solto no editor, em base64), <c>send</c> (Ctrl+Enter) e <c>escape</c>.
/// </remarks>
public static class EditorDocument
{
    /// <summary>Arquivos soltos no editor acima disso não são lidos (ficam para o botão de anexar).</summary>
    public const long MaxDroppedFileBytes = 25L * 1024 * 1024;

    public static string Build(ThemePalette palette, string fontFamily, string placeholder)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var csp = $"default-src 'none'; style-src 'unsafe-inline'; img-src data: blob:; script-src 'nonce-{nonce}'; " +
                  "form-action 'none'; frame-src 'none'; base-uri 'none'";
        return $$"""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="{{csp}}">
            <style>
              :root { {{Variables(palette)}} }
              html, body { margin: 0; height: 100%; background: var(--surface); }
              body { font-family: {{fontFamily}}; font-size: 15px; line-height: 1.6; color: var(--text); }
              #editor { min-height: 100%; box-sizing: border-box; padding: 16px 24px 48px; outline: none;
                        overflow-wrap: anywhere; caret-color: var(--accent); }
              #editor:empty::before { content: attr(data-placeholder); color: var(--muted); pointer-events: none; }
              a { color: var(--accent); }
              img { max-width: 100%; height: auto; }
              img[src^="http:"], img[src^="https:"] { visibility: hidden; }
              blockquote { margin: 8px 0 !important; padding-left: 12px !important;
                           border-left: 3px solid var(--border) !important; color: var(--muted) !important; }
              pre, code { font-family: Consolas, "JetBrains Mono", monospace; font-size: 13px; }
              ::selection { background: color-mix(in srgb, var(--accent) 25%, transparent); }
              /* Realce: o texto fica escuro (no tema escuro o texto claro sumiria no amarelo). */
              #editor [style*="background-color"] { color: #202124; }
              img.cf-sel { outline: 2px solid var(--accent); outline-offset: 2px; }
            </style>
            </head>
            <body>
            <div id="editor" contenteditable="true" spellcheck="true" data-placeholder="{{WebUtility.HtmlEncode(placeholder)}}"></div>
            <script nonce="{{nonce}}">
            (() => {
              const ed = document.getElementById('editor');
              const send = m => {
                const s = JSON.stringify(m);
                if (window.invokeCSharpAction) window.invokeCSharpAction(s);
                else if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(s);
              };
              // Digitar só avisa que mudou (uma vez por rodada); o HTML vai para o app quando ele pede (flush),
              // para não mandar o documento inteiro, que pode ter megabytes de citação, a cada tecla.
              let saved = null, last = '', dirtySent = false;
              // Imagem clicada: o app mostra as opções (posição e tamanho). A marca de seleção não vai no HTML.
              let img = null;
              const mark = on => { if (!img) return; img.classList.toggle('cf-sel', on); if (!img.classList.length) img.removeAttribute('class'); };
              const imgState = () => send(img
                ? { type: 'image', selected: true, align: img.getAttribute('align') || '', width: parseInt(img.getAttribute('width'), 10) || 0 }
                : { type: 'image', selected: false });
              const pick = el => { if (img === el) return; mark(false); img = el; mark(true); imgState(); };
              const tidy = el => { if (!el.getAttribute('style')) el.removeAttribute('style'); };
              const flush = () => {
                dirtySent = false;
                mark(false);
                const html = ed.innerHTML;
                mark(true);
                if (html !== last) { last = html; send({ type: 'content', html }); }
                else send({ type: 'flushed' });
              };
              const changed = () => { if (!dirtySent) { dirtySent = true; send({ type: 'dirty' }); } };
              const state = () => send({ type: 'state',
                bold: document.queryCommandState('bold'), italic: document.queryCommandState('italic'),
                underline: document.queryCommandState('underline'), strike: document.queryCommandState('strikeThrough'),
                ul: document.queryCommandState('insertUnorderedList'), ol: document.queryCommandState('insertOrderedList') });
              const restore = () => {
                ed.focus();
                if (saved) { const s = getSelection(); s.removeAllRanges(); s.addRange(saved); }
              };
              const esc = t => t.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);

              document.addEventListener('selectionchange', () => {
                const s = getSelection();
                if (s.rangeCount && ed.contains(s.anchorNode)) { saved = s.getRangeAt(0).cloneRange(); state(); }
              });
              ed.addEventListener('input', () => { if (img && !ed.contains(img)) pick(null); changed(); });
              ed.addEventListener('click', e => pick(e.target.nodeName === 'IMG' ? e.target : null));
              ed.addEventListener('keydown', e => { if (img && !['Shift', 'Control', 'Meta', 'Alt'].includes(e.key)) pick(null); });

              // Cores em style (não <font>); "sem cor" tira a cor da seleção e dos elementos em volta.
              const color = (name, value) => {
                if (value) {
                  document.execCommand('styleWithCSS', false, true);
                  document.execCommand(name, false, value);
                  document.execCommand('styleWithCSS', false, false);
                  return;
                }
                const prop = name === 'foreColor' ? 'color' : 'backgroundColor';
                const s = getSelection(); if (!s.rangeCount) return;
                const r = s.getRangeAt(0);
                let root = r.commonAncestorContainer; if (root.nodeType !== 1) root = root.parentNode;
                const els = [...root.querySelectorAll('*')].filter(e => r.intersectsNode(e));
                for (let p = root; p && p !== ed; p = p.parentNode) els.push(p);
                for (const e of els) {
                  if (e.style[prop]) { e.style[prop] = ''; tidy(e); }
                  if (prop === 'color' && e.nodeName === 'FONT') e.removeAttribute('color');
                  if ((e.nodeName === 'SPAN' || e.nodeName === 'FONT') && !e.attributes.length) e.replaceWith(...e.childNodes);
                }
              };
              // toStart: na assinatura a imagem vai para o começo, para o texto (escrito antes ou depois) ficar ao lado.
              window.imageAlign = (a, toStart) => {
                if (!img) return;
                if (a === 'left' || a === 'right') {
                  const lead = img.closest('div.signature') || (toStart ? ed : null);
                  if (lead && lead.firstChild !== img) lead.prepend(img);
                  // align: o Outlook ignora float, mas respeita o atributo.
                  img.setAttribute('align', a); img.style.float = a;
                  img.style.margin = a === 'left' ? '0 12px 4px 0' : '0 0 4px 12px';
                } else {
                  img.removeAttribute('align'); img.style.float = ''; img.style.margin = ''; tidy(img);
                }
                changed(); imgState();
              };
              window.imageWidth = w => {
                if (!img) return;
                if (w > 0) { img.setAttribute('width', w); img.style.width = w + 'px'; img.style.height = 'auto'; }
                else { img.removeAttribute('width'); img.removeAttribute('height'); img.style.width = ''; img.style.height = ''; tidy(img); }
                changed(); imgState();
              };
              document.execCommand('defaultParagraphSeparator', false, 'div');

              window.cmd = (name, value) => {
                restore();
                if (name === 'blockquote') {
                  const inQuote = (() => { let n = getSelection().anchorNode; while (n && n !== ed) { if (n.nodeName === 'BLOCKQUOTE') return true; n = n.parentNode; } return false; })();
                  document.execCommand(inQuote ? 'outdent' : 'formatBlock', false, inQuote ? null : 'blockquote');
                } else if (name === 'foreColor' || name === 'hiliteColor') {
                  color(name, value);
                } else {
                  document.execCommand(name, false, value === undefined ? null : value);
                }
                changed(); state();
              };
              window.link = url => {
                restore();
                if (getSelection().isCollapsed) document.execCommand('insertHTML', false, '<a href="' + esc(url) + '">' + esc(url) + '</a>');
                else document.execCommand('createLink', false, url);
                changed();
              };
              window.setContent = html => { pick(null); ed.innerHTML = html; last = ''; flush(); };
              window.flush = flush;
              window.focusStart = () => {
                ed.focus();
                const r = document.createRange(); r.setStart(ed, 0); r.collapse(true);
                const s = getSelection(); s.removeAllRanges(); s.addRange(r);
              };
              window.focusEditor = () => restore();
              window.setColors = vars => { for (const k in vars) document.documentElement.style.setProperty(k, vars[k]); };

              // Imagens coladas entram no texto; arquivos soltos viram anexos (o app recebe o conteúdo).
              ed.addEventListener('paste', e => {
                const items = [...((e.clipboardData && e.clipboardData.items) || [])].filter(i => i.type.startsWith('image/'));
                if (!items.length) return;
                e.preventDefault();
                for (const item of items) {
                  const reader = new FileReader();
                  reader.onload = () => { document.execCommand('insertImage', false, reader.result); changed(); };
                  reader.readAsDataURL(item.getAsFile());
                }
              });
              document.addEventListener('dragover', e => { e.preventDefault(); });
              document.addEventListener('drop', e => {
                const files = e.dataTransfer && e.dataTransfer.files;
                if (!files || !files.length) return;
                e.preventDefault();
                for (const f of files) {
                  if (f.size > {{MaxDroppedFileBytes}}) { send({ type: 'tooLarge', name: f.name }); continue; }
                  const reader = new FileReader();
                  reader.onload = () => send({ type: 'file', name: f.name, data: String(reader.result).split(',')[1] || '' });
                  reader.readAsDataURL(f);
                }
              });
              document.addEventListener('keydown', e => {
                if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); flush(); send({ type: 'send' }); }
                else if (e.key === 'Escape') { flush(); send({ type: 'escape' }); }
              });
              send({ type: 'ready' });
            })();
            </script>
            </body>
            </html>
            """;
    }

    /// <summary>Script que aplica as cores do tema sem recarregar (o texto em edição é mantido).</summary>
    public static string SetColorsScript(ThemePalette palette) =>
        $"setColors({JsonSerializer.Serialize(VariableMap(palette))})";

    public static string Call(string function, string argument) => $"{function}({JsonSerializer.Serialize(argument)})";

    private static Dictionary<string, string> VariableMap(ThemePalette p) => new()
    {
        ["--surface"] = p.Surface,
        ["--text"] = p.TextPrimary,
        ["--muted"] = p.TextSecondary,
        ["--border"] = p.Border,
        ["--accent"] = p.Accent,
    };

    private static string Variables(ThemePalette p) =>
        string.Join(" ", VariableMap(p).Select(kv => $"{kv.Key}: {kv.Value};"));
}
