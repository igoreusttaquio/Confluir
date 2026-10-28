using MimeKit;
using MailAddress = Confluir.Core.Models.MailAddress;

namespace Confluir.Mail;

/// <summary>Lê destinatários digitados ("Ana &lt;ana@x.com&gt;, bruno@y.com; …").</summary>
public static class AddressParser
{
    /// <returns>Falso se algum item não for um endereço válido; <paramref name="invalid"/> diz qual.</returns>
    public static bool TryParse(string? text, out IReadOnlyList<MailAddress> addresses, out string? invalid)
    {
        addresses = [];
        invalid = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var result = new List<MailAddress>();
        // Ponto e vírgula também separa (costume do Outlook); vírgulas dentro de aspas são preservadas pelo MimeKit.
        foreach (var piece in SplitOutsideQuotes(text))
        {
            if (string.IsNullOrWhiteSpace(piece))
                continue;
            if (!InternetAddressList.TryParse(piece, out var list) || list.Mailboxes.Count() == 0 ||
                list.Mailboxes.Any(m => !IsPlausible(m.Address)))
            {
                invalid = piece.Trim();
                return false;
            }
            result.AddRange(list.Mailboxes.Select(m => new MailAddress(m.Name ?? "", m.Address)));
        }
        addresses = result;
        return true;
    }

    /// <summary>Texto para o campo de destinatários: "Nome &lt;endereço&gt;" ou só o endereço.</summary>
    public static string Format(IEnumerable<MailAddress> addresses) =>
        string.Join(", ", addresses.Select(a => a.ToString()));

    private static bool IsPlausible(string address)
    {
        var at = address.LastIndexOf('@');
        return at > 0 && at < address.Length - 1 && address[(at + 1)..].Contains('.') && !address.Contains(' ');
    }

    private static IEnumerable<string> SplitOutsideQuotes(string text)
    {
        var start = 0;
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"')
                quoted = !quoted;
            else if (!quoted && text[i] is ',' or ';')
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        yield return text[start..];
    }
}
