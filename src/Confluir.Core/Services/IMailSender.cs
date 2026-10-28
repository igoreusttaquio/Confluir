using Confluir.Core.Compose;
using Confluir.Core.Models;

namespace Confluir.Core.Services;

/// <summary>Envia mensagens pelo SMTP da conta (e guarda cópia em Enviados quando o provedor não faz isso sozinho).</summary>
public interface IMailSender
{
    /// <param name="loadAttachment">Conteúdo de anexos que vêm de outra mensagem (encaminhar).</param>
    Task SendAsync(OutgoingMessage message, Func<OutgoingAttachment, CancellationToken, Task<byte[]>> loadAttachment,
        CancellationToken ct = default);

    /// <summary>
    /// Grava o rascunho na pasta Rascunhos do servidor, substituindo a versão anterior (<paramref name="previous"/>).
    /// Retorna onde ficou a nova cópia, ou nulo se a conta não tem pasta de rascunhos.
    /// </summary>
    Task<ServerDraft?> SaveDraftAsync(Guid draftId, OutgoingMessage message, ServerDraft? previous,
        Func<OutgoingAttachment, CancellationToken, Task<byte[]>> loadAttachment, CancellationToken ct = default);

    /// <summary>Apaga definitivamente a cópia do rascunho no servidor (após enviar ou descartar).</summary>
    Task DeleteDraftAsync(Guid accountId, ServerDraft draft, CancellationToken ct = default);
}
