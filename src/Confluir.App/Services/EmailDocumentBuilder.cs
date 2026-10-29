using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Confluir.Core.Models;
using Confluir.Mail;

namespace Confluir.App.Services;

public sealed record EmailDocument(string Html, int BlockedRemoteImages);

/// <summary>Fontes do leitor: famílias CSS, regras @font-face (embutidas) e tamanho/altura de linha.</summary>
public sealed record EmailFonts(string Family, string MonoFamily, string FontFaces, double FontSize, double LineHeight)
{
    public static readonly EmailFonts Default = new(FontService.SystemSansCss, "Consolas, monospace", "", 15, 1.6);
}

/// <summary>
/// Monta o documento HTML exibido no painel de leitura: conteúdo sanitizado, política de segurança (CSP)
/// que impede scripts e acesso à rede, e estilos que seguem o tema do app.
/// </summary>
public static partial class EmailDocumentBuilder
{
    // Cores de "papel" para emails com fundo próprio: são feitos para fundo claro.
    private const string PaperBackground = "#FFFFFF";
    private const string PaperText = "#141A33";
    private const string PaperLink = "#2F5BEA";
    private const string PaperTextOnDark = "#EDEFF7";
    private const string PaperLinkOnDark = "#8FA8FF";

    public static EmailDocument Build(MessageBody body, bool allowRemoteImages, ThemePalette palette, EmailFonts fonts)
    {
        string content;
        var blocked = 0;
        var ownBackground = false;
        string? emailBackground = null;

        if (!string.IsNullOrWhiteSpace(body.HtmlBody))
        {
            var sanitized = EmailHtmlSanitizer.Sanitize(body.HtmlBody, allowRemoteImages);
            content = sanitized.Html;
            blocked = sanitized.BlockedRemoteImages;
            // Fundo próprio, ou texto escuro fixado pelo email no tema escuro: vai para o "papel" claro.
            ownBackground = sanitized.HasOwnBackground || (palette.IsDark && sanitized.HasDarkText);
            emailBackground = sanitized.HasOwnBackground ? sanitized.OuterBackground : null;
        }
        else
        {
            content = $"<div class=\"plain\">{Linkify(WebUtility.HtmlEncode(body.TextBody))}</div>";
        }

        // Só roda o script do próprio app (nonce novo a cada documento); os do email já saíram no sanitizador e a
        // CSP os bloquearia de qualquer forma. Rede só para imagens/fontes quando o usuário permitiu.
        var remote = allowRemoteImages ? " https: http:" : "";
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var csp = $"default-src 'none'; style-src 'unsafe-inline'; img-src data: cid:{remote}; font-src data:{remote}; " +
                  $"script-src 'nonce-{nonce}'; form-action 'none'; frame-src 'none'; base-uri 'none'";

        // Emails com cores próprias foram desenhados para fundo claro: ficam num "papel" branco, também no tema escuro.
        // A folha continua o fundo externo do próprio email quando dá para saber qual é (sem moldura branca em volta);
        // texto e links seguem o contraste com esse fundo. Sem cor conhecida, papel claro.
        var paper = ownBackground ? emailBackground ?? PaperBackground : palette.Surface;
        var paperIsDark = ownBackground && EmailHtmlSanitizer.Luminance(paper) is < 0.35;
        var text = !ownBackground ? palette.TextPrimary : paperIsDark ? PaperTextOnDark : PaperText;
        var link = !ownBackground ? palette.Accent : paperIsDark ? PaperLinkOnDark : PaperLink;
        var sheetBorder = !ownBackground ? palette.Border : emailBackground ?? "#E3E6EF";
        var quoteText = !ownBackground ? palette.TextSecondary : paperIsDark ? "#B9C0DA" : "#5B6385";

        var html = $$"""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="{{csp}}">
            <meta name="color-scheme" content="{{((palette.IsDark && !ownBackground) || paperIsDark ? "dark" : "light")}}">
            <style>
              {{fonts.FontFaces}}
              html, body { margin: 0; padding: 0; background: {{palette.Surface}}; }
              body { font-family: {{fonts.Family}}; font-size: {{fonts.FontSize.ToString("0.#", CultureInfo.InvariantCulture)}}px;
                     line-height: {{fonts.LineHeight.ToString("0.##", CultureInfo.InvariantCulture)}}; color: {{palette.TextPrimary}};
                     overflow-wrap: anywhere; -webkit-font-smoothing: antialiased; }
              /* Todo email na mesma folha (margem, raio e borda iguais); só o fundo muda: o do tema ou o papel claro. */
              .mail { background: {{paper}}; color: {{text}}; padding: 18px 20px; border-radius: 12px;
                      border: 1px solid {{sheetBorder}}; overflow-wrap: anywhere; }
              a { color: {{link}}; }
              img { max-width: 100%; height: auto; }
              blockquote { margin: 8px 0; padding-left: 12px; border-left: 3px solid {{palette.Border}};
                           color: {{quoteText}}; }
              .plain { white-space: pre-wrap; }
              pre, code { font-family: {{fonts.MonoFamily}}; font-size: 0.87em; }
              ::selection { background: {{palette.Accent}}33; }
              /* Barra de rolagem discreta: trilho transparente, só o polegar fino na cor do tema. */
              html { scrollbar-width: thin; scrollbar-color: {{palette.TextSecondary}}66 transparent; }
              ::-webkit-scrollbar { width: 10px; height: 10px; background: transparent; }
              ::-webkit-scrollbar-track, ::-webkit-scrollbar-corner { background: transparent; }
              ::-webkit-scrollbar-thumb { background: {{palette.TextSecondary}}66; border-radius: 8px;
                                          border: 3px solid transparent; background-clip: content-box; }
              ::-webkit-scrollbar-thumb:hover { background: {{palette.TextSecondary}}AA; background-clip: content-box; }
            </style>
            </head>
            <body><div class="mail"><div class="content">{{content}}</div></div>
            <script nonce="{{nonce}}">{{FitScript}}</script>
            </body>
            </html>
            """;
        return new EmailDocument(html, blocked);
    }

