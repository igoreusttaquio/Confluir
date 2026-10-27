using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Confluir.App.Services;

namespace Confluir.App.ViewModels;

/// <summary>Mensagem aberta em tela própria (navegação), com o mesmo conteúdo do painel de leitura.</summary>
public sealed partial class MessagePageViewModel(MessageDetailViewModel detail, Action goBack) : ViewModelBase, INavigationPage
{
    public MessageDetailViewModel Detail { get; } = detail;

    /// <summary>Verdadeiro enquanto a página está na frente; o WebView do corpo só aparece nesse momento.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    public IRelayCommand BackCommand { get; } = new RelayCommand(goBack);

    public override void OnLanguageChanged()
    {
        Detail.OnLanguageChanged();
        base.OnLanguageChanged();
    }
}
