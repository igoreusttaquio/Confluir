namespace Confluir.Core;

/// <summary>Pastas de dados do app (<c>&lt;AppData&gt;/Confluir</c> por padrão).</summary>
public sealed class AppPaths
{
    public AppPaths(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Confluir");
    }

    public string DataDirectory { get; }
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public string FontsDirectory => Path.Combine(DataDirectory, "fonts");
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    /// <summary>Contas da fase 2; importado para o banco na primeira execução da fase 3.</summary>
    public string AccountsFile => Path.Combine(DataDirectory, "accounts.json");
    public string DatabaseFile => Path.Combine(DataDirectory, "confluir.db");
}
