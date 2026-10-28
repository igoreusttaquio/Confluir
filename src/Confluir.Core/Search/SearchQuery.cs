using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Confluir.Core.Search;

/// <summary>
/// Busca digitada pelo usuário. Texto solto procura em tudo (assunto, remetente, destinatários, prévia e corpo);
/// operadores restringem: <c>de:</c>, <c>para:</c>, <c>assunto:</c>, <c>tem:anexo</c>, <c>não:lida</c>
/// (também em inglês e espanhol: <c>from:</c>, <c>to:</c>, <c>subject:</c>, <c>has:attachment</c>, <c>is:unread</c>…).
/// Valores com espaço vão entre aspas: <c>de:"Ana Souza"</c>.
/// </summary>
public sealed partial record SearchQuery(
    IReadOnlyList<string> Terms,
    IReadOnlyList<string> From,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Subject,
    bool HasAttachment,
    bool Unread)
{
    public static readonly SearchQuery Empty = new([], [], [], [], false, false);

    public bool IsEmpty => Terms.Count == 0 && From.Count == 0 && To.Count == 0 && Subject.Count == 0 && !HasAttachment && !Unread;

    /// <summary>Tem algo para o índice de texto (só "tem anexo" ou "não lida" não precisam dele).</summary>
    public bool HasText => Terms.Count > 0 || From.Count > 0 || To.Count > 0 || Subject.Count > 0;

    public static SearchQuery Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Empty;
        var terms = new List<string>();
        var from = new List<string>();
        var to = new List<string>();
        var subject = new List<string>();
        var attachment = false;
        var unread = false;

        foreach (Match m in Token().Matches(text))
        {
            var key = m.Groups["key"].Success ? Normalize(m.Groups["key"].Value) : null;
            var value = m.Groups["value"].Value.Trim();
            var normalizedValue = Normalize(value);
            switch (key)
            {
                case "de" or "from" or "remetente":
                    Add(from, value);
                    break;
                case "para" or "to" or "destinatario":
                    Add(to, value);
                    break;
                case "assunto" or "subject" or "asunto":
                    Add(subject, value);
                    break;
                case "tem" or "has" or "tiene" when normalizedValue is "anexo" or "anexos" or "attachment" or "attachments" or "adjunto" or "adjuntos":
                    attachment = true;
                    break;
                case "nao" or "no" when normalizedValue is "lida" or "lidas" or "leido" or "leidos":
                case "is" when normalizedValue is "unread":
                    unread = true;
                    break;
                case null when value.EndsWith(':'):
                    // Operador ainda sem valor (ex.: digitando "de:").
                    break;
                case null:
                    Add(terms, value);
                    break;
                default:
                    // Operador desconhecido: vale como texto ("hora:10" procura "hora:10").
                    Add(terms, m.Value.Trim('"'));
                    break;
            }
        }
        return new SearchQuery(terms, from, to, subject, attachment, unread);

        static void Add(List<string> list, string value)
        {
            if (value.Any(char.IsLetterOrDigit))
                list.Add(value);
        }
    }

    /// <summary>
    /// Expressão MATCH do FTS5 (colunas subject, sender, recipients, preview, body); nulo se não há texto.
    /// Cada termo vira uma frase com prefixo (<c>"orçam"*</c> acha "orçamento"); todos precisam aparecer.
    /// </summary>
    public string? ToFtsMatch()
    {
        if (!HasText)
            return null;
        var parts = Terms.Select(Phrase)
            .Concat(From.Select(v => $"sender : {Phrase(v)}"))
            .Concat(To.Select(v => $"recipients : {Phrase(v)}"))
            .Concat(Subject.Select(v => $"subject : {Phrase(v)}"));
        return string.Join(" AND ", parts);
    }

    // Aspas internas são dobradas: o texto do usuário nunca vira sintaxe do FTS.
    private static string Phrase(string value) => $"\"{value.Replace("\"", "\"\"")}\"*";

    private static string Normalize(string s)
    {
        var decomposed = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"(?:(?<key>[\p{L}]+):)?(?:""(?<value>[^""]*)""?|(?<value>\S+))")]
    private static partial Regex Token();
}

/// <summary>Busca nas mensagens do cache.</summary>
/// <param name="AccountId">Só esta conta (nulo = todas).</param>
/// <param name="FolderId">Só esta pasta (nulo = todas).</param>
/// <param name="Since">Só a partir desta data.</param>
public sealed record MessageSearch(SearchQuery Query, Guid? AccountId = null, Guid? FolderId = null, DateTimeOffset? Since = null);
