using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace Confluir.Mail;

/// <param name="Html">Fragmento seguro para exibir (sem scripts, formulários, iframes nem handlers).</param>
/// <param name="BlockedRemoteImages">Imagens remotas removidas (protege contra rastreamento).</param>
/// <param name="HasOwnBackground">O email define cores de fundo próprias (não deve receber as cores do tema escuro).</param>
/// <param name="HasDarkText">O email fixa cores de texto escuras (ex.: Outlook com "windowtext"): ilegível sobre fundo escuro.</param>
public sealed record SanitizedHtml(string Html, int BlockedRemoteImages, bool HasOwnBackground, bool HasDarkText = false);

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
        return new SanitizedHtml(content, blocked, OwnBackground().IsMatch(content), HasDarkTextColor(content));
    }

    /// <summary>
    /// Alguma cor de texto declarada (CSS <c>color:</c> ou <c>&lt;font color&gt;</c>) é escura? Cores que não dá para
    /// interpretar contam como escuras: na dúvida, o email vai para o "papel" claro e continua legível.
    /// </summary>
    public static bool HasDarkTextColor(string html)
    {
        foreach (Match m in TextColor().Matches(html))
        {
            var value = (m.Groups["css"].Success ? m.Groups["css"].Value : m.Groups["attr"].Value).Trim().Trim('"', '\'').ToLowerInvariant();
            if (value is "inherit" or "initial" or "unset" or "currentcolor" or "transparent" or "")
                continue;
            if (Luminance(value) is not { } luminance || luminance < 0.5)
                return true;
        }
        return false;
    }

    /// <summary>Luminância relativa (0 = preto, 1 = branco) de uma cor CSS simples; nulo se não reconhecida.</summary>
    public static double? Luminance(string color)
    {
        static double Channel(int c)
        {
            var v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        static double Of(int r, int g, int b) => 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);

        if (NamedColors.TryGetValue(color, out var hex))
            color = hex;
        if (color.StartsWith('#'))
        {
            var h = color[1..];
            if (h.Length is 3 or 4)
                h = string.Concat(h.Take(3).Select(c => $"{c}{c}"));
            if (h.Length >= 6 && int.TryParse(h[..6], System.Globalization.NumberStyles.HexNumber, null, out var rgb))
                return Of((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
            return null;
        }
        var fn = RgbFunction().Match(color);
        if (fn.Success)
        {
            static int Part(string p) => p.EndsWith('%')
                ? (int)Math.Round(double.Parse(p.TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture) * 2.55)
                : (int)Math.Round(double.Parse(p, System.Globalization.CultureInfo.InvariantCulture));
            try
            {
                return Of(Math.Clamp(Part(fn.Groups[1].Value), 0, 255), Math.Clamp(Part(fn.Groups[2].Value), 0, 255),
                    Math.Clamp(Part(fn.Groups[3].Value), 0, 255));
            }
            catch (FormatException)
            {
                return null;
            }
        }
        return null;
    }

    // Nomes comuns em emails (o resto cai em "não reconhecida" e conta como escura).
    private static readonly Dictionary<string, string> NamedColors = new()
    {
        ["black"] = "#000000", ["windowtext"] = "#000000", ["navy"] = "#000080", ["darkblue"] = "#00008b",
        ["maroon"] = "#800000", ["darkred"] = "#8b0000", ["green"] = "#008000", ["darkgreen"] = "#006400",
        ["purple"] = "#800080", ["gray"] = "#808080", ["grey"] = "#808080", ["dimgray"] = "#696969",
        ["darkgray"] = "#a9a9a9", ["silver"] = "#c0c0c0", ["white"] = "#ffffff", ["red"] = "#ff0000",
        ["blue"] = "#0000ff", ["teal"] = "#008080", ["olive"] = "#808000", ["yellow"] = "#ffff00",
        ["orange"] = "#ffa500", ["lightgray"] = "#d3d3d3", ["lightgrey"] = "#d3d3d3", ["whitesmoke"] = "#f5f5f5",
    };

    [GeneratedRegex(@"(?<![\w-])color\s*:\s*(?<css>[^;""'}]+)|<font\b[^>]*\bcolor\s*=\s*(?<attr>""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex TextColor();

    [GeneratedRegex(@"^rgba?\(\s*([\d.]+%?)\s*[, ]\s*([\d.]+%?)\s*[, ]\s*([\d.]+%?)", RegexOptions.IgnoreCase)]
    private static partial Regex RgbFunction();

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
