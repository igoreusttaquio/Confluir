using Confluir.Core;
using Confluir.Core.Models;
using Confluir.Core.Services;
using Confluir.Core.Sync;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Logging;
using MailFolder = Confluir.Core.Models.MailFolder;

namespace Confluir.Mail;

/// <summary>
/// Sincroniza o cache com o servidor IMAP: lista de pastas, cabeçalhos por UID, flags e remoções.
/// Cada lote usa a conexão da conta só pelo tempo necessário, para não bloquear a leitura de corpos.
/// </summary>
public sealed class ImapSyncEngine(ImapConnectionPool pool, IMailCache cache, ILogger<ImapSyncEngine> logger)
{
    private const string InboxName = "INBOX";
    private const int FlagChunkSize = 2000;

    private static readonly IFetchRequest SummaryRequest = new FetchRequest(
        MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags |
        MessageSummaryItems.InternalDate | MessageSummaryItems.Size | MessageSummaryItems.BodyStructure |
        MessageSummaryItems.PreviewText);

    private static readonly SpecialFolderKind[] FolderOrder =
    [
        SpecialFolderKind.Inbox, SpecialFolderKind.Drafts, SpecialFolderKind.Sent,
        SpecialFolderKind.Archive, SpecialFolderKind.Junk, SpecialFolderKind.Trash,
    ];

    /// <summary>Atualiza a lista de pastas da conta no cache e devolve a versão do cache (com contagens).</summary>
    /// <param name="lane">Depois de criar uma pasta (o usuário espera), a conexão de leitura.</param>
    public async Task<IReadOnlyList<MailFolder>> SyncFolderListAsync(MailAccount account, CancellationToken ct,
        ImapLane lane = ImapLane.Sync)
    {
        var folders = await pool.RunAsync<IReadOnlyList<MailFolder>>(account.Id, async (client, token) =>
        {
            var ns = client.PersonalNamespaces.FirstOrDefault() ?? new FolderNamespace('/', "");
            var remote = await client.GetFoldersAsync(ns, StatusItems.None, subscribedOnly: false, token);

            var result = new List<MailFolder>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in remote.Prepend(client.Inbox))
            {
                if (folder.Attributes.HasFlag(FolderAttributes.NoSelect) ||
                    folder.Attributes.HasFlag(FolderAttributes.NonExistent) ||
                    !seen.Add(folder.FullName))
                    continue;

                var isInbox = ReferenceEquals(folder, client.Inbox);
                var kind = SpecialFolderResolver.Resolve(folder.Attributes, folder.Name, folder.FullName, isInbox);
                var fullName = isInbox ? InboxName : folder.FullName;
                var name = kind == SpecialFolderKind.None && folder.DirectorySeparator != '\0'
                    ? folder.FullName.Replace(folder.DirectorySeparator.ToString(), " / ")
                    : folder.Name;
                result.Add(new MailFolder(StableId.From(account.Id, fullName), account.Id, name, fullName, kind, 0));
            }

            return result
                .OrderBy(f => f.Kind == SpecialFolderKind.None ? int.MaxValue : Array.IndexOf(FolderOrder, f.Kind))
                .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }, ct, lane);

