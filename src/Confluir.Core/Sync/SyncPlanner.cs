namespace Confluir.Core.Sync;

/// <param name="Reset">UIDVALIDITY mudou (ou primeira sincronização com cache antigo): apagar a pasta e recomeçar.</param>
/// <param name="FetchChunks">UIDs a baixar, em lotes, do mais novo para o mais antigo.</param>
/// <param name="Remove">UIDs no cache que não existem mais no servidor.</param>
public sealed record SyncPlan(bool Reset, IReadOnlyList<IReadOnlyList<uint>> FetchChunks, IReadOnlyList<uint> Remove)
{
    public int FetchCount => FetchChunks.Sum(c => c.Count);
}

/// <summary>
/// Decide o que sincronizar numa pasta comparando os UIDs do servidor com os do cache.
/// Cobre mensagens novas, o preenchimento inicial/retomado das antigas e as apagadas no servidor.
/// </summary>
public static class SyncPlanner
{
    public const int DefaultChunkSize = 500;

    public static SyncPlan Plan(
        uint? cachedUidValidity,
        uint serverUidValidity,
        IReadOnlyCollection<uint> serverUids,
        IReadOnlyCollection<uint> cachedUids,
        int chunkSize = DefaultChunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);

        // Com UIDVALIDITY diferente os UIDs do cache não significam mais nada.
        var reset = cachedUidValidity is { } v && v != serverUidValidity;
        var cached = reset ? new HashSet<uint>() : cachedUids.ToHashSet();
        var server = serverUids as ISet<uint> ?? serverUids.ToHashSet();

        var missing = server.Where(u => !cached.Contains(u)).OrderByDescending(u => u).ToList();
        var chunks = missing.Chunk(chunkSize).Select(c => (IReadOnlyList<uint>)c).ToList();
        var remove = reset ? [] : cached.Where(u => !server.Contains(u)).OrderBy(u => u).ToList();

        return new SyncPlan(reset, chunks, remove);
    }
}
