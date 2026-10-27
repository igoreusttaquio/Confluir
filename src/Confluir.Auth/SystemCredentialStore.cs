using System.Text;
using System.Text.Json;
using Confluir.Core.Services;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Confluir.Auth;

/// <summary>
/// Cofre de credenciais do sistema, via <c>Microsoft.Identity.Client.Extensions.Msal</c>:
/// Windows → arquivo cifrado com DPAPI (usuário atual); macOS → Keychain; Linux → libsecret (Secret Service).
/// Todos os segredos ficam num único item, como um dicionário JSON chave → segredo.
/// Se o cofre não estiver disponível (ex.: Linux sem keyring), falha em vez de gravar em texto puro.
/// </summary>
public sealed class SystemCredentialStore : ICredentialStore
{
    private const string ServiceName = "Confluir";

    private readonly Lazy<Storage> _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SystemCredentialStore(string dataDirectory)
    {
        _storage = new Lazy<Storage>(() => CreateStorage(dataDirectory));
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return Read().GetValueOrDefault(key);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string key, string secret, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var all = Read();
            all[key] = secret;
            Write(all);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var all = Read();
            if (all.Remove(key))
                Write(all);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Dictionary<string, string> Read()
    {
        var data = _storage.Value.ReadData();
        if (data.Length == 0)
            return [];
        return JsonSerializer.Deserialize<Dictionary<string, string>>(data) ?? [];
    }

    private void Write(Dictionary<string, string> all) =>
        _storage.Value.WriteData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(all)));

    private static Storage CreateStorage(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var properties = new StorageCreationPropertiesBuilder("credentials.bin", dataDirectory)
            .WithMacKeyChain(ServiceName, "credentials")
            .WithLinuxKeyring(
                schemaName: "com.confluir.credentials",
                collection: "default",
                secretLabel: "Credenciais do Confluir",
                attribute1: new KeyValuePair<string, string>("Product", ServiceName),
                attribute2: new KeyValuePair<string, string>("Purpose", "credentials"))
            .Build();

        var storage = Storage.Create(properties);
        // Lança se o cofre não puder ser usado (ex.: sem Secret Service no Linux).
        storage.VerifyPersistence();
        return storage;
    }
}