        await cache.SaveFoldersAsync(account.Id, folders, ct);
        return await cache.GetFoldersAsync(account.Id, ct);
    }

    /// <summary>
    /// Cria a pasta no servidor (cada trecho de "Mãe/Filha" que ainda não existir) e a assina. Devolve o nome completo.
    /// Usa a conexão de leitura: o usuário está esperando e a sincronização pode estar no meio de um lote longo.
    /// </summary>
    public Task<string> CreateFolderAsync(MailAccount account, string name, CancellationToken ct) =>
        pool.RunAsync(account.Id, async (client, token) =>
        {
            var ns = client.PersonalNamespaces.FirstOrDefault() ?? new FolderNamespace('/', "");
            IMailFolder current = client.GetFolder(ns)
                ?? throw new InvalidOperationException("O servidor não informou a pasta raiz das pastas pessoais.");
            var separators = new[] { '/', ns.DirectorySeparator }.Where(c => c != '\0').Distinct().ToArray();
            foreach (var part in name.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                IMailFolder? existing = null;
                try
                {
                    existing = await current.GetSubfolderAsync(part, token);
                }
                catch (FolderNotFoundException)
                {
                }
                current = existing ?? await current.CreateAsync(part, isMessageFolder: true, token)
                    ?? throw new InvalidOperationException("O servidor não devolveu a pasta criada.");
                try
                {
                    await current.SubscribeAsync(token);
                }
                catch (ImapCommandException ex)
                {
                    logger.LogDebug(ex, "Servidor recusou assinar a pasta {Folder}", current.FullName);
                }
            }
            logger.LogInformation("Pasta criada na conta {AccountId}", account.Id);
            return current.FullName;
        }, ct, ImapLane.Reading);

    /// <summary>
    /// Sincroniza uma pasta: novas mensagens e preenchimento das antigas (do mais novo para o mais antigo, em lotes),
    /// remoções e flags. <paramref name="onProgress"/> é chamado a cada lote gravado, para a interface atualizar.
    /// </summary>
    /// <param name="arrived">Recebe as mensagens que chegaram desde a última sincronização (não as da primeira carga
    /// nem as antigas do preenchimento), para as regras.</param>
    /// <returns>Verdadeiro se algo mudou no cache.</returns>
    public async Task<bool> SyncFolderAsync(MailAccount account, MailFolder folder, Action onProgress, CancellationToken ct,
        List<Core.Models.MessageSummary>? arrived = null)
    {
        var state = await cache.GetSyncStateAsync(folder.Id, ct);
        var cachedUids = await cache.GetUidsAsync(folder.Id, ct);

        var (uidValidity, serverUids, highestModSeq, condStore) = await pool.RunAsync(account.Id, async (client, token) =>
        {
            var f = await OpenAsync(client, folder.FullName, FolderAccess.ReadOnly, token);
            var uids = await f.SearchAsync(SearchQuery.All, token);
            return (f.UidValidity, uids.Select(u => u.Id).ToList(),
                f.HighestModSeq, client.Capabilities.HasFlag(ImapCapabilities.CondStore));
        }, ct);

        var plan = SyncPlanner.Plan(state?.UidValidity, uidValidity, serverUids, cachedUids);
        var changed = false;
        // Novas de verdade: UID acima do maior já visto, numa pasta que já tinha sido sincronizada.
        var newerThan = state is not null && !plan.Reset && cachedUids.Count > 0 ? cachedUids.Max() : (uint?)null;

        if (plan.Reset)
        {
            logger.LogInformation("UIDVALIDITY mudou na pasta {FolderId}; refazendo o cache", folder.Id);
            await cache.ClearFolderAsync(folder.Id, ct);
            changed = true;
        }
        if (plan.Remove.Count > 0)
        {
            await cache.DeleteMessagesAsync(folder.Id, plan.Remove, ct);
            changed = true;
        }

        foreach (var chunk in plan.FetchChunks)
        {
            var messages = await pool.RunAsync(account.Id, async (client, token) =>
            {
                var f = await OpenAsync(client, folder.FullName, FolderAccess.ReadOnly, token);
                var ids = chunk.Select(u => new UniqueId(uidValidity, u)).ToList();
                var summaries = await f.FetchAsync(ids, SummaryRequest, token);
                return summaries.Select(s => new CachedMessage(
                    MessageConverter.ToSummary(s, StableId.From(account.Id, folder.FullName, uidValidity, s.UniqueId.Id),
                        account.Id, folder.Id),
                    s.UniqueId.Id)).ToList();
            }, ct);
            await cache.UpsertMessagesAsync(folder.Id, messages, ct);
            if (arrived is not null && newerThan is { } max)
                arrived.AddRange(messages.Where(m => m.Uid > max).Select(m => m.Summary));
            changed = true;
            onProgress();
        }

        // Flags das mensagens que já estavam no cache (as recém-baixadas já vieram com flags atuais).
        var existing = plan.Reset ? [] : cachedUids.Except(plan.Remove).ToList();
        if (existing.Count > 0)
        {
            var updates = await FetchFlagChangesAsync(account, folder, uidValidity, existing,
                condStore && state is { HighestModSeq: > 0 } ? state.HighestModSeq : 0, ct);
            if (updates.Count > 0)
            {
                await cache.UpdateFlagsAsync(folder.Id, updates, ct);
                changed = true;
            }
        }

        if (!plan.Reset)
            changed |= await BackfillRecipientsAsync(account, folder, uidValidity, ct);

        await cache.SetSyncStateAsync(folder.Id,
            new FolderSyncState(uidValidity, condStore ? highestModSeq : 0, DateTimeOffset.UtcNow), ct);
        if (plan.FetchCount > 0 || plan.Remove.Count > 0 || plan.Reset)
            logger.LogInformation("Pasta {FolderId}: {Fetched} baixadas, {Removed} removidas",
                folder.Id, plan.FetchCount, plan.Remove.Count);
        return changed;
    }

    /// <summary>
    /// Mensagens gravadas antes de o cache guardar destinatários: busca só o envelope (Para/Cc), em lotes, para
    /// regras por "Para" e a busca <c>para:</c> valerem também para o histórico.
    /// </summary>
    private async Task<bool> BackfillRecipientsAsync(MailAccount account, MailFolder folder, uint uidValidity, CancellationToken ct)
    {
        var missing = await cache.GetUidsWithoutRecipientsAsync(folder.Id, RecipientBackfillBatch, ct);
        if (missing.Count == 0)
            return false;
        var found = await pool.RunAsync(account.Id, async (client, token) =>
        {
            var f = await OpenAsync(client, folder.FullName, FolderAccess.ReadOnly, token);
            var summaries = await f.FetchAsync(missing.Select(u => new UniqueId(uidValidity, u)).ToList(),
                new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope), token);
            return summaries.Select(s => (s.UniqueId.Id, MessageConverter.RecipientsOf(s.Envelope))).ToList();
        }, ct);
        // As que o servidor não devolveu (apagadas) também são marcadas, para não serem pedidas de novo.
        var byUid = found.ToDictionary(f => f.Item1, f => f.Item2);
        await cache.SetRecipientsAsync(folder.Id, missing.Select(u => (u, byUid.GetValueOrDefault(u))).ToList(), ct);
        return true;
    }

    private const int RecipientBackfillBatch = 500;

    /// <summary>
    /// Busca no servidor e grava no cache as mensagens encontradas que ainda não estavam nele
    /// (as 200 mais recentes). Retorna quantas entraram.
    /// </summary>
    public async Task<int> SearchServerAsync(MailAccount account, MailFolder folder, Core.Search.SearchQuery query, CancellationToken ct)
    {
        if (ToImapQuery(query) is not { } imapQuery)
            return 0;
        var cached = (await cache.GetUidsAsync(folder.Id, ct)).ToHashSet();
        var (uidValidity, found) = await pool.RunAsync(account.Id, async (client, token) =>
        {
            var f = await OpenAsync(client, folder.FullName, FolderAccess.ReadOnly, token);
            var uids = await f.SearchAsync(imapQuery, token);
            return (f.UidValidity, uids.Select(u => u.Id).ToList());
        }, ct);

        var missing = found.Where(u => !cached.Contains(u)).OrderByDescending(u => u).Take(200).ToList();
        if (missing.Count == 0)
            return 0;
        var messages = await pool.RunAsync(account.Id, async (client, token) =>
        {
            var f = await OpenAsync(client, folder.FullName, FolderAccess.ReadOnly, token);
            var summaries = await f.FetchAsync(missing.Select(u => new UniqueId(uidValidity, u)).ToList(), SummaryRequest, token);
            return summaries.Select(s => new CachedMessage(
                MessageConverter.ToSummary(s, StableId.From(account.Id, folder.FullName, uidValidity, s.UniqueId.Id),
                    account.Id, folder.Id),
                s.UniqueId.Id)).ToList();
        }, ct);
        await cache.UpsertMessagesAsync(folder.Id, messages, ct);
        logger.LogInformation("Busca no servidor trouxe {Count} mensagens (pasta {FolderId})", messages.Count, folder.Id);
        return messages.Count;
    }

    /// <summary>Critério IMAP equivalente (texto solto procura em assunto, remetente e corpo). "Tem anexo" não existe no IMAP.</summary>
    public static SearchQuery? ToImapQuery(Core.Search.SearchQuery query)
    {
        var parts = new List<SearchQuery>();
        parts.AddRange(query.Terms.Select(t => SearchQuery.SubjectContains(t).Or(SearchQuery.FromContains(t)).Or(SearchQuery.BodyContains(t))));
        parts.AddRange(query.From.Select(SearchQuery.FromContains));
        parts.AddRange(query.To.Select(SearchQuery.ToContains));
        parts.AddRange(query.Subject.Select(SearchQuery.SubjectContains));
        if (parts.Count == 0)
            return null;
        if (query.Unread)
            parts.Add(SearchQuery.NotSeen);
        return parts.Aggregate((a, b) => a.And(b));
    }

    /// <summary>
    /// Com CONDSTORE, pede só o que mudou desde o último MODSEQ; sem ele, relê as flags de todas as mensagens em cache.
    /// </summary>
    private async Task<List<FlagUpdate>> FetchFlagChangesAsync(MailAccount account, MailFolder folder, uint uidValidity,
        IReadOnlyList<uint> uids, ulong changedSince, CancellationToken ct)
    {
        var updates = new List<FlagUpdate>();
        var chunks = changedSince > 0 ? [uids.ToArray()] : uids.Chunk(FlagChunkSize);
        foreach (var chunk in chunks)
        {
            var summaries = await pool.RunAsync(account.Id, async (client, token) =>
            {
                var f = await OpenAsync(client, folder.FullName, FolderAccess.ReadOnly, token);
                var request = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Flags);
                if (changedSince > 0)
                {
                    request.ChangedSince = changedSince;
                    return await f.FetchAsync(UniqueIdRange.All, request, token);
                }
                return await f.FetchAsync(chunk.Select(u => new UniqueId(uidValidity, u)).ToList(), request, token);
            }, ct);

            updates.AddRange(summaries
                .Where(s => s.Flags is not null)
                .Select(s => new FlagUpdate(s.UniqueId.Id,
                    s.Flags!.Value.HasFlag(MessageFlags.Seen),
                    s.Flags.Value.HasFlag(MessageFlags.Flagged))));
        }
        return updates;
    }

    /// <param name="lane">Abrir uma mensagem usa a conexão de leitura, para não esperar a sincronização;
    /// o download antecipado em segundo plano usa a de sincronização.</param>
    public Task<MessageBody> FetchBodyAsync(MessageLocation location, CancellationToken ct, ImapLane lane = ImapLane.Reading) =>
        pool.RunAsync(location.AccountId, async (client, token) =>
        {
            var f = await OpenAsync(client, location.FolderFullName, FolderAccess.ReadOnly, token);
            // GetMessage usa BODY.PEEK: não marca como lida por conta própria.
            var mime = await f.GetMessageAsync(new UniqueId(f.UidValidity, location.Uid), token);
            return MessageConverter.ToBody(mime, location.MessageId);
        }, ct, lane);

    public Task<byte[]> FetchAttachmentAsync(MessageLocation location, int index, CancellationToken ct) =>
        pool.RunAsync(location.AccountId, async (client, token) =>
        {
            var f = await OpenAsync(client, location.FolderFullName, FolderAccess.ReadOnly, token);
            var mime = await f.GetMessageAsync(new UniqueId(f.UidValidity, location.Uid), token);
            return MessageConverter.ReadAttachment(mime, index, token);
        }, ct, ImapLane.Reading);

    /// <param name="folders">Pastas da conta, para achar Arquivo e Lixeira.</param>
    /// <param name="targetFolder">Destino de <see cref="OperationKind.Move"/>.</param>
    public Task ApplyAsync(OperationKind kind, MessageLocation location, IReadOnlyList<MailFolder> folders, CancellationToken ct,
        string? targetFolder = null) =>
        pool.RunAsync(location.AccountId, async (client, token) =>
        {
            var f = await OpenAsync(client, location.FolderFullName, FolderAccess.ReadWrite, token);
            var uid = new UniqueId(f.UidValidity, location.Uid);
            switch (kind)
            {
                case OperationKind.MarkRead or OperationKind.MarkUnread:
                    await StoreAsync(f, uid, kind == OperationKind.MarkRead, MessageFlags.Seen, token);
                    break;
                case OperationKind.Flag or OperationKind.Unflag:
                    await StoreAsync(f, uid, kind == OperationKind.Flag, MessageFlags.Flagged, token);
                    break;
                case OperationKind.Archive:
                    var archive = folders.FirstOrDefault(x => x.Kind == SpecialFolderKind.Archive)
                        ?? throw new InvalidOperationException("A conta não tem pasta de arquivo.");
                    if (archive.FullName != location.FolderFullName)
                        await f.MoveToAsync(uid, await client.GetFolderAsync(archive.FullName, token), token);
                    break;
                case OperationKind.Move:
                    if (string.IsNullOrEmpty(targetFolder))
                        throw new InvalidOperationException("Pasta de destino não informada.");
                    if (targetFolder != location.FolderFullName)
                        await f.MoveToAsync(uid, await client.GetFolderAsync(targetFolder, token), token);
                    break;
                case OperationKind.Delete:
                    var trash = folders.FirstOrDefault(x => x.Kind == SpecialFolderKind.Trash);
                    if (trash is not null && trash.FullName != location.FolderFullName)
                    {
                        await f.MoveToAsync(uid, await client.GetFolderAsync(trash.FullName, token), token);
                        break;
                    }
                    // Já na Lixeira (ou sem Lixeira): exclusão definitiva só desta mensagem.
                    await StoreAsync(f, uid, true, MessageFlags.Deleted, token);
                    if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
                        await f.ExpungeAsync([uid], token);
                    else
                        await f.ExpungeAsync(token);
                    break;
            }
            return true;
        }, ct);

    private static Task StoreAsync(IMailFolder folder, UniqueId uid, bool add, MessageFlags flag, CancellationToken ct) =>
        folder.StoreAsync(uid, new StoreFlagsRequest(add ? StoreAction.Add : StoreAction.Remove, flag) { Silent = true }, ct);

    private static async Task<IMailFolder> OpenAsync(ImapClient client, string fullName, FolderAccess access, CancellationToken ct)
    {
        var folder = fullName == InboxName ? client.Inbox : await client.GetFolderAsync(fullName, ct);
        if (!folder.IsOpen || (access == FolderAccess.ReadWrite && folder.Access != FolderAccess.ReadWrite))
            await folder.OpenAsync(access, ct);
        return folder;
    }
}
