using System.Text.RegularExpressions;
using Confluir.Core.Models;

namespace Confluir.Core.Compose;

public enum ComposeMode
{
    New,
    Reply,
    ReplyAll,
    Forward,
}

/// <summary>Regras de resposta/encaminhamento: assunto, destinatários, citação e cabeçalhos da conversa.</summary>
public static partial class ReplyBuilder
{
    /// <summary>
    /// "Re: assunto" / "Fwd: assunto", sem acumular prefixos ("Re: Re: RES:"). Prefixos comuns de outros
    /// clientes e idiomas (RES, ENC, AW, WG, RV, Fw) são reconhecidos e substituídos.
    /// </summary>
    public static string Subject(string original, ComposeMode mode)
    {
        var bare = Prefixes().Replace(original.Trim(), "").Trim();
        return mode switch
        {
            ComposeMode.Reply or ComposeMode.ReplyAll => $"Re: {bare}",
            ComposeMode.Forward => $"Fwd: {bare}",
            _ => original,
        };
    }

    /// <summary>
    /// Responder: para o Reply-To (ou o remetente). Responder a todos: também os demais destinatários em Cc,
    /// sem o próprio endereço e sem repetições.
    /// </summary>
    public static (IReadOnlyList<MailAddress> To, IReadOnlyList<MailAddress> Cc) Recipients(
        ComposeMode mode, MailAddress from, IReadOnlyList<MailAddress>? replyTo,
        IReadOnlyList<MailAddress> to, IReadOnlyList<MailAddress> cc, string ownAddress)
    {
        if (mode is not (ComposeMode.Reply or ComposeMode.ReplyAll))
            return ([], []);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ownAddress.Trim() };
        var primary = Distinct(replyTo is { Count: > 0 } ? replyTo : [from], seen);
        if (primary.Count == 0)
            primary = [from]; // respondendo a uma mensagem enviada por mim mesmo
        if (mode == ComposeMode.Reply)
            return (primary, []);

        var others = Distinct([.. to, .. cc], seen);
        return (primary, others);
    }

    /// <summary>Linhas do texto original precedidas de "&gt; ".</summary>
    public static string Quote(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(l => l.Length == 0 ? ">" : "> " + l));

    /// <summary>References da resposta: as da original seguidas do Message-Id dela.</summary>
    public static IReadOnlyList<string> References(IReadOnlyList<string>? originalReferences, string? originalMessageId)
    {
        var refs = (originalReferences ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (!string.IsNullOrWhiteSpace(originalMessageId) && !refs.Contains(originalMessageId))
            refs.Add(originalMessageId);
        return refs;
    }

    private static List<MailAddress> Distinct(IEnumerable<MailAddress> addresses, HashSet<string> seen) =>
        addresses.Where(a => !string.IsNullOrWhiteSpace(a.Address) && seen.Add(a.Address.Trim())).ToList();

    [GeneratedRegex(@"^((re|res|fw|fwd|enc|aw|wg|rv|tr)\s*(\[\d+\])?\s*:\s*)+", RegexOptions.IgnoreCase)]
    private static partial Regex Prefixes();
}
