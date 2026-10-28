using Confluir.Core.Models;

namespace Confluir.Core.Services;

/// <summary>Envia mensagens pelo SMTP da conta (e guarda cópia em Enviados quando o provedor não faz isso sozinho).</summary>
public interface IMailSender
{
    /// <param name="loadAttachment">Conteúdo de anexos que vêm de outra mensagem (encaminhar).</param>
    Task SendAsync(OutgoingMessage message, Func<OutgoingAttachment, CancellationToken, Task<byte[]>> loadAttachment,
        CancellationToken ct = default);
}
