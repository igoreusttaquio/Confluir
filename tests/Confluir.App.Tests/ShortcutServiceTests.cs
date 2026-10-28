using Avalonia.Input;
using Confluir.App.Services;
using Confluir.App.ViewModels;
using Confluir.Core.Settings;

namespace Confluir.App.Tests;

public sealed class ShortcutServiceTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"confluir-shortcuts-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_file);

    private static KeyModifiers Mod => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    [Fact]
    public void Todas_as_acoes_tem_padrao_e_nenhum_se_repete()
    {
        Assert.All(ShortcutInfo.All, s => Assert.True(ShortcutService.Defaults.ContainsKey(s.Action)));
        Assert.Equal(ShortcutService.Defaults.Count, ShortcutService.Defaults.Values.Distinct().Count());
    }

    [Fact]
    public void Trocar_salva_nas_configuracoes_e_voltar_ao_padrao_apaga()
    {
        var service = new ShortcutService(new JsonSettingsStore(_file));
        var gesture = new KeyGesture(Key.A, Mod | KeyModifiers.Shift);

        service.Set(ShortcutAction.Archive, gesture);
        var reloaded = new ShortcutService(new JsonSettingsStore(_file));
        Assert.Equal(gesture, reloaded.GestureFor(ShortcutAction.Archive));
        Assert.True(reloaded.IsCustom(ShortcutAction.Archive));

        service.Reset(ShortcutAction.Archive);
        Assert.Equal(ShortcutService.Defaults[ShortcutAction.Archive], service.GestureFor(ShortcutAction.Archive));
        Assert.Empty(new JsonSettingsStore(_file).Current.Shortcuts);
    }

    [Fact]
    public void Virgula_e_teclas_especiais_sobrevivem_ao_arquivo()
    {
        var service = new ShortcutService(new JsonSettingsStore(_file));
        service.Set(ShortcutAction.Sync, new KeyGesture(Key.OemComma, Mod | KeyModifiers.Alt));
        service.Set(ShortcutAction.Search, new KeyGesture(Key.F3));

        var reloaded = new ShortcutService(new JsonSettingsStore(_file));
        Assert.Equal(new KeyGesture(Key.OemComma, Mod | KeyModifiers.Alt), reloaded.GestureFor(ShortcutAction.Sync));
        Assert.Equal(new KeyGesture(Key.F3), reloaded.GestureFor(ShortcutAction.Search));
    }

    [Fact]
    public void Conflito_aponta_a_outra_acao_e_trocar_inverte_as_duas()
    {
        var service = new ShortcutService(new JsonSettingsStore(_file));
        var reply = service.GestureFor(ShortcutAction.Reply);
        var archive = service.GestureFor(ShortcutAction.Archive);

        Assert.Equal(ShortcutAction.Reply, service.ConflictWith(reply, ShortcutAction.Archive));
        Assert.Null(service.ConflictWith(archive, ShortcutAction.Archive));

        service.Swap(ShortcutAction.Archive, ShortcutAction.Reply);
        Assert.Equal(reply, service.GestureFor(ShortcutAction.Archive));
        Assert.Equal(archive, service.GestureFor(ShortcutAction.Reply));
    }

    [Fact]
    public void Valor_invalido_no_arquivo_vale_o_padrao()
    {
        var store = new JsonSettingsStore(_file);
        store.Current.Shortcuts["Archive"] = "Ctrl+NaoExiste";
        var service = new ShortcutService(store);
        Assert.Equal(ShortcutService.Defaults[ShortcutAction.Archive], service.GestureFor(ShortcutAction.Archive));
    }

    [Theory]
    [InlineData(Key.A, KeyModifiers.None, false)]
    [InlineData(Key.A, KeyModifiers.Shift, false)]
    [InlineData(Key.A, KeyModifiers.Control, true)]
    [InlineData(Key.A, KeyModifiers.Alt, true)]
    [InlineData(Key.F7, KeyModifiers.None, true)]
    [InlineData(Key.Delete, KeyModifiers.None, true)]
    [InlineData(Key.Escape, KeyModifiers.Control, false)]
    public void So_aceita_combinacoes_que_nao_atrapalham_a_digitacao(Key key, KeyModifiers modifiers, bool allowed) =>
        Assert.Equal(allowed, ShortcutService.IsAllowed(new KeyGesture(key, modifiers)));
}
