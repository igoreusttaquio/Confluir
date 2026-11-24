using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Media;
using Confluir.App.Services;
using Confluir.App.ViewModels;
using Confluir.Core.Settings;

namespace Confluir.App.Views;

public partial class MainWindow : Window
{
    // Faixa recolhida: 62px de conteúdo dentro do cartão (8px de margem de cada lado e 1px de borda).
    private const double CollapsedSidebarWidth = 80;

    private readonly ISettingsStore? _settings;
    private readonly MainWindowViewModel? _vm;
    private readonly ShortcutService? _shortcuts;
    private readonly FontService? _fonts;

    // Usado pelo previewer do XAML.
    public MainWindow() => InitializeComponent();

    public MainWindow(MainWindowViewModel viewModel, ISettingsStore settings, FontService fonts, ShortcutService shortcuts) : this()
    {
        _settings = settings;
        _shortcuts = shortcuts;
        _fonts = fonts;
        _vm = viewModel;
        DataContext = viewModel;

        // Escala da interface (Configurações → Fontes): toda a janela, inclusive ícones e espaçamentos.
        void ApplyScale()
        {
            ScaleHost.LayoutTransform = new ScaleTransform(fonts.Current.Scale, fonts.Current.Scale);
            ApplyMinimumSize();
            UpdateNarrow();
        }
        ApplyScale();
        fonts.Changed += (_, _) => ApplyScale();

        ApplyLayout();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.FocusSearchRequested += (_, _) => this.FindDescendantOfType<MessageListView>()?.FocusSearch();
        RestoreWindowSize();
        Opened += async (_, _) =>
        {
            FitToScreen(center: true);
            ApplyMinimumSize();
            await viewModel.InitializeAsync();
        };
    }

