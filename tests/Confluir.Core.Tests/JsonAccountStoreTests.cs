using Confluir.Core.Models;
using Confluir.Core.Providers;
using Confluir.Core.Services;

namespace Confluir.Core.Tests;

public sealed class JsonAccountStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "confluir-tests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "accounts.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static MailAccount Account(string name = "Pessoal") => new(
        Guid.NewGuid(), name, "ana@gmail.com", MailProvider.Gmail, AccountColors.Teal.Hex,
        "ana@gmail.com", KnownProviders.Gmail);

    [Fact]
    public void Adiciona_persiste_e_notifica()
    {
        var store = new JsonAccountStore(FilePath);
        var changes = 0;
        store.Changed += (_, _) => changes++;

        var account = Account();
        store.Add(account);

        Assert.Equal(1, changes);
        var reloaded = new JsonAccountStore(FilePath);
        Assert.Equal(account, Assert.Single(reloaded.Accounts));
    }

    [Fact]
    public void Adicionar_com_mesmo_id_substitui()
    {
        var store = new JsonAccountStore(FilePath);
        var account = Account();
        store.Add(account);
        store.Add(account with { DisplayName = "Renomeada" });

        Assert.Equal("Renomeada", Assert.Single(store.Accounts).DisplayName);
    }

    [Fact]
    public void Remove_conta()
    {
        var store = new JsonAccountStore(FilePath);
        var a = Account("A");
        var b = Account("B");
        store.Add(a);
        store.Add(b);

        store.Remove(a.Id);

        Assert.Equal(b.Id, Assert.Single(new JsonAccountStore(FilePath).Accounts).Id);
    }

    [Fact]
    public void Arquivo_nao_guarda_senha()
    {
        var store = new JsonAccountStore(FilePath);
        store.Add(Account());

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(FilePath));
        var names = PropertyNames(doc.RootElement).ToList();
        Assert.NotEmpty(names);
        Assert.DoesNotContain(names, n =>
            n.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> PropertyNames(System.Text.Json.JsonElement e) => e.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Object => e.EnumerateObject().SelectMany(p => PropertyNames(p.Value).Prepend(p.Name)),
        System.Text.Json.JsonValueKind.Array => e.EnumerateArray().SelectMany(PropertyNames),
        _ => [],
    };

    [Fact]
    public void Arquivo_corrompido_e_preservado_para_recuperacao()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "[{ quebrado");

        var store = new JsonAccountStore(FilePath);

        Assert.Empty(store.Accounts);
        Assert.Equal("[{ quebrado", File.ReadAllText(FilePath + ".corrompido"));
    }
}
