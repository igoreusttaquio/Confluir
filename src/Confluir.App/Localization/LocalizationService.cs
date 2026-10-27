using System.ComponentModel;
using System.Globalization;
using Confluir.App.Resources;
using Confluir.Core.Localization;

namespace Confluir.App.Localization;

/// <summary>
/// Idioma da interface, trocado em tempo de execução sem reiniciar.
/// O XAML lê os textos pelo indexador (via <see cref="TrExtension"/>), que é renotificado a cada troca;
/// o código C# usa <see cref="Strings"/>, cuja cultura também é atualizada aqui.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    // Capturada antes de qualquer Apply (que altera CultureInfo.CurrentUICulture).
    // Declarada antes de Instance: inicializadores estáticos rodam na ordem do texto.
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;

    /// <summary>Instância única, acessível ao XAML. A mesma é registrada na injeção de dependência.</summary>
    public static LocalizationService Instance { get; } = new();

    private LocalizationService()
    {
        Apply(null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Disparado depois que o idioma muda, para ViewModels recalcularem textos derivados.</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>Código efetivo ("pt-BR", "en", "es").</summary>
    public string Language { get; private set; } = "pt-BR";

    /// <summary>Preferência salva; nulo segue o sistema.</summary>
    public string? Setting { get; private set; }

    public CultureInfo UiCulture { get; private set; } = CultureInfo.InvariantCulture;

    /// <summary>Cultura para datas e números.</summary>
    public CultureInfo FormatCulture { get; private set; } = CultureInfo.InvariantCulture;

    /// <summary>Muda a cada troca de idioma; usado por bindings que traduzem chaves dinâmicas.</summary>
    public int Version { get; private set; }

    public string this[string key] => Strings.ResourceManager.GetString(key, UiCulture) ?? $"#{key}";

    /// <summary>Formata a chave com argumentos na cultura atual.</summary>
    public string Format(string key, params object?[] args) => string.Format(FormatCulture, this[key], args);

    /// <summary>Escolhe a forma singular (<c>chave_One</c>) ou plural (<c>chave_Other</c>).</summary>
    public string Plural(string key, int count) => Format(count == 1 ? key + "_One" : key + "_Other", count);

    /// <param name="setting">"pt-BR", "en", "es" ou nulo para seguir o sistema.</param>
    public void Apply(string? setting)
    {
        var resolved = LanguageResolver.Resolve(setting, SystemCulture);
        Setting = string.IsNullOrWhiteSpace(setting) ? null : resolved.Code;
        var changed = resolved.Code != Language || !Equals(resolved.FormatCulture, FormatCulture);

        Language = resolved.Code;
        UiCulture = resolved.UiCulture;
        FormatCulture = resolved.FormatCulture;
        Strings.Culture = UiCulture;
        CultureInfo.CurrentUICulture = UiCulture;
        CultureInfo.CurrentCulture = FormatCulture;
        CultureInfo.DefaultThreadCurrentUICulture = UiCulture;
        CultureInfo.DefaultThreadCurrentCulture = FormatCulture;

        if (!changed)
            return;
        Version++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}
