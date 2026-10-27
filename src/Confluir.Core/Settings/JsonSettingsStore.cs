using System.Text.Json;
using System.Text.Json.Serialization;

namespace Confluir.Core.Settings;

/// <summary>Configurações em um arquivo JSON. Arquivo ausente ou corrompido resulta nos valores padrão.</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public JsonSettingsStore(string path)
    {
        _path = path;
        Current = Load(path);
    }

    public AppSettings Current { get; }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Current, Options));
        File.Move(temp, _path, overwrite: true);
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
        }
        catch (JsonException)
        {
        }
        return new AppSettings();
    }
}
