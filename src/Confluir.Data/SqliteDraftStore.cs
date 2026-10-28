using System.Text.Json;
using System.Text.Json.Serialization;
using Confluir.Core.Compose;
using Confluir.Core.Services;
using Dapper;

namespace Confluir.Data;

/// <summary>Rascunhos na tabela <c>drafts</c> (um JSON por rascunho).</summary>
public sealed class SqliteDraftStore(Database database) : IDraftStore
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public async Task<Draft?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await ThreadPoolSwitch.Instance;
        await using var connection = await database.OpenAsync(ct);
        var data = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition("SELECT data FROM drafts WHERE id = @id", new { id = id.ToString() }, cancellationToken: ct));
        return data is null ? null : JsonSerializer.Deserialize<Draft>(data, Json);
    }

    public async Task<IReadOnlyList<Draft>> ListAsync(CancellationToken ct = default)
    {
        await ThreadPoolSwitch.Instance;
        await using var connection = await database.OpenAsync(ct);
        var rows = await connection.QueryAsync<string>(
            new CommandDefinition("SELECT data FROM drafts ORDER BY updated_at DESC", cancellationToken: ct));
        return rows.Select(r => JsonSerializer.Deserialize<Draft>(r, Json)!).ToList();
    }

    public async Task SaveAsync(Draft draft, CancellationToken ct = default)
    {
        await ThreadPoolSwitch.Instance;
        await using var connection = await database.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO drafts (id, account_id, updated_at, data) VALUES (@id, @account, @updated, @data)
            ON CONFLICT (id) DO UPDATE SET account_id = excluded.account_id, updated_at = excluded.updated_at, data = excluded.data
            """,
            new
            {
                id = draft.Id.ToString(),
                account = draft.AccountId.ToString(),
                updated = draft.UpdatedAt.UtcDateTime.ToString("O"),
                data = JsonSerializer.Serialize(draft, Json),
            },
            cancellationToken: ct));
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await ThreadPoolSwitch.Instance;
        await using var connection = await database.OpenAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition("DELETE FROM drafts WHERE id = @id", new { id = id.ToString() }, cancellationToken: ct));
    }
}
