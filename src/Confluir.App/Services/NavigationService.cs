using CommunityToolkit.Mvvm.ComponentModel;
using Confluir.App.ViewModels;

namespace Confluir.App.Services;

/// <summary>
/// Navegação dentro da janela principal. A raiz (página nula) é a caixa de email de 3 colunas;
/// outras telas (adicionar conta, configurações…) são empilhadas por cima e voltam com <see cref="GoBack"/>.
/// O app prefere navegar a abrir janelas ou diálogos modais.
/// </summary>
public sealed partial class NavigationService : ObservableObject
{
    private readonly Stack<ViewModelBase> _backStack = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAtRoot))]
    public partial ViewModelBase? CurrentPage { get; private set; }

    public bool IsAtRoot => CurrentPage is null;

    public void NavigateTo(ViewModelBase page)
    {
        if (CurrentPage is not null)
            _backStack.Push(CurrentPage);
        CurrentPage = page;
    }

    public void GoBack() => CurrentPage = _backStack.Count > 0 ? _backStack.Pop() : null;
}
