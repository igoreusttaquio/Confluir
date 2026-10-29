using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Confluir.App.ViewModels;

namespace Confluir.App.Views;

/// <summary>Conteúdo de uma mensagem: ações, cabeçalho, avisos, anexos e corpo. Usado no painel e na tela própria.</summary>
public partial class MessageDetailView : UserControl
{
    /// <summary>Falso quando a área não está na frente: o WebView do corpo (janela nativa) é escondido.</summary>
    public static readonly StyledProperty<bool> IsBodyActiveProperty =
        AvaloniaProperty.Register<MessageDetailView, bool>(nameof(IsBodyActive), defaultValue: true);

    public static readonly StyledProperty<bool> ShowBackProperty =
        AvaloniaProperty.Register<MessageDetailView, bool>(nameof(ShowBack));

    public static readonly StyledProperty<ICommand?> BackCommandProperty =
        AvaloniaProperty.Register<MessageDetailView, ICommand?>(nameof(BackCommand));

    public static readonly StyledProperty<bool> ShowOpenInPageProperty =
        AvaloniaProperty.Register<MessageDetailView, bool>(nameof(ShowOpenInPage));

    public MessageDetailView() => InitializeComponent();

    public bool IsBodyActive
    {
        get => GetValue(IsBodyActiveProperty);
        set => SetValue(IsBodyActiveProperty, value);
    }

    public bool ShowBack
    {
        get => GetValue(ShowBackProperty);
        set => SetValue(ShowBackProperty, value);
    }

    public ICommand? BackCommand
    {
        get => GetValue(BackCommandProperty);
        set => SetValue(BackCommandProperty, value);
    }

    public bool ShowOpenInPage
    {
        get => GetValue(ShowOpenInPageProperty);
        set => SetValue(ShowOpenInPageProperty, value);
    }

    /// <summary>"Copiar endereço" no menu de um remetente ou destinatário.</summary>
    private async void OnCopyAddress(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not AddressViewModel address ||
            TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        await clipboard.SetTextAsync(address.Email);
        address.Copied();
    }
}
