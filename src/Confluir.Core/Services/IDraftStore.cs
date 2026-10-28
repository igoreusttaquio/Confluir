using Confluir.Core.Compose;

namespace Confluir.Core.Services;

/// <summary>Rascunhos guardados no aparelho (sobrevivem a fechar o app ou ficar sem conexão).</summary>
public interface IDraftStore
{
    Task<Draft?> GetAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Draft>> ListAsync(CancellationToken ct = default);

    Task SaveAsync(Draft draft, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
