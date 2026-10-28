using System.Globalization;
using System.Text.Json;
using Confluir.Core.Models;
using Confluir.Core.Search;
using Confluir.Core.Services;
using Dapper;

namespace Confluir.Data;

/// <summary>Cache local em SQLite. Escritas são serializadas; leituras rodam em paralelo (WAL).</summary>
public sealed class SqliteMailCache(Database db) : IMailCache
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    // Pastas ------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(Guid accountId, CancellationToken ct = default)
    {
        await ThreadPoolSwitch.Instance;
        await using var c = await db.OpenAsync(ct);
        var rows = await c.QueryAsync<FolderRow>(new CommandDefinition("""
            SELECT f.id AS Id, f.account_id AS AccountId, f.name AS Name, f.full_name AS FullName, f.kind AS Kind,
                   (SELECT COUNT(*) FROM messages m WHERE m.folder_id = f.id AND m.is_read = 0) AS Unread
            FROM folders f WHERE f.account_id = @a ORDER BY f.position
            """, new { a = accountId.ToString() }, cancellationToken: ct));
        return rows.Select(r => new MailFolder(Guid.Parse(r.Id), Guid.Parse(r.AccountId), r.Name, r.FullName,
            (SpecialFolderKind)r.Kind, (int)r.Unread)).ToList();
    }

    public Task SaveFoldersAsync(Guid accountId, IReadOnlyList<MailFolder> folders, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            var account = accountId.ToString();
            for (var i = 0; i < folders.Count; i++)
            {
                var f = folders[i];
                await c.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO folders (id, account_id, full_name, name, kind, position)
                    VALUES (@id, @account, @fullName, @name, @kind, @position)
                    ON CONFLICT (id) DO UPDATE SET name = excluded.name, kind = excluded.kind, position = excluded.position
                    """, new { id = f.Id.ToString(), account, fullName = f.FullName, name = f.Name, kind = (int)f.Kind, position = i },
                    tx, cancellationToken: ct));
            }
            // Pastas que sumiram do servidor: apaga (mensagens e corpos vão em cascata).
            var keep = folders.Select(f => f.Id.ToString()).ToList();
            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM folders WHERE account_id = @account AND id NOT IN @keep",
                new { account, keep }, tx, cancellationToken: ct));
        }, ct);

    public async Task<FolderSyncState?> GetSyncStateAsync(Guid folderId, CancellationToken ct = default)
    {
        await ThreadPoolSwitch.Instance;
        await using var c = await db.OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<SyncRow>(new CommandDefinition(
            "SELECT uid_validity AS UidValidity, highest_modseq AS HighestModSeq, last_synced_at AS LastSyncedAt FROM folders WHERE id = @id",
            new { id = folderId.ToString() }, cancellationToken: ct));
        return row?.UidValidity is { } v
            ? new FolderSyncState((uint)v, (ulong)row.HighestModSeq,
                row.LastSyncedAt is { } s ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture) : DateTimeOffset.MinValue)
            : null;
    }

    public Task SetSyncStateAsync(Guid folderId, FolderSyncState state, CancellationToken ct = default) =>
        WriteAsync((c, tx) => c.ExecuteAsync(new CommandDefinition(
            "UPDATE folders SET uid_validity = @v, highest_modseq = @m, last_synced_at = @t WHERE id = @id",
            new
            {
                id = folderId.ToString(),
                v = (long)state.UidValidity,
                m = (long)state.HighestModSeq,
                t = state.LastSyncedAt.ToString("O", CultureInfo.InvariantCulture),
            }, tx, cancellationToken: ct)), ct);

    // Mensagens ---------------------------------------------------------------------------------

    private const string MessageColumns = """
        id AS Id, account_id AS AccountId, folder_id AS FolderId, from_name AS FromName, from_address AS FromAddress,
        subject AS Subject, preview AS Preview, date_utc_ms AS DateUtcMs, date_offset_min AS DateOffsetMin,
        is_read AS IsRead, is_flagged AS IsFlagged, has_attachments AS HasAttachments, recipients AS Recipients
        """;

    public async Task<IReadOnlyList<MessageSummary>> GetMessagesAsync(Guid folderId, CancellationToken ct = default)
    {
        await ThreadPoolSwitch.Instance;
        await using var c = await db.OpenAsync(ct);
        var rows = await c.QueryAsync<MessageRow>(new CommandDefinition(
            $"SELECT {MessageColumns} FROM messages WHERE folder_id = @f ORDER BY date_utc_ms DESC",
            new { f = folderId.ToString() }, cancellationToken: ct));
        return rows.Select(ToSummary).ToList();
    }

    public async Task<IReadOnlyList<MessageSummary>> GetInboxMessagesAsync(CancellationToken ct = default)
    {
        await ThreadPoolSwitch.Instance;
        await using var c = await db.OpenAsync(ct);
        var rows = await c.QueryAsync<MessageRow>(new CommandDefinition($"""
            SELECT {MessageColumns} FROM messages
            WHERE folder_id IN (SELECT id FROM folders WHERE kind = @inbox)
            ORDER BY date_utc_ms DESC
            """, new { inbox = (int)SpecialFolderKind.Inbox }, cancellationToken: ct));
        return rows.Select(ToSummary).ToList();
    }

    public Task UpsertMessagesAsync(Guid folderId, IReadOnlyList<CachedMessage> messages, CancellationToken ct = default) =>
        messages.Count == 0 ? Task.CompletedTask : WriteAsync(async (c, tx) =>
        {
            foreach (var m in messages)
            {
                var s = m.Summary;
                await c.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO messages (id, account_id, folder_id, uid, from_name, from_address, subject, preview,
                                          date_utc_ms, date_offset_min, is_read, is_flagged, has_attachments, recipients)
                    VALUES (@id, @account, @folder, @uid, @fromName, @fromAddress, @subject, @preview,
                            @dateMs, @offset, @read, @flagged, @attachments, @recipients)
                    ON CONFLICT (folder_id, uid) DO UPDATE SET
                        id = excluded.id, from_name = excluded.from_name, from_address = excluded.from_address,
                        subject = excluded.subject, preview = excluded.preview, date_utc_ms = excluded.date_utc_ms,
                        date_offset_min = excluded.date_offset_min, is_read = excluded.is_read,
                        is_flagged = excluded.is_flagged, has_attachments = excluded.has_attachments,
                        recipients = excluded.recipients
                    """, new
                {
                    id = s.Id.ToString(),
                    account = s.AccountId.ToString(),
                    folder = folderId.ToString(),
                    uid = (long)m.Uid,
                    fromName = s.FromName,
                    fromAddress = s.FromAddress,
                    subject = s.Subject,
                    preview = s.Preview,
                    dateMs = s.Date.ToUnixTimeMilliseconds(),
                    offset = (int)s.Date.Offset.TotalMinutes,
                    read = s.IsRead,
                    flagged = s.IsFlagged,
                    attachments = s.HasAttachments,
                    recipients = s.Recipients ?? "",
                }, tx, cancellationToken: ct));
            }
        }, ct);

    public async Task<IReadOnlyList<uint>> GetUidsAsync(Guid folderId, CancellationToken ct = default)
    {
        await ThreadPoolSwitch.Instance;
        await using var c = await db.OpenAsync(ct);
        var uids = await c.QueryAsync<long>(new CommandDefinition(
            "SELECT uid FROM messages WHERE folder_id = @f", new { f = folderId.ToString() }, cancellationToken: ct));
        return uids.Select(u => (uint)u).ToList();
    }

    public Task DeleteMessagesAsync(Guid folderId, IReadOnlyCollection<uint> uids, CancellationToken ct = default) =>
        uids.Count == 0 ? Task.CompletedTask : WriteAsync(async (c, tx) =>
        {
            // Em lotes: o SQLite limita o número de parâmetros por comando.
            foreach (var chunk in uids.Chunk(500))
            {
                await c.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM messages WHERE folder_id = @f AND uid IN @uids",
                    new { f = folderId.ToString(), uids = chunk.Select(u => (long)u).ToList() }, tx, cancellationToken: ct));
            }
        }, ct);

    public Task ClearFolderAsync(Guid folderId, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM messages WHERE folder_id = @f", new { f = folderId.ToString() }, tx, cancellationToken: ct));
            await c.ExecuteAsync(new CommandDefinition(
                "UPDATE folders SET uid_validity = NULL, highest_modseq = 0 WHERE id = @f",
                new { f = folderId.ToString() }, tx, cancellationToken: ct));
        }, ct);

    public Task UpdateFlagsAsync(Guid folderId, IReadOnlyList<FlagUpdate> flags, CancellationToken ct = default) =>
        flags.Count == 0 ? Task.CompletedTask : WriteAsync(async (c, tx) =>
        {
            foreach (var f in flags)
            {
                await c.ExecuteAsync(new CommandDefinition(
                    "UPDATE messages SET is_read = @read, is_flagged = @flagged WHERE folder_id = @folder AND uid = @uid",
                    new { folder = folderId.ToString(), uid = (long)f.Uid, read = f.IsRead, flagged = f.IsFlagged },
                    tx, cancellationToken: ct));
            }
        }, ct);

    public Task SetReadAsync(Guid messageId, bool isRead, CancellationToken ct = default) =>
        WriteAsync((c, tx) => c.ExecuteAsync(new CommandDefinition(
            "UPDATE messages SET is_read = @read WHERE id = @id",
            new { id = messageId.ToString(), read = isRead }, tx, cancellationToken: ct)), ct);

    public Task SetFlaggedAsync(Guid messageId, bool isFlagged, CancellationToken ct = default) =>
        WriteAsync((c, tx) => c.ExecuteAsync(new CommandDefinition(
            "UPDATE messages SET is_flagged = @flagged WHERE id = @id",
            new { id = messageId.ToString(), flagged = isFlagged }, tx, cancellationToken: ct)), ct);

    public Task DeleteMessageAsync(Guid messageId, CancellationToken ct = default) =>
        WriteAsync((c, tx) => c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM messages WHERE id = @id", new { id = messageId.ToString() }, tx, cancellationToken: ct)), ct);

    public async Task<IReadOnlyList<MailAddress>> SearchAddressesAsync(string text, int limit, CancellationToken ct = default)
    {
        await ThreadPoolSwitch.Instance;
        if (string.IsNullOrWhiteSpace(text))
            return [];
        if (ct.IsCancellationRequested)
            return [];
        await using var c = await db.OpenAsync(ct);
        // Uma passada só: com MAX(), o SQLite devolve as colunas "soltas" (endereço e nome) da linha mais recente
        // de cada grupo — o nome mais atual de cada endereço, sem subconsulta por grupo. Mais frequentes primeiro.
        var rows = await c.QueryAsync<AddressRow>(new CommandDefinition("""
            SELECT from_address AS Address, from_name AS Name, MAX(date_utc_ms) AS LastSeen, COUNT(*) AS Uses
            FROM messages
            WHERE from_address <> '' AND (from_address LIKE @p ESCAPE '\' OR from_name LIKE @p ESCAPE '\')
            GROUP BY lower(from_address)
            ORDER BY Uses DESC
            LIMIT @limit
            """, new { p = "%" + EscapeLike(text.Trim()) + "%", limit }, cancellationToken: ct));
        return rows.Select(r => new MailAddress(r.Name ?? "", r.Address)).ToList();
    }

public async Task<IReadOnlyList<MessageSummary>> SearchMessagesAsync(MessageSearch search, int limit, CancellationToken ct = default)
    {
        await ThreadPoolSwitch.Instance;
        var q = search.Query;
        if (q.IsEmpty || ct.IsCancellationRequested)
            return [];
        await using var c = await db.OpenAsync(ct);
        var rows = await c.QueryAsync<MessageRow>(new CommandDefinition($"""
            SELECT {MessageColumns} FROM messages
            WHERE (@fts IS NULL OR rowid IN (SELECT rowid FROM messages_fts WHERE messages_fts MATCH @fts))
              AND (@account IS NULL OR account_id = @account)
              AND (@folder IS NULL OR folder_id = @folder)
              AND (@attachments = 0 OR has_attachments = 1)
              AND (@unread = 0 OR is_read = 0)
              AND (@since IS NULL OR date_utc_ms >= @since)
            ORDER BY date_utc_ms DESC
            LIMIT @limit
            """, new
        {
            fts = q.ToFtsMatch(),
            account = search.AccountId?.ToString(),
            folder = search.FolderId?.ToString(),
            attachments = q.HasAttachment,
            unread = q.Unread,
            since = search.Since?.ToUnixTimeMilliseconds(),
            limit,
        }, cancellationToken: ct));
        return rows.Select(ToSummary).ToList();
    }

    private static string EscapeLike(string s) => s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private sealed class AddressRow
    {
        public string Address { get; set; } = "";
        public string? Name { get; set; }
        public long LastSeen { get; set; }
        public long Uses { get; set; }
    }

    public async Task<MessageLocation?> GetLocationAsync(Guid messageId, CancellationToken ct = default)
    {
        await ThreadPoolSwitch.Instance;
        await using var c = await db.OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<LocationRow>(new CommandDefinition("""
            SELECT m.account_id AS AccountId, m.folder_id AS FolderId, f.full_name AS FullName, m.uid AS Uid
            FROM messages m JOIN folders f ON f.id = m.folder_id WHERE m.id = @id
            """, new { id = messageId.ToString() }, cancellationToken: ct));
        return row is null
            ? null
            : new MessageLocation(messageId, Guid.Parse(row.AccountId), Guid.Parse(row.FolderId), row.FullName, (uint)row.Uid);
    }

    // Corpos ------------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions BodyJson = new();

    public async Task<MessageBody?> GetBodyAsync(Guid messageId, CancellationToken ct = default)
    {
        await ThreadPoolSwitch.Instance;
        await using var c = await db.OpenAsync(ct);
        var data = await c.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT data FROM bodies WHERE message_id = @id", new { id = messageId.ToString() }, cancellationToken: ct));
        return data is null ? null : JsonSerializer.Deserialize<MessageBody>(data, BodyJson);
    }

    public Task SaveBodyAsync(MessageBody body, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO bodies (message_id, data, fetched_at)
            SELECT @id, @data, @at WHERE EXISTS (SELECT 1 FROM messages WHERE id = @id)
            ON CONFLICT (message_id) DO UPDATE SET data = excluded.data, fetched_at = excluded.fetched_at
            """, new
        {
            id = body.MessageId.ToString(),
            data = JsonSerializer.Serialize(body, BodyJson),
            at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        }, tx, cancellationToken: ct));
            await c.ExecuteAsync(new CommandDefinition(
                "UPDATE messages_fts SET body = @text WHERE rowid = (SELECT rowid FROM messages WHERE id = @id)",
                new { id = body.MessageId.ToString(), text = body.TextBody.Length > 20000 ? body.TextBody[..20000] : body.TextBody },
                tx, cancellationToken: ct));
        }, ct);

    public Task ClearBodiesAsync(CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            await c.ExecuteAsync(new CommandDefinition("DELETE FROM bodies", transaction: tx, cancellationToken: ct));
            await c.ExecuteAsync(new CommandDefinition("UPDATE messages_fts SET body = ''", transaction: tx, cancellationToken: ct));
        }, ct);

    // Fila --------------------------------------------------------------------------------------

    public Task EnqueueAsync(Guid accountId, Guid messageId, OperationKind kind, MessageLocation? location = null,
        string? targetFolder = null, DateTimeOffset? notBefore = null, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            // Lida/não lida e sinalizada/não sinalizada: só a última intenção importa.
            int[] opposites = kind switch
            {
                OperationKind.MarkRead or OperationKind.MarkUnread => [(int)OperationKind.MarkRead, (int)OperationKind.MarkUnread],
                OperationKind.Flag or OperationKind.Unflag => [(int)OperationKind.Flag, (int)OperationKind.Unflag],
                _ => [],
            };
            if (opposites.Length > 0)
            {
                await c.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM pending_ops WHERE message_id = @m AND kind IN @kinds",
                    new { m = messageId.ToString(), kinds = opposites }, tx, cancellationToken: ct));
            }
            await c.ExecuteAsync(new CommandDefinition("""
                INSERT INTO pending_ops (account_id, message_id, kind, created_at, folder_name, folder_id, uid, target_folder, not_before_ms)
                VALUES (@a, @m, @k, @t, @folderName, @folderId, @uid, @target, @notBefore)
                """,
                new
                {
                    a = accountId.ToString(),
                    m = messageId.ToString(),
                    k = (int)kind,
                    t = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    folderName = location?.FolderFullName,
                    folderId = location?.FolderId.ToString(),
                    uid = location is null ? (long?)null : location.Uid,
                    target = targetFolder,
                    notBefore = notBefore?.ToUnixTimeMilliseconds(),
                }, tx, cancellationToken: ct));
        }, ct);

    public async Task<IReadOnlyList<PendingOperation>> GetPendingAsync(Guid accountId, CancellationToken ct = default)
    {
        await ThreadPoolSwitch.Instance;
        await using var c = await db.OpenAsync(ct);
        var rows = await c.QueryAsync<PendingRow>(new CommandDefinition("""
            SELECT id AS Id, account_id AS AccountId, message_id AS MessageId, kind AS Kind, attempts AS Attempts,
                   folder_name AS FolderName, folder_id AS FolderId, uid AS Uid, target_folder AS TargetFolder,
                   not_before_ms AS NotBeforeMs
            FROM pending_ops WHERE account_id = @a ORDER BY id
            """, new { a = accountId.ToString() }, cancellationToken: ct));
        return rows.Select(r =>
        {
            var account = Guid.Parse(r.AccountId);
            var message = Guid.Parse(r.MessageId);
            var location = r.FolderName is not null && r.FolderId is not null && r.Uid is { } uid
                ? new MessageLocation(message, account, Guid.Parse(r.FolderId), r.FolderName, (uint)uid)
                : null;
            return new PendingOperation(r.Id, account, message, (OperationKind)r.Kind, (int)r.Attempts, location, r.TargetFolder,
                r.NotBeforeMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null);
        }).ToList();
    }
    public async Task<PendingOperation?> CancelPendingRemovalAsync(Guid messageId, CancellationToken ct = default)
    {
        await ThreadPoolSwitch.Instance;
        PendingOperation? cancelled = null;
        await WriteAsync(async (c, tx) =>
        {
            var row = await c.QuerySingleOrDefaultAsync<PendingRow>(new CommandDefinition("""
                SELECT id AS Id, account_id AS AccountId, message_id AS MessageId, kind AS Kind, attempts AS Attempts,
                       folder_name AS FolderName, folder_id AS FolderId, uid AS Uid, target_folder AS TargetFolder,
                       not_before_ms AS NotBeforeMs
                FROM pending_ops
                WHERE message_id = @m AND kind IN @kinds AND folder_id IS NOT NULL
                ORDER BY id DESC LIMIT 1
                """, new { m = messageId.ToString(), kinds = new[] { (int)OperationKind.Archive, (int)OperationKind.Delete, (int)OperationKind.Move } },
                tx, cancellationToken: ct));
            if (row is null)
                return;
            await c.ExecuteAsync(new CommandDefinition("DELETE FROM pending_ops WHERE id = @id", new { id = row.Id }, tx, cancellationToken: ct));
            var account = Guid.Parse(row.AccountId);
            cancelled = new PendingOperation(row.Id, account, messageId, (OperationKind)row.Kind, (int)row.Attempts,
                new MessageLocation(messageId, account, Guid.Parse(row.FolderId!), row.FolderName!, (uint)row.Uid!.Value), row.TargetFolder);
        }, ct);
        return cancelled;
    }

    public Task CompleteAsync(long operationId, CancellationToken ct = default) =>
        WriteAsync((c, tx) => c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM pending_ops WHERE id = @id", new { id = operationId }, tx, cancellationToken: ct)), ct);

    public Task RecordFailureAsync(long operationId, CancellationToken ct = default) =>
        WriteAsync((c, tx) => c.ExecuteAsync(new CommandDefinition(
            "UPDATE pending_ops SET attempts = attempts + 1 WHERE id = @id", new { id = operationId }, tx, cancellationToken: ct)), ct);

    public Task DeleteAccountAsync(Guid accountId, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            var a = accountId.ToString();
            await c.ExecuteAsync(new CommandDefinition("DELETE FROM pending_ops WHERE account_id = @a", new { a }, tx, cancellationToken: ct));
            await c.ExecuteAsync(new CommandDefinition("DELETE FROM drafts WHERE account_id = @a", new { a }, tx, cancellationToken: ct));
            await c.ExecuteAsync(new CommandDefinition("DELETE FROM folders WHERE account_id = @a", new { a }, tx, cancellationToken: ct));
        }, ct);

    // --------------------------------------------------------------------------------------------

    private async Task WriteAsync(Func<Microsoft.Data.Sqlite.SqliteConnection, System.Data.Common.DbTransaction, Task> write,
        CancellationToken ct)
    {
        await ThreadPoolSwitch.Instance;
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var c = await db.OpenAsync(ct);
            await using var tx = await c.BeginTransactionAsync(ct);
            await write(c, tx);
            await tx.CommitAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static MessageSummary ToSummary(MessageRow r)
    {
        var offset = TimeSpan.FromMinutes(r.DateOffsetMin);
        var date = DateTimeOffset.FromUnixTimeMilliseconds(r.DateUtcMs).ToOffset(offset);
        return new MessageSummary(Guid.Parse(r.Id), Guid.Parse(r.AccountId), Guid.Parse(r.FolderId),
            r.FromName, r.FromAddress, r.Subject, r.Preview, date, r.IsRead, r.IsFlagged, r.HasAttachments,
            string.IsNullOrEmpty(r.Recipients) ? null : r.Recipients);
    }

    // Linhas lidas pelo Dapper (Guid é guardado como texto).
    private sealed class FolderRow
    {
        public string Id { get; set; } = "";
        public string AccountId { get; set; } = "";
        public string Name { get; set; } = "";
        public string FullName { get; set; } = "";
        public long Kind { get; set; }
        public long Unread { get; set; }
    }

    private sealed class SyncRow
    {
        public long? UidValidity { get; set; }
        public long HighestModSeq { get; set; }
        public string? LastSyncedAt { get; set; }
    }

    private sealed class MessageRow
    {
        public string Id { get; set; } = "";
        public string AccountId { get; set; } = "";
        public string FolderId { get; set; } = "";
        public string FromName { get; set; } = "";
        public string FromAddress { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Preview { get; set; } = "";
        public long DateUtcMs { get; set; }
        public long DateOffsetMin { get; set; }
        public bool IsRead { get; set; }
        public bool IsFlagged { get; set; }
        public bool HasAttachments { get; set; }
        public string? Recipients { get; set; }
    }

    private sealed class LocationRow
    {
        public string AccountId { get; set; } = "";
        public string FolderId { get; set; } = "";
        public string FullName { get; set; } = "";
        public long Uid { get; set; }
    }

    private sealed class PendingRow
    {
        public long Id { get; set; }
        public string AccountId { get; set; } = "";
        public string MessageId { get; set; } = "";
        public long Kind { get; set; }
        public long Attempts { get; set; }
        public string? FolderName { get; set; }
        public string? FolderId { get; set; }
        public long? Uid { get; set; }
        public string? TargetFolder { get; set; }
        public long? NotBeforeMs { get; set; }
    }
}
