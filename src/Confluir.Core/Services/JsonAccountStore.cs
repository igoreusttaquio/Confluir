using System.Text.Json;
using System.Text.Json.Serialization;
using Confluir.Core.Models;

namespace Confluir.Core.Services;

/// <summary>Contas em <c>accounts.json</c>. Só metadados e servidores; segredos ficam no cofre do sistema.</summary>
public sealed class JsonAccountStore : IAccountStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Lock _lock = new();
    private List<MailAccount> _accounts;

    public JsonAccountStore(string path)
    {
        _path = path;
        _accounts = Load(path);
    }

    public IReadOnlyList<MailAccount> Accounts
    {
        get { lock (_lock) return _accounts.ToList(); }
    }

    public event EventHandler? Changed;

    public void Add(MailAccount account)
    {
        lock (_lock)
        {
            _accounts = [.. _accounts.Where(a => a.Id != account.Id), account];
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(Guid accountId)
    {
        lock (_lock)
        {
            _accounts = _accounts.Where(a => a.Id != accountId).ToList();
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_accounts, Options));
        File.Move(temp, _path, overwrite: true);
    }

    private static List<MailAccount> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<List<MailAccount>>(File.ReadAllText(path), Options) ?? [];
        }
        catch (JsonException)
        {
            // Não descartar contas em silêncio: preserva o arquivo ilegível para recuperação manual.
            File.Copy(path, path + ".corrompido", overwrite: true);
        }
        return [];
    }
}
