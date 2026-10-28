using Dapper;
using Microsoft.Data.Sqlite;

namespace Confluir.Data;

/// <summary>
/// Banco SQLite local (<c>confluir.db</c>). Cria/migra o esquema na primeira abertura,
/// usando <c>PRAGMA user_version</c> como número da versão.
/// </summary>
public sealed class Database
{
    private static readonly string[] Migrations =
    [
        // v1: contas, pastas, mensagens, corpos e fila de operações
        """
        CREATE TABLE accounts (
            id        TEXT PRIMARY KEY,
            position  INTEGER NOT NULL,
            data      TEXT NOT NULL
        );

        CREATE TABLE folders (
            id              TEXT PRIMARY KEY,
            account_id      TEXT NOT NULL,
            full_name       TEXT NOT NULL,
            name            TEXT NOT NULL,
            kind            INTEGER NOT NULL,
            position        INTEGER NOT NULL,
            uid_validity    INTEGER,
            highest_modseq  INTEGER NOT NULL DEFAULT 0,
            last_synced_at  TEXT,
            UNIQUE (account_id, full_name)
        );

        CREATE TABLE messages (
            id               TEXT PRIMARY KEY,
            account_id       TEXT NOT NULL,
            folder_id        TEXT NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
            uid              INTEGER NOT NULL,
            from_name        TEXT NOT NULL,
            from_address     TEXT NOT NULL,
            subject          TEXT NOT NULL,
            preview          TEXT NOT NULL,
            date_utc_ms      INTEGER NOT NULL,
            date_offset_min  INTEGER NOT NULL,
            is_read          INTEGER NOT NULL,
            is_flagged       INTEGER NOT NULL,
            has_attachments  INTEGER NOT NULL,
            UNIQUE (folder_id, uid)
        );
        CREATE INDEX ix_messages_folder_date ON messages (folder_id, date_utc_ms DESC);
        CREATE INDEX ix_messages_unread ON messages (folder_id) WHERE is_read = 0;

        CREATE TABLE bodies (
            message_id  TEXT PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
            data        TEXT NOT NULL,
            fetched_at  TEXT NOT NULL
        );

        CREATE TABLE pending_ops (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id  TEXT NOT NULL,
            message_id  TEXT NOT NULL,
            kind        INTEGER NOT NULL,
            attempts    INTEGER NOT NULL DEFAULT 0,
            created_at  TEXT NOT NULL
        );
        CREATE INDEX ix_pending_account ON pending_ops (account_id, id);
        """,

        // v2: mensagens sem data válida gravadas como ano 1 (antes da correção) passam a 1/1/1970,
        // que é o valor usado agora quando não há data utilizável.
        """
        UPDATE messages SET date_utc_ms = 0, date_offset_min = 0 WHERE date_utc_ms < 0;
        """,

        // v3: corpos guardados antes da fase 4 não têm o HTML; são baixados de novo ao abrir.
        """
        DELETE FROM bodies;
        """,

        // v4: operações que tiram a mensagem do cache (arquivar/excluir) guardam onde ela estava no servidor.
        """
        ALTER TABLE pending_ops ADD COLUMN folder_name TEXT;
        ALTER TABLE pending_ops ADD COLUMN folder_id TEXT;
        ALTER TABLE pending_ops ADD COLUMN uid INTEGER;
        """,

        // v5: corpos passam a guardar Message-Id/References/Reply-To (para responder na mesma conversa).
        """
        DELETE FROM bodies;
        CREATE INDEX IF NOT EXISTS ix_messages_from ON messages (from_address);
        """,

        // v6: rascunhos salvos automaticamente durante a composição.
        """
        CREATE TABLE IF NOT EXISTS drafts (
            id          TEXT PRIMARY KEY,
            account_id  TEXT NOT NULL,
            updated_at  TEXT NOT NULL,
            data        TEXT NOT NULL
        );
        """,

        // v7: destinatários no cache (regras por "Para") e pasta de destino na fila (mover por regra).
        """
        ALTER TABLE messages ADD COLUMN recipients TEXT NOT NULL DEFAULT '';
        ALTER TABLE pending_ops ADD COLUMN target_folder TEXT;
        """,

        // v8: busca de texto completo (FTS5). Sem acentos ("orcamento" acha "orçamento"); o rowid é o da
        // tabela messages, mantido por gatilhos. O corpo entra quando a mensagem é aberta (SaveBodyAsync).
        """
        CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
            subject, sender, recipients, preview, body,
            tokenize = 'unicode61 remove_diacritics 2'
        );
        DELETE FROM messages_fts;
        INSERT INTO messages_fts (rowid, subject, sender, recipients, preview, body)
            SELECT m.rowid, m.subject, m.from_name || ' ' || m.from_address, m.recipients, m.preview,
                   COALESCE(substr(json_extract(b.data, '$.TextBody'), 1, 20000), '')
            FROM messages m LEFT JOIN bodies b ON b.message_id = m.id;
        CREATE TRIGGER IF NOT EXISTS messages_fts_insert AFTER INSERT ON messages BEGIN
            INSERT INTO messages_fts (rowid, subject, sender, recipients, preview, body)
            VALUES (new.rowid, new.subject, new.from_name || ' ' || new.from_address, new.recipients, new.preview, '');
        END;
        CREATE TRIGGER IF NOT EXISTS messages_fts_delete AFTER DELETE ON messages BEGIN
            DELETE FROM messages_fts WHERE rowid = old.rowid;
        END;
        CREATE TRIGGER IF NOT EXISTS messages_fts_update AFTER UPDATE OF subject, from_name, from_address, recipients, preview ON messages BEGIN
            UPDATE messages_fts SET subject = new.subject, sender = new.from_name || ' ' || new.from_address,
                   recipients = new.recipients, preview = new.preview
            WHERE rowid = old.rowid;
        END;
        """,

        // v9: arquivar/excluir esperam alguns segundos na fila (o "Desfazer" cancela antes de ir ao servidor).
        """
        ALTER TABLE pending_ops ADD COLUMN not_before_ms INTEGER;
        """,
    ];

    private readonly string _connectionString;
    private readonly Lazy<Task> _initialized;

    public Database(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();
        _initialized = new Lazy<Task>(MigrateAsync);
    }

    public int SchemaVersion => Migrations.Length;

    /// <summary>Abre uma conexão pronta para uso (esquema migrado, chaves estrangeiras ativas).</summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        await _initialized.Value;
        return await OpenRawAsync(ct);
    }

    private async Task<SqliteConnection> OpenRawAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync("PRAGMA busy_timeout = 5000;");
        return connection;
    }

    private async Task MigrateAsync()
    {
        await using var connection = await OpenRawAsync();
        // WAL: leituras não bloqueiam a sincronização que escreve em segundo plano.
        await connection.ExecuteAsync("PRAGMA journal_mode = WAL;");

        var version = await connection.ExecuteScalarAsync<int>("PRAGMA user_version;");
        for (var v = version; v < Migrations.Length; v++)
        {
            await using var tx = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(Migrations[v], transaction: tx);
            await connection.ExecuteAsync($"PRAGMA user_version = {v + 1};", transaction: tx);
            await tx.CommitAsync();
        }
    }
}
