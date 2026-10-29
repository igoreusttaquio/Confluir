using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Confluir.App.Views;

/// <summary>
/// Opções da imagem clicada num <see cref="RichEditorView"/>: à esquerda ou à direita com o texto ao lado, ou na
/// linha do texto; e a largura.
/// </summary>
public partial class ImageOptionsBar : UserControl
{
    /// <summary>Larguras de Pequena, Média e Grande (0 = original).</summary>
    public static readonly int[] Widths = [64, 120, 200, 0];

    private RichEditorView? _editor;
    private readonly ToggleButton[] _aligns;
    private readonly ToggleButton[] _sizes;

    public ImageOptionsBar()
    {
        InitializeComponent();
        _aligns = [AlignLeft, AlignRight, AlignInline];
        _sizes = [SizeSmall, SizeMedium, SizeLarge, SizeOriginal];
        foreach (var button in _aligns)
            button.Click += (_, _) => _editor?.SetImageAlign((string)button.Tag!);
        for (var i = 0; i < _sizes.Length; i++)
        {
            var width = Widths[i];
            _sizes[i].Click += (_, _) => _editor?.SetImageWidth(width);
        }
    }

    public RichEditorView? Editor
    {
        get => _editor;
        set
        {
            if (_editor is not null)
                _editor.ImageChanged -= OnImageChanged;
            _editor = value;
            if (_editor is not null)
                _editor.ImageChanged += OnImageChanged;
        }
    }

    private void OnImageChanged(object? sender, EditorImageState? state)
    {
        IsVisible = state is not null;
        if (state is null)
            return;
        // O editor devolve o estado depois de cada mudança: os botões mostram o que vale agora.
        foreach (var button in _aligns)
            button.IsChecked = (string)button.Tag! == state.Align;
        for (var i = 0; i < _sizes.Length; i++)
            _sizes[i].IsChecked = Widths[i] == state.Width;
    }
}
