using CommunityToolkit.Mvvm.ComponentModel;

namespace Confluir.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>
    /// Chamado quando o idioma muda. O padrão renotifica todas as propriedades,
    /// o que basta para textos calculados a partir de <see cref="Resources.Strings"/>.
    /// </summary>
    public virtual void OnLanguageChanged() => OnPropertyChanged(string.Empty);
}
