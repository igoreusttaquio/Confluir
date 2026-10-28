using Confluir.Core.Models;

namespace Confluir.Core.Services;

public enum SyncStatus
{
    /// <summary>Ainda não sincronizou nesta sessão.</summary>
    Unknown,
    Syncing,
    /// <summary>Em dia com o servidor.</summary>
    UpToDate,
    /// <summary>Sem conexão com o servidor; usando o cache.</summary>
    Offline,
    /// <summary>Erro que exige ação (ex.: senha recusada).</summary>
    Error,
}

/// <summary>O conteúdo em cache de uma conta mudou (mensagens, flags ou pastas).</summary>
public sealed class MailboxChangedEventArgs(Guid accountId, bool foldersChanged) : EventArgs
{
    public Guid AccountId { get; } = accountId;
    public bool FoldersChanged { get; } = foldersChanged;
}

public sealed class AccountStatusEventArgs(Guid accountId, SyncStatus status, Exception? error = null) : EventArgs
{
    public Guid AccountId { get; } = accountId;
    public SyncStatus Status { get; } = status;

    /// <summary>Causa de <see cref="SyncStatus.Offline"/> ou <see cref="SyncStatus.Error"/>, para mensagem amigável.</summary>
    public Exception? Error { get; } = error;
}

/// <summary>Uma ação enviada ao servidor foi recusada de vez; a interface deve desfazê-la.</summary>
public sealed class OperationFailedEventArgs(Guid messageId, OperationKind kind) : EventArgs
{
    public Guid MessageId { get; } = messageId;
    public OperationKind Kind { get; } = kind;
}

/// <summary>Mensagens novas chegaram na caixa de entrada (depois das regras: as que foram movidas, arquivadas ou
/// marcadas como lidas por uma regra não entram).</summary>
public sealed class MessagesArrivedEventArgs(Guid accountId, IReadOnlyList<MessageSummary> messages) : EventArgs
{
    public Guid AccountId { get; } = accountId;
    public IReadOnlyList<MessageSummary> Messages { get; } = messages;
}
