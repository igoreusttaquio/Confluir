using System.Collections.Concurrent;
using Confluir.Core;
using Confluir.Core.Models;
using Confluir.Core.Services;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.Extensions.Logging;
using MailFolder = Confluir.Core.Models.MailFolder;
using MessageSummary = Confluir.Core.Models.MessageSummary;

namespace Confluir.Mail;

/// <summary>
/// Contas, pastas e mensagens direto do servidor IMAP.
/// Fase 2: busca só os cabeçalhos das mensagens mais recentes de cada pasta, sem cache.
/// A sincronização incremental com SQLite substitui isto na fase 3.
/// </summary>
public sealed class ImapMailboxService(
    IAccountStore accounts,
    ImapConnectionPool pool,
    ILogger<ImapMailboxService> logger) : IMailboxService
{
    public const int RecentMessagesPerFolder = 200;
    private const string InboxName = "INBOX";

    private static readonly IFetchRequest SummaryRequest = new FetchRequest(
        MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags |
        MessageSummaryItems.InternalDate | MessageSummaryItems.Size | MessageSummaryItems.BodyStructure |
        MessageSummaryItems.PreviewText);

    private static readonly SpecialFolderKind[] FolderOrder =
    [
        SpecialFolderKind.Inbox, SpecialFolderKind.Drafts, SpecialFolderKind.Sent,
        SpecialFolderKind.Archive, SpecialFolderKind.Junk, SpecialFolderKind.Trash,
    ];

    private readonly ConcurrentDictionary<Guid, FolderRef> _folders = new();
    private readonly ConcurrentDictionary<Guid, MessageRef> _messages = new();

    public Task<IReadOnlyList<MailAccount>> GetAccountsAsync(CancellationToken ct = default) =>
        Task.FromResult(accounts.Accounts);

    public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(Guid accountId, CancellationToken ct = default) =>
        pool.RunAsync<IReadOnlyList<MailFolder>>(accountId, async (client, token) =>
        {
            var ns = client.PersonalNamespaces.FirstOrDefault() ?? new FolderNamespace('/', "");
            var remote = await client.GetFoldersAsync(ns, StatusItems.Unread, subscribedOnly: false, token);

            var result = new List<MailFolder>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in remote.Prepend(client.Inbox))
            {
                if (folder.Attributes.HasFlag(FolderAttributes.NoSelect) ||
                    folder.Attributes.HasFlag(FolderAttributes.NonExistent) ||
                    !seen.Add(folder.FullName))
                    continue;

                var isInbox = ReferenceEquals(folder, client.Inbox);
                if (isInbox && folder.Unread == 0)
                    await folder.StatusAsync(StatusItems.Unread, token);

                var kind = SpecialFolderResolver.Resolve(folder.Attributes, folder.Name, folder.FullName, isInbox);
                var fullName = isInbox ? InboxName : folder.FullName;
                var id = RegisterFolder(accountId, fullName, kind);
                var name = kind == SpecialFolderKind.None && folder.DirectorySeparator != '\0'
                    ? folder.FullName.Replace(folder.DirectorySeparator.ToString(), " / ")
                    : folder.Name;
                result.Add(new MailFolder(id, accountId, name, fullName, kind, folder.Unread));
            }

            return result
                .OrderBy(f => f.Kind == SpecialFolderKind.None ? int.MaxValue : Array.IndexOf(FolderOrder, f.Kind))
                .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }, ct);

    public Task<IReadOnlyList<MessageSummary>> GetMessagesAsync(Guid folderId, CancellationToken ct = default)
    {
        var folder = _folders.TryGetValue(folderId, out var f)
            ? f
            : throw new InvalidOperationException("Pasta desconhecida; recarregue a lista de pastas.");
        return FetchRecentAsync(folder, ct);
    }

    public async Task<IReadOnlyList<MessageSummary>> GetUnifiedInboxAsync(CancellationToken ct = default)
    {
        var all = accounts.Accounts;
        if (all.Count == 0)
            return [];

        var tasks = all.Select(async account =>
        {
            var id = RegisterFolder(account.Id, InboxName, SpecialFolderKind.Inbox);
            try
            {
                return (Messages: await FetchRecentAsync(_folders[id], ct), Error: (Exception?)null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Falha ao carregar a caixa de entrada da conta {AccountId}", account.Id);
                return (Messages: (IReadOnlyList<MessageSummary>)[], Error: ex);
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        // Uma conta com problema não esconde as outras; só falha se nenhuma respondeu.
        if (results.All(r => r.Error is not null))
            throw results[0].Error!;

        return results.SelectMany(r => r.Messages).OrderByDescending(m => m.Date).ToList();
    }

    public Task<MessageBody> GetMessageBodyAsync(Guid messageId, CancellationToken ct = default)
    {
        var message = GetMessageRef(messageId);
        return pool.RunAsync(message.AccountId, async (client, token) =>
        {
            var folder = await OpenAsync(client, message.FolderFullName, FolderAccess.ReadOnly, token);
            // GetMessage usa BODY.PEEK: não marca como lida por conta própria.
            var mime = await folder.GetMessageAsync(message.Uid, token);
            return MessageConverter.ToBody(mime, messageId);
        }, ct);
    }

    public Task MarkAsReadAsync(Guid messageId, CancellationToken ct = default)
    {
        var message = GetMessageRef(messageId);
        return pool.RunAsync(message.AccountId, async (client, token) =>
        {
            var folder = await OpenAsync(client, message.FolderFullName, FolderAccess.ReadWrite, token);
            await folder.StoreAsync(message.Uid, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Seen) { Silent = true }, token);
            return true;
        }, ct);
    }

    private Task<IReadOnlyList<MessageSummary>> FetchRecentAsync(FolderRef folderRef, CancellationToken ct) =>
        pool.RunAsync<IReadOnlyList<MessageSummary>>(folderRef.AccountId, async (client, token) =>
        {
            var folder = await OpenAsync(client, folderRef.FullName, FolderAccess.ReadOnly, token);
            if (folder.Count == 0)
                return [];

            var start = Math.Max(0, folder.Count - RecentMessagesPerFolder);
            var summaries = await folder.FetchAsync(start, -1, SummaryRequest, token);
            var folderId = StableId.From(folderRef.AccountId, folderRef.FullName);

            return summaries
                .Select(s =>
                {
                    var id = StableId.From(folderRef.AccountId, folderRef.FullName, folder.UidValidity, s.UniqueId.Id);
                    _messages[id] = new MessageRef(folderRef.AccountId, folderRef.FullName, s.UniqueId);
                    return MessageConverter.ToSummary(s, id, folderRef.AccountId, folderId);
                })
                .OrderByDescending(m => m.Date)
                .ToList();
        }, ct);

    private static async Task<IMailFolder> OpenAsync(ImapClient client, string fullName, FolderAccess access, CancellationToken ct)
    {
        var folder = fullName == InboxName ? client.Inbox : await client.GetFolderAsync(fullName, ct);
        if (!folder.IsOpen || (access == FolderAccess.ReadWrite && folder.Access != FolderAccess.ReadWrite))
            await folder.OpenAsync(access, ct);
        return folder;
    }

    private Guid RegisterFolder(Guid accountId, string fullName, SpecialFolderKind kind)
    {
        var id = StableId.From(accountId, fullName);
        _folders[id] = new FolderRef(accountId, fullName, kind);
        return id;
    }

    private MessageRef GetMessageRef(Guid messageId) =>
        _messages.TryGetValue(messageId, out var m)
            ? m
            : throw new InvalidOperationException("Mensagem desconhecida; recarregue a lista.");

    private sealed record FolderRef(Guid AccountId, string FullName, SpecialFolderKind Kind);

    private sealed record MessageRef(Guid AccountId, string FolderFullName, UniqueId Uid);
}
