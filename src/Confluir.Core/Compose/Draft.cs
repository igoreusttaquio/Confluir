using System.Text.RegularExpressions;
using Confluir.Core.Models;

namespace Confluir.Core.Compose;

/// <summary>Cópia do rascunho na pasta Rascunhos do servidor.</summary>
public sealed record ServerDraft(string FolderFullName, uint Uid);

/// <summary>
/// Mensagem em composição, salva automaticamente no aparelho e enviada para a pasta Rascunhos do servidor.
/// Os destinatários ficam como o texto digitado (podem estar incompletos).
/// </summary>
/// <param name="NeedsUpload">Há alterações ainda não enviadas ao servidor (ex.: sem conexão).</param>
public sealed record Draft(
    Guid Id,
    Guid AccountId,
    ComposeMode Mode,
    string To,
    string Cc,
    string Bcc,
    string Subject,
    string Html,
    string Text,
    bool IsPlainText,
    IReadOnlyList<OutgoingAttachment> Attachments,
    string? InReplyTo,
    IReadOnlyList<string>? References,
    DateTimeOffset UpdatedAt,
    ServerDraft? Server = null,
    bool NeedsUpload = true)
{
    /// <summary>Sem destinatários, assunto, texto nem anexos: não vale a pena guardar.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(To) && string.IsNullOrWhiteSpace(Cc) && string.IsNullOrWhiteSpace(Bcc) &&
        string.IsNullOrWhiteSpace(Subject) && string.IsNullOrWhiteSpace(Text) && Attachments.Count == 0;
}

/// <summary>Message-Id dos rascunhos gravados pelo Confluir: permite reabrir a versão local ao abrir pela pasta Rascunhos.</summary>
public static partial class DraftMessageId
{
    public static string For(Guid draftId) => $"{draftId:N}.draft@confluir";

    /// <summary>Id do rascunho local a partir do Message-Id (com ou sem &lt;&gt;), ou nulo se não é um rascunho do Confluir.</summary>
    public static Guid? Parse(string? messageId)
    {
        var m = Pattern().Match(messageId ?? "");
        return m.Success ? Guid.ParseExact(m.Groups[1].Value, "N") : null;
    }

    [GeneratedRegex(@"^<?([0-9a-f]{32})\.draft@confluir>?$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();
}
