using Confluir.Core;
using Confluir.Core.Models;
using Confluir.Core.Search;
using Confluir.Core.Services;
using Microsoft.Data.Sqlite;

namespace Confluir.Data.Tests;

public sealed class SqliteMailCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "confluir-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteMailCache _cache;
    private readonly Guid _account = Guid.NewGuid();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public SqliteMailCacheTests()
    {
        _cache = new SqliteMailCache(new Database(Path.Combine(_dir, "confluir.db")));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); // libera o arquivo para apagar
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private MailFolder Folder(string fullName, SpecialFolderKind kind = SpecialFolderKind.None) =>
        new(StableId.From(_account, fullName), _account, fullName, fullName, kind, 0);

    private CachedMessage Message(MailFolder folder, uint uid, bool read = false, DateTimeOffset? date = null) =>
        new(new MessageSummary(StableId.From(_account, folder.FullName, 1u, uid), _account, folder.Id,
            "Ana", "ana@exemplo.com", $"Assunto {uid}", "prévia", date ?? DateTimeOffset.UnixEpoch.AddDays(uid),
            IsRead: read, IsFlagged: false, HasAttachments: uid % 2 == 0), uid);

    [Fact]
    public async Task Salva_pastas_com_contagem_de_nao_lidas_e_remove_as_que_sumiram()
    {
        var inbox = Folder("INBOX", SpecialFolderKind.Inbox);
        var old = Folder("Antiga");
        await _cache.SaveFoldersAsync(_account, [inbox, old], _ct);
        await _cache.UpsertMessagesAsync(old.Id, [Message(old, 1)], _ct);
        await _cache.UpsertMessagesAsync(inbox.Id, [Message(inbox, 1), Message(inbox, 2, read: true), Message(inbox, 3)], _ct);

        await _cache.SaveFoldersAsync(_account, [inbox], _ct);

        var folder = Assert.Single(await _cache.GetFoldersAsync(_account, _ct));
        Assert.Equal(inbox.Id, folder.Id);
        Assert.Equal(2, folder.UnreadCount);
        Assert.Empty(await _cache.GetMessagesAsync(old.Id, _ct)); // apagadas em cascata
    }

    [Fact]
    public async Task Mensagens_voltam_ordenadas_e_com_data_e_fuso_preservados()
    {
        var inbox = Folder("INBOX", SpecialFolderKind.Inbox);
        await _cache.SaveFoldersAsync(_account, [inbox], _ct);
        var date = new DateTimeOffset(2026, 10, 27, 14, 45, 30, TimeSpan.FromHours(-3));
        await _cache.UpsertMessagesAsync(inbox.Id, [Message(inbox, 1), Message(inbox, 2, date: date)], _ct);

        var messages = await _cache.GetMessagesAsync(inbox.Id, _ct);

        Assert.Equal([2, 1], messages.Select(m => int.Parse(m.Subject.Split(' ')[1])));
        Assert.Equal(date, messages[0].Date);
        Assert.Equal(date.Offset, messages[0].Date.Offset);
        Assert.True(messages[0].HasAttachments);
    }

    [Fact]
    public async Task Migracao_v2_corrige_datas_invalidas_gravadas_antes()
    {
        var path = Path.Combine(_dir, "antigo.db");
        var db = new Database(path);
        var cache = new SqliteMailCache(db);
        var inbox = Folder("INBOX", SpecialFolderKind.Inbox);
        await cache.SaveFoldersAsync(_account, [inbox], _ct);
        await cache.UpsertMessagesAsync(inbox.Id, [Message(inbox, 1, date: DateTimeOffset.MinValue)], _ct);

        // Simula um banco da versão 1 reaberto pela versão atual.
        await using (var c = await db.OpenAsync(_ct))
        await using (var cmd = c.CreateCommand())
        {
            // Desfaz o que as migrações seguintes criaram (v4 adiciona colunas) e volta para a versão 1.
            cmd.CommandText = """
                ALTER TABLE pending_ops DROP COLUMN folder_name;
                ALTER TABLE pending_ops DROP COLUMN folder_id;
                ALTER TABLE pending_ops DROP COLUMN uid;
                DROP TRIGGER messages_fts_insert;
                DROP TRIGGER messages_fts_delete;
                DROP TRIGGER messages_fts_update;
                DROP TABLE messages_fts;
                ALTER TABLE pending_ops DROP COLUMN not_before_ms;
                ALTER TABLE pending_ops DROP COLUMN target_folder;
                ALTER TABLE messages DROP COLUMN recipients;
                PRAGMA user_version = 1;
                """;
            await cmd.ExecuteNonQueryAsync(_ct);
        }
        SqliteConnection.ClearAllPools();

        var reopened = new SqliteMailCache(new Database(path));
        Assert.Equal(DateTimeOffset.UnixEpoch, (await reopened.GetMessagesAsync(inbox.Id, _ct)).Single().Date);
    }

    [Fact]
    public async Task Upsert_atualiza_mensagem_existente_pelo_uid()
    {
        var inbox = Folder("INBOX", SpecialFolderKind.Inbox);
        await _cache.SaveFoldersAsync(_account, [inbox], _ct);
        await _cache.UpsertMessagesAsync(inbox.Id, [Message(inbox, 5)], _ct);
        await _cache.UpsertMessagesAsync(inbox.Id, [Message(inbox, 5, read: true)], _ct);

        var m = Assert.Single(await _cache.GetMessagesAsync(inbox.Id, _ct));
        Assert.True(m.IsRead);
    }

    [Fact]
    public async Task Caixa_unificada_junta_as_caixas_de_entrada_de_todas_as_contas()
    {
        var inbox = Folder("INBOX", SpecialFolderKind.Inbox);
        var sent = Folder("Sent", SpecialFolderKind.Sent);
        var otherAccount = Guid.NewGuid();
        var otherInbox = new MailFolder(StableId.From(otherAccount, "INBOX"), otherAccount, "INBOX", "INBOX", SpecialFolderKind.Inbox, 0);
        await _cache.SaveFoldersAsync(_account, [inbox, sent], _ct);
        await _cache.SaveFoldersAsync(otherAccount, [otherInbox], _ct);
        await _cache.UpsertMessagesAsync(inbox.Id, [Message(inbox, 1)], _ct);
        await _cache.UpsertMessagesAsync(sent.Id, [Message(sent, 2)], _ct);
        await _cache.UpsertMessagesAsync(otherInbox.Id,
            [new CachedMessage(Message(otherInbox, 3).Summary with { AccountId = otherAccount }, 3)], _ct);

        var unified = await _cache.GetInboxMessagesAsync(_ct);

        Assert.Equal(2, unified.Count);
        Assert.DoesNotContain(unified, m => m.FolderId == sent.Id);
        Assert.True(unified[0].Date > unified[1].Date);
    }

    [Fact]
    public async Task Estado_de_sincronizacao_uids_flags_e_remocao()
    {
        var inbox = Folder("INBOX", SpecialFolderKind.Inbox);
        await _cache.SaveFoldersAsync(_account, [inbox], _ct);
        Assert.Null(await _cache.GetSyncStateAsync(inbox.Id, _ct));

        var at = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        await _cache.SetSyncStateAsync(inbox.Id, new FolderSyncState(42, 9_000_000_000, at), _ct);
        await _cache.UpsertMessagesAsync(inbox.Id, [Message(inbox, 1), Message(inbox, 2), Message(inbox, 3)], _ct);
        await _cache.UpdateFlagsAsync(inbox.Id, [new FlagUpdate(2, IsRead: true, IsFlagged: true)], _ct);
        await _cache.DeleteMessagesAsync(inbox.Id, [3], _ct);

        Assert.Equal(new FolderSyncState(42, 9_000_000_000, at), await _cache.GetSyncStateAsync(inbox.Id, _ct));
        Assert.Equal([1u, 2u], (await _cache.GetUidsAsync(inbox.Id, _ct)).Order());
        var flagged = (await _cache.GetMessagesAsync(inbox.Id, _ct)).Single(m => m.Subject == "Assunto 2");
        Assert.True(flagged.IsRead);
        Assert.True(flagged.IsFlagged);

        await _cache.ClearFolderAsync(inbox.Id, _ct);
        Assert.Empty(await _cache.GetUidsAsync(inbox.Id, _ct));
        Assert.Null(await _cache.GetSyncStateAsync(inbox.Id, _ct)); // UIDVALIDITY esquecido
    }

    [Fact]
    public async Task Localizacao_e_marcar_como_lida()
    {
        var inbox = Folder("INBOX", SpecialFolderKind.Inbox);
        await _cache.SaveFoldersAsync(_account, [inbox], _ct);
        var message = Message(inbox, 77);
        await _cache.UpsertMessagesAsync(inbox.Id, [message], _ct);

        await _cache.SetReadAsync(message.Summary.Id, true, _ct);

        Assert.Equal(new MessageLocation(message.Summary.Id, _account, inbox.Id, "INBOX", 77),
            await _cache.GetLocationAsync(message.Summary.Id, _ct));
        Assert.True((await _cache.GetMessagesAsync(inbox.Id, _ct)).Single().IsRead);
        Assert.Null(await _cache.GetLocationAsync(Guid.NewGuid(), _ct));
    }

    [Fact]
    public async Task Corpo_e_guardado_e_apagado_junto_com_a_mensagem()
    {
        var inbox = Folder("INBOX", SpecialFolderKind.Inbox);
        await _cache.SaveFoldersAsync(_account, [inbox], _ct);
        var message = Message(inbox, 1);
        await _cache.UpsertMessagesAsync(inbox.Id, [message], _ct);
        var body = new MessageBody(message.Summary.Id, [new MailAddress("Bia", "bia@exemplo.com")], [], "Olá!",
            [new AttachmentInfo("a.pdf", "application/pdf", 1234)]);

        await _cache.SaveBodyAsync(body, _ct);
        var loaded = await _cache.GetBodyAsync(message.Summary.Id, _ct);

        Assert.NotNull(loaded);
        Assert.Equal("Olá!", loaded.TextBody);
        Assert.Equal(body.To, loaded.To);
        Assert.Equal(body.Attachments, loaded.Attachments);

        await _cache.DeleteMessagesAsync(inbox.Id, [1], _ct);
        Assert.Null(await _cache.GetBodyAsync(message.Summary.Id, _ct));
    }

    [Fact]
    public async Task Fila_guarda_so_a_ultima_intencao_e_conta_falhas()
    {
        var message = Guid.NewGuid();
        await _cache.EnqueueAsync(_account, message, OperationKind.MarkRead, ct: _ct);
        await _cache.EnqueueAsync(_account, message, OperationKind.MarkUnread, ct: _ct);
        await _cache.EnqueueAsync(Guid.NewGuid(), Guid.NewGuid(), OperationKind.MarkRead, ct: _ct); // outra conta

        var op = Assert.Single(await _cache.GetPendingAsync(_account, _ct));
        Assert.Equal(OperationKind.MarkUnread, op.Kind);

        await _cache.RecordFailureAsync(op.Id, _ct);
        Assert.Equal(1, (await _cache.GetPendingAsync(_account, _ct)).Single().Attempts);

        await _cache.CompleteAsync(op.Id, _ct);
        Assert.Empty(await _cache.GetPendingAsync(_account, _ct));
    }

    [Fact]
    public async Task Arquivar_guarda_o_local_no_servidor_mesmo_apos_sair_do_cache()
    {
        var inbox = Folder("INBOX", SpecialFolderKind.Inbox);
        await _cache.SaveFoldersAsync(_account, [inbox], _ct);
        var message = Message(inbox, 42);
        await _cache.UpsertMessagesAsync(inbox.Id, [message], _ct);
        var location = await _cache.GetLocationAsync(message.Summary.Id, _ct);

        await _cache.DeleteMessageAsync(message.Summary.Id, _ct);
        await _cache.EnqueueAsync(_account, message.Summary.Id, OperationKind.Archive, location, ct: _ct);

        Assert.Empty(await _cache.GetMessagesAsync(inbox.Id, _ct));
        var op = Assert.Single(await _cache.GetPendingAsync(_account, _ct));
        Assert.Equal(OperationKind.Archive, op.Kind);
        Assert.Equal(location, op.Location);
    }

    [Fact]
    public async Task Destinatarios_sao_guardados_e_mover_guarda_a_pasta_de_destino()
    {
        var inbox = Folder("INBOX", SpecialFolderKind.Inbox);
        await _cache.SaveFoldersAsync(_account, [inbox], _ct);
        var message = Message(inbox, 9);
        message = message with { Summary = message.Summary with { Recipients = "eu@exemplo.com ana@exemplo.com" } };
        await _cache.UpsertMessagesAsync(inbox.Id, [message], _ct);

        Assert.Equal("eu@exemplo.com ana@exemplo.com", Assert.Single(await _cache.GetMessagesAsync(inbox.Id, _ct)).Recipients);

        var location = await _cache.GetLocationAsync(message.Summary.Id, _ct);
        await _cache.EnqueueAsync(_account, message.Summary.Id, OperationKind.Move, location, "Promoções", ct: _ct);
        var op = Assert.Single(await _cache.GetPendingAsync(_account, _ct));
        Assert.Equal(OperationKind.Move, op.Kind);
        Assert.Equal("Promoções", op.TargetFolder);
    }

    [Fact]
    public async Task Desfazer_cancela_a_remocao_pendente_e_devolve_o_local()
    {
        var inbox = Folder("INBOX", SpecialFolderKind.Inbox);
        await _cache.SaveFoldersAsync(_account, [inbox], _ct);
        var message = Message(inbox, 11);
        await _cache.UpsertMessagesAsync(inbox.Id, [message], _ct);
        var location = await _cache.GetLocationAsync(message.Summary.Id, _ct);
        var due = DateTimeOffset.UtcNow.AddSeconds(15);

        await _cache.DeleteMessageAsync(message.Summary.Id, _ct);
        await _cache.EnqueueAsync(_account, message.Summary.Id, OperationKind.Archive, location, notBefore: due, ct: _ct);
        var queued = Assert.Single(await _cache.GetPendingAsync(_account, _ct));
        Assert.Equal(due.ToUnixTimeMilliseconds(), queued.NotBefore!.Value.ToUnixTimeMilliseconds());

        var cancelled = await _cache.CancelPendingRemovalAsync(message.Summary.Id, _ct);
        Assert.Equal(location, cancelled!.Location);
        Assert.Empty(await _cache.GetPendingAsync(_account, _ct));
        Assert.Null(await _cache.CancelPendingRemovalAsync(message.Summary.Id, _ct)); // nada mais a desfazer
    }

    [Fact]
    public async Task Busca_de_texto_completo_ignora_acentos_usa_prefixo_e_operadores()
    {
        var inbox = Folder("INBOX", SpecialFolderKind.Inbox);
        var archive = Folder("Arquivo", SpecialFolderKind.Archive);
        await _cache.SaveFoldersAsync(_account, [inbox, archive], _ct);
        CachedMessage Msg(MailFolder folder, uint uid, string subject, string fromName, string from, bool attachments = false) =>
            Message(folder, uid) with
            {
                Summary = Message(folder, uid).Summary with
                {
                    Subject = subject, FromName = fromName, FromAddress = from, HasAttachments = attachments,
                    Recipients = "eu@exemplo.com",
                },
            };
        await _cache.UpsertMessagesAsync(inbox.Id,
        [
            Msg(inbox, 1, "Orçamento de setembro", "Ana Souza", "ana@empresa.com", attachments: true),
            Msg(inbox, 2, "Reunião de quinta", "Bruno", "bruno@empresa.com"),
        ], _ct);
        await _cache.UpsertMessagesAsync(archive.Id, [Msg(archive, 3, "Orçamentos antigos", "Carla", "carla@outra.com")], _ct);

        async Task<string[]> Find(string text, Guid? folder = null) =>
            (await _cache.SearchMessagesAsync(new MessageSearch(SearchQuery.Parse(text), FolderId: folder), 50, _ct))
            .Select(m => m.Subject).Order().ToArray();

        Assert.Equal(["Orçamento de setembro", "Orçamentos antigos"], await Find("orcamento"));   // sem acento e com prefixo
        Assert.Equal(["Orçamento de setembro"], await Find("orçam de:ana"));
        Assert.Equal(["Orçamento de setembro"], await Find("tem:anexo"));                    // só filtro, sem texto
        Assert.Equal(["Orçamentos antigos"], await Find("orcamento", archive.Id));
        Assert.Equal(["Reunião de quinta"], await Find("assunto:reuniao para:eu@exemplo.com"));
        Assert.Empty(await Find("\"aspas\" soltas\""));                                     // nunca é erro de sintaxe

        // Corpo entra no índice ao ser guardado; mensagem apagada sai do índice.
        var bruno = (await _cache.GetMessagesAsync(inbox.Id, _ct)).Single(m => m.FromName == "Bruno");
        await _cache.SaveBodyAsync(new MessageBody(bruno.Id, [], [], "Vamos falar do cronograma", []), _ct);
        Assert.Equal(["Reunião de quinta"], await Find("cronograma"));
        await _cache.DeleteMessageAsync(bruno.Id, _ct);
        Assert.Empty(await Find("cronograma"));
    }

    [Fact]
    public async Task Sugestao_de_endereco_usa_o_nome_mais_recente_e_ordena_por_frequencia()
    {
        var inbox = Folder("INBOX", SpecialFolderKind.Inbox);
        await _cache.SaveFoldersAsync(_account, [inbox], _ct);
        CachedMessage From(uint uid, string name, string address) => Message(inbox, uid) with
        {
            Summary = Message(inbox, uid).Summary with { FromName = name, FromAddress = address },
        };
        await _cache.UpsertMessagesAsync(inbox.Id,
        [
            From(1, "Ana antiga", "ana@exemplo.com"),
            From(5, "Ana Souza", "ANA@exemplo.com"),
            From(3, "Ana", "ana@exemplo.com"),
            From(2, "Anabela", "anabela@exemplo.com"),
        ], _ct);

        var found = await _cache.SearchAddressesAsync("ana", 8, _ct);

        Assert.Equal(2, found.Count);
        Assert.Equal("Ana Souza", found[0].Name); // 3 usos; nome da mensagem mais recente (uid 5)
        Assert.Equal("anabela@exemplo.com", found[1].Address);
    }

    [Fact]
    public async Task Sinalizar_muda_o_cache_e_fila_guarda_a_ultima_intencao()
    {
        var inbox = Folder("INBOX", SpecialFolderKind.Inbox);
        await _cache.SaveFoldersAsync(_account, [inbox], _ct);
        var message = Message(inbox, 7);
        await _cache.UpsertMessagesAsync(inbox.Id, [message], _ct);

        await _cache.SetFlaggedAsync(message.Summary.Id, true, _ct);
        await _cache.EnqueueAsync(_account, message.Summary.Id, OperationKind.Flag, ct: _ct);
        await _cache.EnqueueAsync(_account, message.Summary.Id, OperationKind.Unflag, ct: _ct);
        await _cache.EnqueueAsync(_account, message.Summary.Id, OperationKind.MarkRead, ct: _ct); // tipo diferente: fica

        Assert.True((await _cache.GetMessagesAsync(inbox.Id, _ct)).Single().IsFlagged);
        var ops = await _cache.GetPendingAsync(_account, _ct);
        Assert.Equal([OperationKind.Unflag, OperationKind.MarkRead], ops.Select(o => o.Kind));
        Assert.All(ops, o => Assert.Null(o.Location));
    }

    [Fact]
    public async Task Apagar_conta_remove_pastas_mensagens_e_fila()
    {
        var inbox = Folder("INBOX", SpecialFolderKind.Inbox);
        await _cache.SaveFoldersAsync(_account, [inbox], _ct);
        await _cache.UpsertMessagesAsync(inbox.Id, [Message(inbox, 1)], _ct);
        await _cache.EnqueueAsync(_account, Guid.NewGuid(), OperationKind.MarkRead, ct: _ct);

        await _cache.DeleteAccountAsync(_account, _ct);

        Assert.Empty(await _cache.GetFoldersAsync(_account, _ct));
        Assert.Empty(await _cache.GetInboxMessagesAsync(_ct));
        Assert.Empty(await _cache.GetPendingAsync(_account, _ct));
    }
}