    private static readonly TimeSpan SidebarAnimation = TimeSpan.FromMilliseconds(220);
    private int _sidebarAnimationId;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsSidebarCollapsed))
        {
            UpdateNarrow();
            SaveWidths();
            AnimateSidebar();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsTwoColumnLayout))
        {
            SaveWidths();
            ApplyLayout();
        }
    }

    /// <summary>Anima a largura da barra lateral quadro a quadro (ColumnDefinition não tem transições).</summary>
    private void AnimateSidebar()
    {
        if (_settings is null || _vm is null)
            return;
        var column = Columns.ColumnDefinitions[0];
        var from = column.ActualWidth;
        var to = _vm.IsSidebarCollapsed ? CollapsedSidebarWidth : _settings.Current.Layout.SidebarWidth;
        var id = ++_sidebarAnimationId;
        var started = DateTime.UtcNow;

        // Libera os limites durante a animação; os definitivos voltam no fim (ApplyLayout).
        column.MinWidth = Math.Min(from, to);
        column.MaxWidth = Math.Max(from, to);
        SidebarSplitter.IsVisible = false;

        void Frame(TimeSpan _)
        {
            if (id != _sidebarAnimationId)
                return; // outra animação começou
            var t = Math.Clamp((DateTime.UtcNow - started) / SidebarAnimation, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3); // desacelera no fim
            column.Width = new GridLength(from + (to - from) * eased);
            if (t < 1)
                RequestAnimationFrame(Frame);
            else
                ApplyLayout();
        }
        RequestAnimationFrame(Frame);
    }

    /// <summary>
    /// Colunas: barra lateral (ou faixa recolhida) | lista | leitura. Em duas colunas a lista ocupa o resto
    /// e a mensagem abre em tela própria.
    /// </summary>
    private void ApplyLayout()
    {
        if (_settings is null || _vm is null)
            return;
        var layout = _settings.Current.Layout;
        var columns = Columns.ColumnDefinitions;

        var collapsed = _vm.IsSidebarCollapsed;
        columns[0].MinWidth = collapsed ? CollapsedSidebarWidth : 200;
        columns[0].MaxWidth = collapsed ? CollapsedSidebarWidth : 420;
        columns[0].Width = new GridLength(collapsed ? CollapsedSidebarWidth : layout.SidebarWidth);
        SidebarSplitter.IsVisible = !collapsed;

        var twoColumns = _vm.IsTwoColumnLayout;
        columns[2].MaxWidth = twoColumns ? double.PositiveInfinity : 720;
        columns[2].Width = twoColumns ? new GridLength(1, GridUnitType.Star) : new GridLength(layout.MessageListWidth);
        columns[4].MinWidth = twoColumns ? 0 : 360;
        columns[4].Width = twoColumns ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ReadingSplitter.IsVisible = !twoColumns;
        ReadingPane.IsVisible = !twoColumns;
    }

    /// <summary>Guarda as larguras ajustadas pelo usuário, só das colunas no modo "normal".</summary>
    private void SaveWidths()
    {
        if (_settings is null || _vm is null)
            return;
        var columns = Columns.ColumnDefinitions;
        if (columns[0].ActualWidth > CollapsedSidebarWidth)
            _settings.Current.Layout.SidebarWidth = columns[0].ActualWidth;
        if (columns[2].Width.IsAbsolute && columns[2].ActualWidth > 0)
            _settings.Current.Layout.MessageListWidth = columns[2].ActualWidth;
    }

    /// <summary>
    /// Atalhos (<see cref="ShortcutService"/>, editáveis em Configurações). Chegam aqui depois do controle com foco: o
    /// que ele já tratou (ex.: Delete num campo de texto) não vira atalho, e ações sobre mensagens não valem enquanto se digita.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _vm is null || _shortcuts?.Find(e) is not { } action)
            return;
        if (ShortcutInfo.ActsOnMessages(action) && IsTyping())
            return;
        e.Handled = _vm.RunShortcut(action);
    }

    private bool IsTyping() =>
        FocusManager?.GetFocusedElement() is Visual focused &&
        (focused is TextBox || focused.FindAncestorOfType<TextBox>() is not null || focused.FindAncestorOfType<AutoCompleteBox>() is not null);

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_settings is null || e.Cancel)
            return;
        SaveWidths();
        SaveWindowSize();
        _settings.Save();
    }

    // Tamanho da janela ------------------------------------------------------------------------------

    // Mínimo em unidades da interface (antes da escala de Configurações → Fontes): barra lateral, lista e leitura
    // em duas colunas, e as Configurações sem cortar.
    private const double MinContentWidth = 860;
    private const double MinContentHeight = 540;
    private const double ScreenMargin = 16;

    private Size _normalSize;

    private double Scale => _fonts?.Current.Scale ?? 1;

    /// <summary>Área útil da tela da janela (sem a barra de tarefas), em unidades da janela.</summary>
    private (PixelRect Area, double Scaling)? WorkArea() =>
        (Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } screen ? (screen.WorkingArea, screen.Scaling) : null;

    private void RestoreWindowSize()
    {
        if (_settings is null)
            return;
        var layout = _settings.Current.Layout;
        if (layout is { WindowWidth: > 0, WindowHeight: > 0 })
        {
            Width = layout.WindowWidth;
            Height = layout.WindowHeight;
        }
        if (layout.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowSize()
    {
        if (_settings is null)
            return;
        var layout = _settings.Current.Layout;
        layout.WindowMaximized = WindowState == WindowState.Maximized;
        if (_normalSize is { Width: > 0, Height: > 0 })
            (layout.WindowWidth, layout.WindowHeight) = (_normalSize.Width, _normalSize.Height);
    }

    /// <summary>
    /// Fora do modo maximizado, a janela cabe na área útil da tela e fica dentro dela (ao abrir, ao sair do
    /// maximizado e ao trocar de monitor).
    /// </summary>
    private void FitToScreen(bool center = false)
    {
        if (WindowState != WindowState.Normal || WorkArea() is not var (area, scaling))
            return;
        // Bordas e barra de título ficam fora do ClientSize.
        var frame = FrameSize is { } f ? f - ClientSize : new Size(0, 0);
        var maxWidth = area.Width / scaling - frame.Width - ScreenMargin * 2;
        var maxHeight = area.Height / scaling - frame.Height - ScreenMargin * 2;
        var width = Math.Min(ClientSize.Width, maxWidth);
        var height = Math.Min(ClientSize.Height, maxHeight);
        var resized = width < ClientSize.Width || height < ClientSize.Height;
        if (resized)
        {
            MinWidth = Math.Min(MinWidth, width);
            MinHeight = Math.Min(MinHeight, height);
            Width = width;
            Height = height;
        }

        var pixelWidth = (int)Math.Ceiling((width + frame.Width) * scaling);
        var pixelHeight = (int)Math.Ceiling((height + frame.Height) * scaling);
        var x = center && resized ? area.X + (area.Width - pixelWidth) / 2 : Position.X;
        var y = center && resized ? area.Y + (area.Height - pixelHeight) / 2 : Position.Y;
        // A barra de título precisa ficar à vista; o resto, dentro da área útil se couber.
        x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - pixelWidth));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - pixelHeight));
        if (x != Position.X || y != Position.Y)
            Position = new PixelPoint(x, y);
    }

    /// <summary>O mínimo acompanha a escala da interface, sem passar do tamanho da tela.</summary>
    private void ApplyMinimumSize()
    {
        var width = MinContentWidth * Scale;
        var height = MinContentHeight * Scale;
        if (WorkArea() is var (area, scaling))
        {
            width = Math.Min(width, area.Width / scaling - ScreenMargin * 2);
            height = Math.Min(height, area.Height / scaling - ScreenMargin * 2);
        }
        MinWidth = width;
        MinHeight = height;
    }

    /// <summary>
    /// Sem largura para lista e leitura lado a lado, a mensagem abre em tela própria (como em duas colunas) em vez
    /// de a leitura ser cortada. Uma folga evita alternar ao redimensionar perto do limite.
    /// </summary>
    private void UpdateNarrow()
    {
        if (_vm is null || _settings is null || ClientSize.Width <= 0)
            return;
        var available = ClientSize.Width / Scale;
        var sidebar = _vm.IsSidebarCollapsed ? CollapsedSidebarWidth : _settings.Current.Layout.SidebarWidth;
        var list = Math.Max(Columns.ColumnDefinitions[2].MinWidth, _settings.Current.Layout.MessageListWidth);
        var needed = sidebar + list + Columns.ColumnDefinitions[4].MinWidth + 16;
        _vm.IsNarrow = _vm.IsNarrow ? available < needed + 40 : available < needed;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty)
        {
            if (WindowState == WindowState.Normal)
                _normalSize = ClientSize;
            UpdateNarrow();
        }
        else if (change.Property == WindowStateProperty && change.GetNewValue<WindowState>() == WindowState.Normal)
        {
            // Ao sair do maximizado o sistema devolve o tamanho anterior, que pode não caber nesta tela.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => FitToScreen());
        }
    }
}