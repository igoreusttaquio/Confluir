using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace Confluir.Mail;

/// <param name="Html">Fragmento seguro para exibir (sem scripts, formulários, iframes nem handlers).</param>
/// <param name="BlockedRemoteImages">Imagens remotas removidas (protege contra rastreamento).</param>
/// <param name="HasOwnBackground">O email define cores de fundo próprias (não deve receber as cores do tema escuro).</param>
public sealed record SanitizedHtml(string Html, int BlockedRemoteImages, bool HasOwnBackground);

/// <summary>
/// Sanitiza o HTML de emails antes de exibir. Remove tudo que executa código ou envia dados
/// (scripts, iframes, formulários, handlers de evento, <c>javascript:</c>) e, por padrão,
/// as imagens remotas, que servem para rastrear a abertura do email.
/// </summary>
public static partial class EmailHtmlSanitizer
{
    private static readonly string[] ForbiddenTags =
    [
        "script", "iframe", "frame", "frameset", "object", "embed", "applet", "form", "input", "button",
        "select", "option", "textarea", "base", "meta", "link", "svg", "math",
    ];

    // Atributos de layout comuns em emails (tabelas antigas), além dos padrões do HtmlSanitizer.
    private static readonly string[] ExtraAttributes =
    [
        "class", "bgcolor", "background", "align", "valign", "width", "height", "border",
        "cellpadding", "cellspacing", "color", "face", "size",
    ];

    public const string TransparentGif = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";

    public static SanitizedHtml Sanitize(string html, bool allowRemoteImages)
    {
        var blocked = 0;
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Add("style");
        foreach (var tag in ForbiddenTags)
            sanitizer.AllowedTags.Remove(tag);
        foreach (var attribute in ExtraAttributes)
            sanitizer.AllowedAttributes.Add(attribute);
        sanitizer.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto", "data", "cid" })
            sanitizer.AllowedSchemes.Add(scheme);
        sanitizer.AllowDataAttributes = false;

        sanitizer.FilterUrl += (_, e) =>
        {
            if (e.OriginalUrl is null || allowRemoteImages || !IsRemote(e.OriginalUrl))
                return;
            // Links continuam funcionando (abrem no navegador); qualquer outro recurso remoto é bloqueado.
            if (string.Equals(e.Tag?.NodeName, "A", StringComparison.OrdinalIgnoreCase))
                return;
            if (string.Equals(e.Tag?.NodeName, "IMG", StringComparison.OrdinalIgnoreCase))
            {
                // GIF transparente embutido: mantém o layout sem o ícone de imagem quebrada.
                e.SanitizedUrl = TransparentGif;
                blocked++;
            }
            else
            {
                e.SanitizedUrl = null;
            }
        };

        var content = sanitizer.Sanitize(ExtractBody(html));
        return new SanitizedHtml(content, blocked, OwnBackground().IsMatch(content));
    }

    private static bool IsRemote(string url)
    {
        var u = url.TrimStart();
        return u.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
               u.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
               u.StartsWith("//", StringComparison.Ordinal);
    }

    /// <summary>
    /// Leva os <c>&lt;style&gt;</c> do <c>&lt;head&gt;</c> para o corpo (o sanitizador trabalha com o corpo)
    /// e preserva cor de fundo/estilo definidos no próprio <c>&lt;body&gt;</c>.
    /// </summary>
    private static string ExtractBody(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var styles = doc.Head?.QuerySelectorAll("style").Select(s => s.OuterHtml) ?? [];
        var body = doc.Body;
        if (body is null)
            return html;

        var inner = body.InnerHtml;
        var bodyStyle = body.GetAttribute("style");
        var bodyBg = body.GetAttribute("bgcolor");
        if (!string.IsNullOrWhiteSpace(bodyStyle) || !string.IsNullOrWhiteSpace(bodyBg))
        {
            var wrapper = doc.CreateElement("div");
            if (!string.IsNullOrWhiteSpace(bodyStyle))
                wrapper.SetAttribute("style", bodyStyle);
            if (!string.IsNullOrWhiteSpace(bodyBg))
                wrapper.SetAttribute("bgcolor", bodyBg);
            wrapper.InnerHtml = inner;
            inner = wrapper.OuterHtml;
        }
        return string.Concat(styles) + inner;
    }

    [GeneratedRegex(@"background(-color)?\s*:|\bbgcolor\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex OwnBackground();
}
