using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace Confluir.App.Views;

/// <summary>Botões de cor do texto e de realce de um <see cref="RichEditorView"/>.</summary>
public partial class TextColorButtons : UserControl
{
    /// <summary>Cores que se leem bem no branco (como o destinatário costuma ver) e no tema escuro do editor.</summary>
    public static readonly string[] TextPalette =
        ["#D93025", "#E8710A", "#B06000", "#188038", "#00897B", "#1A73E8", "#3F51B5", "#9334E6", "#C2185B", "#5F6368"];

    public static readonly string[] HighlightPalette =
        ["#FFF475", "#FFD8A8", "#FDCFE8", "#CCFF90", "#A7FFEB", "#AECBFA", "#D7AEFB", "#E8EAED"];

    public TextColorButtons()
    {
        InitializeComponent();
        Fill(TextColors, TextPalette, ColorButton, "foreColor");
        Fill(HighlightColors, HighlightPalette, HighlightButton, "hiliteColor");
        ColorAuto.Click += (_, _) => Apply(ColorButton, "foreColor", "");
        HighlightNone.Click += (_, _) => Apply(HighlightButton, "hiliteColor", "");
    }

    public RichEditorView? Editor { get; set; }

    private void Fill(WrapPanel panel, string[] colors, Button owner, string command)
    {
        foreach (var color in colors)
        {
            var swatch = new Button { Background = new SolidColorBrush(Color.Parse(color)) };
            swatch.Classes.Add("swatch");
            ToolTip.SetTip(swatch, color);
            AutomationProperties.SetName(swatch, color);
            swatch.Click += (_, _) => Apply(owner, command, color);
            panel.Children.Add(swatch);
        }
    }

    private void Apply(Button owner, string command, string color)
    {
        owner.Flyout?.Hide();
        Editor?.Exec(command, color);
    }
}
