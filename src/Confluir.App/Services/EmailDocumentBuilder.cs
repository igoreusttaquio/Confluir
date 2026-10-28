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

    public static EmailDocument Build(MessageBody body, bool allowRemoteImages, ThemePalette palette, EmailFonts fonts)
    {
        string content;
        var blocked = 0;
        var ownBackground = false;

        if (!string.IsNullOrWhiteSpace(body.HtmlBody))
        {
            var sanitized = EmailHtmlSanitizer.Sanitize(body.HtmlBody, allowRemoteImages);
            content = sanitized.Html;
            blocked = sanitized.BlockedRemoteImages;
            ownBackground = sanitized.HasOwnBackground;
        }
        else
        {
            content = $"<div class=\"plain\">{Linkify(WebUtility.HtmlEncode(body.TextBody))}</div>";
        }

        // Sem script-src: nenhum JavaScript roda. Rede só para imagens/fontes quando o usuário permitiu.
        var remote = allowRemoteImages ? " https: http:" : "";
        var csp = $"default-src 'none'; style-src 'unsafe-inline'; img-src data: cid:{remote}; font-src data:{remote}; " +
                  "form-action 'none'; frame-src 'none'; base-uri 'none'";

        // Emails com cores próprias foram desenhados para fundo claro: ficam num "papel" branco, também no tema escuro.
        var text = ownBackground ? PaperText : palette.TextPrimary;
        var link = ownBackground ? PaperLink : palette.Accent;
        var paper = ownBackground ? PaperBackground : palette.Surface;

        var html = $$"""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="{{csp}}">
            <meta name="color-scheme" content="{{(palette.IsDark && !ownBackground ? "dark" : "light")}}">
            <style>
              {{fonts.FontFaces}}
              html, body { margin: 0; padding: 0; background: {{palette.Surface}}; }
              body { font-family: {{fonts.Family}}; font-size: {{fonts.FontSize.ToString("0.#", CultureInfo.InvariantCulture)}}px;
                     line-height: {{fonts.LineHeight.ToString("0.##", CultureInfo.InvariantCulture)}}; color: {{palette.TextPrimary}};
                     overflow-wrap: anywhere; -webkit-font-smoothing: antialiased; }
              .mail { background: {{paper}}; color: {{text}}; {{(ownBackground ? "padding: 16px; border-radius: 10px;" : "")}} }
              a { color: {{link}}; }
              img { max-width: 100%; height: auto; }
              table { max-width: 100%; }
              blockquote { margin: 8px 0; padding-left: 12px; border-left: 3px solid {{palette.Border}};
                           color: {{(ownBackground ? "#5B6385" : palette.TextSecondary)}}; }
              .plain { white-space: pre-wrap; }
              pre, code { font-family: {{fonts.MonoFamily}}; font-size: 0.87em; }
              ::selection { background: {{palette.Accent}}33; }
            </style>
            </head>
            <body><div class="mail">{{content}}</div></body>
            </html>
            """;
        return new EmailDocument(html, blocked);
    }

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
