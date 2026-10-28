using System.Text.RegularExpressions;
using Confluir.Core.Models;
using Confluir.Mail;

namespace Confluir.App.Services;

/// <summary>Conversões de HTML usadas na composição (citação, texto simples ↔ formatado, documento enviado).</summary>
public static partial class ComposeHtml
{
    /// <summary>Fonte do email enviado: genérica, existe em qualquer cliente.</summary>
    public const string MailFont = "-apple-system, \"Segoe UI\", Roboto, Helvetica, Arial, sans-serif";

    private const string QuoteStyle = "margin:0 0 0 .8ex;border-left:2px solid #cfd4e4;padding-left:1ex;color:#4b5470";

    /// <summary>Texto simples em HTML editável: uma linha por bloco, links clicáveis.</summary>
    public static string FromText(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Concat(lines.Select(line => line.Length == 0
            ? "<div><br></div>"
            : $"<div>{EmailDocumentBuilder.Linkify(Encode(line))}</div>"));
    }

    /// <summary>Texto simples a partir do HTML do editor (versão alternativa do email e modo texto).</summary>
    public static string ToText(string html) => MessageConverter.HtmlToText(html);

    /// <summary>
    /// Conteúdo da mensagem original para citar/encaminhar: HTML sanitizado (sem scripts) e sem folhas de estilo,
    /// que vazariam para o editor e para a resposta inteira; ou o texto, se ela não tem HTML.
    /// </summary>
    public static string Original(MessageBody body)
    {
        if (string.IsNullOrWhiteSpace(body.HtmlBody))
            return FromText(body.TextBody);
        var html = EmailHtmlSanitizer.Sanitize(body.HtmlBody, allowRemoteImages: true).Html;
        return StyleBlocks().Replace(html, "");
    }

    /// <summary>Resposta: linhas em branco para escrever, cabeçalho "Em …, … escreveu:" e a citação.</summary>
    public static string Reply(string header, MessageBody body) =>
        $"<div><br></div><div><br></div><div>{Encode(header)}</div>" +
        $"<blockquote type=\"cite\" style=\"{QuoteStyle}\">{Original(body)}</blockquote>";

    /// <summary>Encaminhamento: linhas em branco, cabeçalhos da mensagem original e o conteúdo.</summary>
    public static string Forward(string title, IEnumerable<(string Label, string Value)> headers, MessageBody body) =>
        $"<div><br></div><div><br></div><div>{Encode(title)}</div>" +
        string.Concat(headers.Select(h => $"<div>{Encode(h.Label)}: {Encode(h.Value)}</div>")) +
        $"<div><br></div>{Original(body)}";

    /// <summary>Documento HTML completo enviado ao destinatário.</summary>
    public static string Document(string editorHtml) =>
        $"""
        <!doctype html>
        <html>
        <head><meta charset="utf-8"></head>
        <body><div style="font-family:{Encode(MailFont)};font-size:14px;line-height:1.5">{editorHtml}</div></body>
        </html>
        """;

    /// <summary>Escapa só o necessário (acentos ficam legíveis, sem virar entidades numéricas).</summary>
    private static string Encode(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    [GeneratedRegex(@"<style\b[^>]*>[\s\S]*?</style\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleBlocks();
}
