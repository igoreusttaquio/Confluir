using Confluir.Core.Compose;
using Confluir.Core.Models;
using Confluir.Core.Services;

namespace Confluir.App.DesignData;

/// <summary>Envio simulado para o modo de dados falsos.</summary>
public sealed class FakeMailSender : IMailSender
{
    private uint _nextUid = 1;

    public async Task SendAsync(OutgoingMessage message, Func<OutgoingAttachment, CancellationToken, Task<byte[]>> loadAttachment,
        CancellationToken ct = default) => await Task.Delay(400, ct);

    public Task<ServerDraft?> SaveDraftAsync(Guid draftId, OutgoingMessage message, ServerDraft? previous,
        Func<OutgoingAttachment, CancellationToken, Task<byte[]>> loadAttachment, CancellationToken ct = default) =>
        Task.FromResult<ServerDraft?>(new ServerDraft("Drafts", _nextUid++));

    public Task DeleteDraftAsync(Guid accountId, ServerDraft draft, CancellationToken ct = default) => Task.CompletedTask;
}
