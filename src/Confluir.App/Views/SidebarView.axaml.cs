using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Confluir.App.ViewModels;

namespace Confluir.App.Views;

public partial class SidebarView : UserControl
{
    // Conta do menu do botão direito (a linha da lista captura o ponteiro, por isso o menu fica na lista toda).
    private AccountNode? _menuAccount;

    public SidebarView()
    {
        InitializeComponent();
        Nodes.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(Nodes).Properties.IsRightButtonPressed)
                SetMenuTarget((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Nodes.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Apps || (e.Key == Key.F10 && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
                SetMenuTarget((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private MenuFlyout? _accountMenu;

    // Menu conforme a linha: conta, pasta criada pelo usuário (excluir) ou nenhum (caixa unificada e pastas especiais).
    private void SetMenuTarget(object? node)
    {
        _accountMenu ??= Nodes.ContextFlyout as MenuFlyout;
        var folderMenu = Nodes.Resources["FolderMenu"] as MenuFlyout;
        _menuAccount = node as AccountNode;
        var folder = node is FolderNode { CanDelete: true } f ? f : null;
        Nodes.ContextFlyout = _menuAccount is not null ? _accountMenu : folder is not null ? folderMenu : null;
        foreach (var (menu, context) in new[] { (_accountMenu, (object?)_menuAccount), (folderMenu, folder) })
        {
            if (menu is null)
                continue;
            foreach (var item in menu.Items.OfType<Control>())
                item.DataContext = context;
        }
    }

    // O menu de conta só abre sobre uma conta.
    private void OnAccountMenuOpening(object? sender, EventArgs e)
    {
        if (_menuAccount is null && sender is MenuFlyout menu)
            menu.Hide();
    }

    // Abrir/fechar a conta: as pastas crescem a partir do cabeçalho (e encolhem de volta antes de sair da lista).

    private static TimeSpan ExpandDuration => TimeSpan.FromMilliseconds(200 * SlowMotion);
    private static TimeSpan CollapseDuration => TimeSpan.FromMilliseconds(160 * SlowMotion);

    // Câmera lenta para a verificação sem janela, que captura quadro a quadro (lá cada quadro demora).
    private static double SlowMotion = 1;
    private const double FolderRowHeight = 40; // um pouco acima da linha real; a animação para quando a linha cabe

    private readonly HashSet<AccountNode> _collapsing = [];

    private async void OnAccountHeaderClick(object? sender, RoutedEventArgs e)
    {
        // O botão "…" fica dentro do cabeçalho e o clique dele sobe até aqui.
        if (!ReferenceEquals(e.Source, sender) || sender is not Button { DataContext: AccountNode node })
            return;
        if (!node.IsExpanded)
        {
            // As pastas entram fechadas (o tema lê IsEntering desde o primeiro quadro) e crescem depois do layout
            // que cria as linhas; as que ficaram fora da tela só perdem a marca.
            var folders = node.Folders.ToList();
            foreach (var folder in folders)
                folder.IsEntering = true;
            void OnLayoutUpdated(object? s, EventArgs args)
            {
                Nodes.LayoutUpdated -= OnLayoutUpdated;
                foreach (var folder in folders)
                {
                    if (Nodes.ContainerFromItem(folder) is ListBoxItem row)
                        _ = Expand(row, folder);
                    else
                        folder.IsEntering = false;
                }
            }
            Nodes.LayoutUpdated += OnLayoutUpdated;
            node.ToggleCommand.Execute(null);
            return;
        }
        if (!_collapsing.Add(node))
            return;
        try
        {
            var rows = node.Folders.Select(f => Nodes.ContainerFromItem(f)).OfType<ListBoxItem>().ToList();
            var animations = Task.WhenAll(rows.Select(row => Animate(row, row.Bounds.Height, 0, 1, 0, CollapseDuration, new CubicEaseIn())));
            try
            {
                // Fechada por baixo da animação, já com ela rodando (antes, a linha sumiria por um quadro no começo):
                // entre o fim dela e a saída da lista a linha não pisca de volta.
                await Task.Delay(CollapseDuration / 2);
                foreach (var row in rows)
                {
                    row.MaxHeight = 0;
                    row.Opacity = 0;
                }
                await animations;
                if (node.IsExpanded)
                    node.ToggleCommand.Execute(null);
            }
            finally
            {
                foreach (var row in rows)
                {
                    row.ClearValue(MaxHeightProperty);
                    row.ClearValue(OpacityProperty);
                }
            }
        }
        finally
        {
            _collapsing.Remove(node);
        }
    }

    // A marca sai no meio da animação, quando ela já cobre os valores de base: nem no começo (a linha apareceria cheia)
    // nem depois do fim (voltaria a zero por um quadro).
    private static async Task Expand(ListBoxItem row, FolderNode folder)
    {
        var animation = Animate(row, 0, FolderRowHeight, 0, 1, ExpandDuration, new CubicEaseOut());
        await Task.Delay(ExpandDuration / 2);
        folder.IsEntering = false;
        await animation;
    }

    // Altura máxima, margem e opacidade juntas; ao terminar a linha volta aos valores do tema (pode ser reciclada).
    private static async Task Animate(ListBoxItem row, double fromHeight, double toHeight, double fromOpacity, double toOpacity,
        TimeSpan duration, Easing easing)
    {
        var margin = row.Margin;
        var collapsed = new Thickness(margin.Left, 0, margin.Right, 0);
        var growing = toHeight > fromHeight;
        var animation = new Animation
        {
            Duration = duration,
            Easing = easing,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(MaxHeightProperty, fromHeight),
                        new Setter(OpacityProperty, fromOpacity),
                        new Setter(MarginProperty, growing ? collapsed : margin),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters =
                    {
                        new Setter(MaxHeightProperty, toHeight),
                        new Setter(OpacityProperty, toOpacity),
                        new Setter(MarginProperty, growing ? margin : collapsed),
                    },
                },
            },
        };
        row.ClipToBounds = true;
        try
        {
            await animation.RunAsync(row);
        }
        finally
        {
            row.ClearValue(ClipToBoundsProperty);
        }
    }

    // O campo "Nova pasta" recebe o foco quando aparece.
    private void OnNewFolderBoxAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox { DataContext: AccountNode node } box)
        {
            void OnChanged(object? s, PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(AccountNode.IsCreatingFolder) && node.IsCreatingFolder)
                    Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Background);
            }
            node.PropertyChanged += OnChanged;
            box.Tag = (node, (PropertyChangedEventHandler)OnChanged);
        }
    }

    private void OnNewFolderBoxDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox { Tag: (AccountNode node, PropertyChangedEventHandler handler) } box)
        {
            node.PropertyChanged -= handler;
            box.Tag = null;
        }
    }
}
