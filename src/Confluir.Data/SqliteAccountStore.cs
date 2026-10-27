using System.Text.Json;
using System.Text.Json.Serialization;
using Confluir.Core.Models;
using Confluir.Core.Services;
using Dapper;

namespace Confluir.Data;

/// <summary>
/// Contas no SQLite (sem segredos). Na primeira execução importa o <c>accounts.json</c> da fase 2
/// e o renomeia para <c>accounts.json.migrado</c>.
/// </summary>
public sealed class SqliteAccountStore : IAccountStore
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly Database _db;
    private readonly Lock _lock = new();
    private List<MailAccount> _accounts;

    /// <param name="legacyJsonPath">accounts.json da versão anterior, importado se o banco ainda não tiver contas.</param>
    public SqliteAccountStore(Database db, string? legacyJsonPath = null)
    {
        _db = db;
        // Leitura síncrona na construção: as contas são necessárias antes de qualquer tela.
        _accounts = Task.Run(() => LoadAsync(legacyJsonPath)).GetAwaiter().GetResult();
    }

    public IReadOnlyList<MailAccount> Accounts
    {
        get { lock (_lock) return _accounts.ToList(); }
    }

    public event EventHandler? Changed;

    public void Add(MailAccount account)
    {
        List<MailAccount> snapshot;
        lock (_lock)
        {
            var index = _accounts.FindIndex(a => a.Id == account.Id);
            if (index >= 0)
                _accounts[index] = account;
            else
                _accounts.Add(account);
            snapshot = _accounts.ToList();
        }
        Task.Run(() => SaveAsync(snapshot)).GetAwaiter().GetResult();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(Guid accountId)
    {
        List<MailAccount> snapshot;
        lock (_lock)
        {
            _accounts.RemoveAll(a => a.Id == accountId);
            snapshot = _accounts.ToList();
        }
        Task.Run(() => SaveAsync(snapshot)).GetAwaiter().GetResult();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<List<MailAccount>> LoadAsync(string? legacyJsonPath)
    {
        await using var c = await _db.OpenAsync();
        var rows = (await c.QueryAsync<string>("SELECT data FROM accounts ORDER BY position")).ToList();
        if (rows.Count > 0)
            return rows.Select(r => JsonSerializer.Deserialize<MailAccount>(r, Json)!).ToList();

        if (legacyJsonPath is null || !File.Exists(legacyJsonPath))
            return [];

        var legacy = JsonSerializer.Deserialize<List<MailAccount>>(await File.ReadAllTextAsync(legacyJsonPath), Json) ?? [];
        await SaveAsync(legacy);
        File.Move(legacyJsonPath, legacyJsonPath + ".migrado", overwrite: true);
        return legacy;
    }

    private async Task SaveAsync(IReadOnlyList<MailAccount> accounts)
    {
        await using var c = await _db.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("DELETE FROM accounts", transaction: tx);
        for (var i = 0; i < accounts.Count; i++)
        {
            await c.ExecuteAsync("INSERT INTO accounts (id, position, data) VALUES (@id, @position, @data)",
                new { id = accounts[i].Id.ToString(), position = i, data = JsonSerializer.Serialize(accounts[i], Json) }, tx);
        }
        await tx.CommitAsync();
    }
}
