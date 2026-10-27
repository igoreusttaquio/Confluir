using Confluir.Core.Settings;

namespace Confluir.Core.Tests;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "confluir-tests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Sem_arquivo_usa_padroes()
    {
        var store = new JsonSettingsStore(FilePath);
        Assert.Equal(ThemeMode.System, store.Current.Appearance.Theme);
        Assert.Null(store.Current.Appearance.AccentColor);
    }

    [Fact]
    public void Salva_e_recarrega()
    {
        var store = new JsonSettingsStore(FilePath);
        store.Current.Appearance.Theme = ThemeMode.Dark;
        store.Current.Appearance.AccentColor = "#8B5CF6";
        store.Current.Layout.SidebarWidth = 312;
        store.Save();

        var reloaded = new JsonSettingsStore(FilePath);
        Assert.Equal(ThemeMode.Dark, reloaded.Current.Appearance.Theme);
        Assert.Equal("#8B5CF6", reloaded.Current.Appearance.AccentColor);
        Assert.Equal(312, reloaded.Current.Layout.SidebarWidth);
        Assert.Contains("\"Dark\"", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Arquivo_corrompido_volta_aos_padroes()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ isto não é json");

        var store = new JsonSettingsStore(FilePath);
        Assert.Equal(ThemeMode.System, store.Current.Appearance.Theme);
    }
}
