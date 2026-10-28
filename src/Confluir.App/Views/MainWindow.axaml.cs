using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using Confluir.App.Services;
using Confluir.App.ViewModels;
using Confluir.Core.Settings;

namespace Confluir.App.Views;

public partial class MainWindow : Window
{
    private const double CollapsedSidebarWidth = 64;

    private readonly ISettingsStore? _settings;
    private readonly MainWindowViewModel? _vm;

    // Usado pelo previewer do XAML.
    public MainWindow() => InitializeComponent();

    public MainWindow(MainWindowViewModel viewModel, ISettingsStore settings, FontService fonts) : this()
    {
        _settings = settings;
        _vm = viewModel;
        DataContext = viewModel;

        // Escala da interface (Configurações → Fontes): toda a janela, inclusive ícones e espaçamentos.
        void ApplyScale() => ScaleHost.LayoutTransform = new ScaleTransform(fonts.Current.Scale, fonts.Current.Scale);
        ApplyScale();
        fonts.Changed += (_, _) => ApplyScale();

        ApplyLayout();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Opened += async (_, _) => await viewModel.InitializeAsync();
    }

    private static readonly TimeSpan SidebarAnimation = TimeSpan.FromMilliseconds(220);
    private int _sidebarAnimationId;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsSidebarCollapsed))
        {
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

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_settings is null || e.Cancel)
            return;
        SaveWidths();
        _settings.Save();
    }
}
