using Confluir.Core.Models;
using Confluir.Core.Providers;
using Confluir.Core.Services;
using Microsoft.Data.Sqlite;

namespace Confluir.Data.Tests;

public sealed class SqliteAccountStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "confluir-tests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "confluir.db");
    private string JsonPath => Path.Combine(_dir, "accounts.json");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static MailAccount Account(string name) => new(
        Guid.NewGuid(), name, $"{name.ToLowerInvariant()}@gmail.com", MailProvider.Gmail, AccountColors.Coral.Hex,
        $"{name.ToLowerInvariant()}@gmail.com", KnownProviders.Gmail);

    [Fact]
    public void Adiciona_atualiza_remove_e_persiste_na_ordem()
    {
        var store = new SqliteAccountStore(new Database(DbPath));
        var a = Account("A");
        var b = Account("B");
        store.Add(a);
        store.Add(b);
        store.Add(a with { DisplayName = "A2" });

        var reloaded = new SqliteAccountStore(new Database(DbPath));
        Assert.Equal(["A2", "B"], reloaded.Accounts.Select(x => x.DisplayName));

        reloaded.Remove(a.Id);
        Assert.Equal(b.Id, Assert.Single(new SqliteAccountStore(new Database(DbPath)).Accounts).Id);
    }

    [Fact]
    public void Importa_accounts_json_da_versao_anterior_uma_unica_vez()
    {
        Directory.CreateDirectory(_dir);
        var legacy = new JsonAccountStore(JsonPath);
        var account = Account("Pessoal");
        legacy.Add(account);

        var store = new SqliteAccountStore(new Database(DbPath), JsonPath);

        Assert.Equal(account, Assert.Single(store.Accounts));
        Assert.False(File.Exists(JsonPath));
        Assert.True(File.Exists(JsonPath + ".migrado"));

        // Segunda abertura: lê do banco, sem reimportar.
        Assert.Single(new SqliteAccountStore(new Database(DbPath), JsonPath).Accounts);
    }

    [Fact]
    public void Notifica_mudancas()
    {
        var store = new SqliteAccountStore(new Database(DbPath));
        var changes = 0;
        store.Changed += (_, _) => changes++;

        var a = Account("A");
        store.Add(a);
        store.Remove(a.Id);

        Assert.Equal(2, changes);
    }
}