    /// <summary>
    /// "Ajustar à largura": um email desenhado mais largo que o painel (newsletters de 600 px em tabelas) é montado na
    /// largura dele e reduzido em proporção, em vez de ser espremido (colunas apertadas) ou cortado à direita.
    /// A largura de projeto vem do que o próprio conteúdo ocupa e das larguras fixas em tabelas e blocos (imagens não
    /// contam: elas já encolhem). Recalcula ao redimensionar o painel.
    /// </summary>
    private const string FitScript = """
        (function () {
          var mail = document.querySelector('.mail'), content = document.querySelector('.content');
          if (!mail || !content) return;
          function px(v) { return v && /^\s*\d+(\.\d+)?(px)?\s*$/.test(v) ? parseFloat(v) : 0; }
          function designed() {
            var max = 0, all = content.querySelectorAll('table, td, th, div, center');
            for (var i = 0; i < all.length && i < 3000; i++) {
              var e = all[i];
              max = Math.max(max, px(e.getAttribute('width')), px(e.style.width), px(e.style.minWidth));
            }
            return max;
          }
          function fit() {
            content.style.zoom = ''; content.style.width = '';
            var s = getComputedStyle(mail);
            var available = mail.clientWidth - parseFloat(s.paddingLeft) - parseFloat(s.paddingRight);
            var natural = Math.max(content.scrollWidth, designed());
            if (available > 0 && natural > available + 1) {
              content.style.width = natural + 'px';
              content.style.zoom = Math.max(0.4, available / natural);
            }
          }
          fit();
          addEventListener('load', fit);
          addEventListener('resize', fit);
        })();
        """;

    /// <summary>Torna URLs e endereços clicáveis num texto já codificado em HTML.</summary>
    public static string Linkify(string encodedText) =>
        LinkPattern().Replace(encodedText, m =>
        {
            var value = m.Value;
            // Pontuação final costuma não fazer parte do link ("veja https://x.com.").
            var trailing = "";
            while (value.Length > 0 && ".,;:!?)".Contains(value[^1]))
            {
                trailing = value[^1] + trailing;
                value = value[..^1];
            }
            var href = value.Contains('@') && !value.Contains("://") ? "mailto:" + value : value;
            return $"<a href=\"{href}\">{value}</a>{trailing}";
        });

    [GeneratedRegex(@"\bhttps?://[^\s<>""']+|\b[\w.+-]+@[\w-]+(\.[\w-]+)+\b", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();
}
