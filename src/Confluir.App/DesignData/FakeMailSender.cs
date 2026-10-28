using Confluir.Core.Models;
using Confluir.Core.Services;

namespace Confluir.App.DesignData;

/// <summary>Envio simulado para o modo de dados falsos.</summary>
public sealed class FakeMailSender : IMailSender
{
    public async Task SendAsync(OutgoingMessage message, Func<OutgoingAttachment, CancellationToken, Task<byte[]>> loadAttachment,
        CancellationToken ct = default) => await Task.Delay(400, ct);
}
