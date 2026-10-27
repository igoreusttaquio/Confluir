using CommunityToolkit.Mvvm.ComponentModel;
using Confluir.App.ViewModels;

namespace Confluir.App.Services;

/// <summary>Página que precisa saber quando está na frente (ex.: para mostrar um WebView nativo só nesse momento).</summary>
public interface INavigationPage
{
    bool IsActive { get; set; }
}

/// <summary>
/// Navegação dentro da janela principal. A raiz (página nula) é a caixa de email; outras telas
/// (adicionar conta, mensagem em tela própria…) ocupam a área de conteúdo, com a barra lateral sempre visível,
/// e voltam com <see cref="GoBack"/>. O app prefere navegar a abrir janelas ou diálogos modais.
/// </summary>
public sealed partial class NavigationService : ObservableObject
{
    private readonly Stack<ViewModelBase> _backStack = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAtRoot))]
    public partial ViewModelBase? CurrentPage { get; private set; }

    /// <summary>
    /// Página exibida no host. Ao voltar para a raiz ela é mantida até a próxima navegação,
    /// para que a animação de saída mostre o conteúdo em vez de uma área vazia.
    /// </summary>
    [ObservableProperty]
    public partial ViewModelBase? DisplayedPage { get; private set; }

    public bool IsAtRoot => CurrentPage is null;

    partial void OnCurrentPageChanged(ViewModelBase? oldValue, ViewModelBase? newValue)
    {
        if (oldValue is INavigationPage oldPage)
            oldPage.IsActive = false;
        if (newValue is INavigationPage newPage)
            newPage.IsActive = true;
    }

    public void NavigateTo(ViewModelBase page)
    {
        if (CurrentPage is not null)
            _backStack.Push(CurrentPage);
        CurrentPage = page;
        DisplayedPage = page;
    }

    public void GoBack()
    {
        CurrentPage = _backStack.Count > 0 ? _backStack.Pop() : null;
        if (CurrentPage is not null)
            DisplayedPage = CurrentPage;
    }

    public void GoToRoot()
    {
        _backStack.Clear();
        CurrentPage = null;
    }
}
