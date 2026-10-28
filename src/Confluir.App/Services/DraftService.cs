using Confluir.Core.Compose;
using Confluir.Core.Models;
using Confluir.Core.Services;
using Confluir.Mail;
using Microsoft.Extensions.Logging;

namespace Confluir.App.Services;

/// <summary>
/// Rascunhos: guarda no aparelho a cada alteração e sincroniza com a pasta Rascunhos do servidor.
/// Sem conexão o rascunho fica marcado e é enviado na próxima vez que o app abrir.
/// </summary>
public sealed class DraftService(
    IDraftStore store,
    IMailSender sender,
    IMailboxService mailbox,
    IMailCache cache,
    ILogger<DraftService> logger)
{
    public Task<Draft?> GetAsync(Guid id, CancellationToken ct = default) => store.GetAsync(id, ct);

    public Task SaveLocalAsync(Draft draft, CancellationToken ct = default) => store.SaveAsync(draft, ct);

    /// <summary>Anexos que vêm de outra mensagem (encaminhar, rascunho do servidor).</summary>
    public Task<byte[]> LoadAttachmentAsync(OutgoingAttachment attachment, CancellationToken ct) =>
        mailbox.GetAttachmentAsync(attachment.SourceMessageId!.Value, attachment.SourceIndex, ct);

    /// <summary>Grava no servidor (substituindo a versão anterior) e retorna o rascunho atualizado.</summary>
    public async Task<Draft> UploadAsync(Draft draft, CancellationToken ct = default)
    {
        var server = await Task.Run(() => sender.SaveDraftAsync(draft.Id, ToOutgoing(draft), draft.Server, LoadAttachmentAsync, ct), ct);
        // Sem pasta Rascunhos conhecida (ainda não sincronizou): tenta de novo na próxima abertura.
        var updated = draft with { Server = server ?? draft.Server, NeedsUpload = server is null };
        await store.SaveAsync(updated, ct);
        mailbox.RequestSync(draft.AccountId);
        return updated;
    }

    /// <summary>Apaga o rascunho do aparelho e do servidor (após enviar ou descartar).</summary>
    public async Task DeleteAsync(Guid draftId, Guid accountId, ServerDraft? server)
    {
        await store.DeleteAsync(draftId);
        if (server is null)
            return;
        try
        {
            await sender.DeleteDraftAsync(accountId, server);
            mailbox.RequestSync(accountId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Não foi possível apagar o rascunho do servidor (conta {AccountId})", accountId);
        }
    }

    /// <summary>Envia ao servidor os rascunhos que ficaram só no aparelho (ex.: app fechado sem conexão).</summary>
    public async Task UploadPendingAsync(IReadOnlyCollection<Guid> accountIds)
    {
        IReadOnlyList<Draft> drafts;
        try
        {
            drafts = await store.ListAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao ler os rascunhos locais");
            return;
        }
        foreach (var draft in drafts.Where(d => d.NeedsUpload && accountIds.Contains(d.AccountId)))
        {
            try
            {
                await UploadAsync(draft);
            }
            catch (Exception ex)
            {
                logger.LogInformation(ex, "Rascunho continua só no aparelho (conta {AccountId})", draft.AccountId);
            }
        }
    }

    /// <summary>Onde está no servidor um rascunho aberto pela pasta Rascunhos.</summary>
    public async Task<ServerDraft?> LocateAsync(Guid messageId) =>
        await cache.GetLocationAsync(messageId) is { } location ? new ServerDraft(location.FolderFullName, location.Uid) : null;

    /// <summary>Mensagem a gravar: endereços incompletos ficam de fora (o texto digitado continua no rascunho local).</summary>
    public static OutgoingMessage ToOutgoing(Draft draft) =>
        new(draft.AccountId, Lenient(draft.To), Lenient(draft.Cc), Lenient(draft.Bcc), draft.Subject,
            draft.IsPlainText ? draft.Text : ComposeHtml.ToText(draft.Html),
            draft.IsPlainText ? null : ComposeHtml.Document(draft.Html),
            draft.Attachments, draft.InReplyTo, draft.References);

    private static IReadOnlyList<MailAddress> Lenient(string text) =>
        AddressParser.TryParse(text, out var all, out _)
            ? all
            : text.Split(',', ';').SelectMany(part => AddressParser.TryParse(part, out var one, out _) ? one : []).ToList();
}
