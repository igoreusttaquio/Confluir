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

    /// <summary>Resposta: linhas em branco para escrever, a assinatura (se houver), "Em …, … escreveu:" e a citação.</summary>
    public static string Reply(string header, MessageBody body, string? signature = null) =>
        $"<div><br></div><div><br></div>{SignatureBlock(signature, spaced: true)}<div>{Encode(header)}</div>" +
        $"<blockquote type=\"cite\" style=\"{QuoteStyle}\">{Original(body)}</blockquote>";

    /// <summary>Encaminhamento: linhas em branco, a assinatura (se houver), cabeçalhos da original e o conteúdo.</summary>
    public static string Forward(string title, IEnumerable<(string Label, string Value)> headers, MessageBody body,
        string? signature = null) =>
        $"<div><br></div><div><br></div>{SignatureBlock(signature, spaced: true)}<div>{Encode(title)}</div>" +
        string.Concat(headers.Select(h => $"<div>{Encode(h.Label)}: {Encode(h.Value)}</div>")) +
        $"<div><br></div>{Original(body)}";

    // Assinatura ------------------------------------------------------------------------------------
    // No HTML vai entre comentários-marcadores, para trocar ao mudar a conta "De" (o editor preserva comentários);
    // no texto, depois da linha "-- " (o separador de assinatura que os clientes de email reconhecem).

    private const string SignatureStart = "<!--confluir-signature-->";
    private const string SignatureEnd = "<!--/confluir-signature-->";
    private const string TextSeparator = "-- ";

    /// <summary>Corpo de uma mensagem nova: espaço para escrever e a assinatura no fim.</summary>
    public static string NewMessage(string? signature) => $"<div><br></div><div><br></div>{SignatureBlock(signature)}";

    /// <summary>
    /// Assinatura entre os marcadores. Sem assinatura, só os marcadores: trocar para uma conta que tem assinatura
    /// ainda acha onde colocá-la. <paramref name="spaced"/>: linha em branco depois (antes da citação).
    /// </summary>
    public static string SignatureBlock(string? signature, bool spaced = false) =>
        SignatureStart +
        (HasContent(signature) ? $"<div class=\"signature\">{signature}</div>{(spaced ? "<div><br></div>" : "")}" : "") +
        SignatureEnd;

    /// <summary>A assinatura no modo texto ("-- " e o texto), ou vazio.</summary>
    public static string SignatureText(string? signature) =>
        HasContent(signature) ? $"{TextSeparator}\n{ToText(signature!).Trim()}" : "";

    /// <summary>Tem texto ou imagem (o editor deixa "&lt;div&gt;&lt;br&gt;&lt;/div&gt;" quando apagado).</summary>
    public static bool HasContent(string? signature) =>
        !string.IsNullOrWhiteSpace(signature) &&
        (signature.Contains("<img", StringComparison.OrdinalIgnoreCase) || ToText(signature).Trim().Length > 0);

    /// <summary>
    /// Troca a assinatura no HTML (outra conta "De"), mantendo o espaço antes da citação se havia. Sem marcadores
    /// (o usuário apagou a assinatura, ou o rascunho é de antes), não mexe.
    /// </summary>
    public static string ReplaceSignature(string html, string? signature)
    {
        var start = html.IndexOf(SignatureStart, StringComparison.Ordinal);
        var end = start < 0 ? -1 : html.IndexOf(SignatureEnd, start, StringComparison.Ordinal);
        if (start < 0 || end < 0)
            return html;
        var after = html[(end + SignatureEnd.Length)..];
        // Antes de uma citação (resposta/encaminhamento) a assinatura vem com uma linha em branco depois.
        var spaced = after.TrimStart().StartsWith("<div>", StringComparison.Ordinal) && after.Trim().Length > 0;
        return html[..start] + SignatureBlock(signature, spaced) + after;
    }

    /// <summary>Troca a assinatura no texto, se a anterior ainda estiver lá como foi inserida.</summary>
    public static string ReplaceSignatureText(string text, string? previous, string? signature)
    {
        var old = SignatureText(previous);
        if (old.Length == 0 || text.IndexOf(old, StringComparison.Ordinal) is var at && at < 0)
            return text;
        return text[..at] + SignatureText(signature) + text[(at + old.Length)..];
    }

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
