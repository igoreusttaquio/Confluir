using System.Collections.Concurrent;
using Confluir.Core.Compose;
using Confluir.Core.Services;

namespace Confluir.App.DesignData;

/// <summary>Rascunhos do modo de dados falsos: só na memória, para não misturar com os do banco real.</summary>
public sealed class InMemoryDraftStore : IDraftStore
{
    private readonly ConcurrentDictionary<Guid, Draft> _drafts = new();

    public Task<Draft?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_drafts.TryGetValue(id, out var d) ? d : null);

    public Task<IReadOnlyList<Draft>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Draft>>(_drafts.Values.OrderByDescending(d => d.UpdatedAt).ToList());

    public Task SaveAsync(Draft draft, CancellationToken ct = default)
    {
        _drafts[draft.Id] = draft;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        _drafts.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}
