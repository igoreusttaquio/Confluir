using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Confluir.App.Resources;
using Confluir.App.Services;
using Microsoft.Extensions.Logging;

namespace Confluir.App.ViewModels;

public sealed record ScaleOption(double Value)
{
    public string Label => $"{Value * 100:0}%";
}

public sealed record LineHeightOption(double Value, string LabelKey);

/// <summary>
/// Configurações → Fontes: interface, leitura e monoespaçada, escala e altura de linha. As escolhas aparecem
/// na pré-visualização e só valem ao aplicar; importar arquivo ou do Google Fonts adiciona famílias às listas.
/// </summary>
public sealed partial class FontSettingsViewModel : ViewModelBase
{
    private readonly FontService _fonts;
    private readonly ILogger _logger;
    private bool _loading;

    public FontSettingsViewModel(FontService fonts, ILogger logger)
    {
        _fonts = fonts;
        _logger = logger;
        ScaleOptions = [new(0.9), new(1.0), new(1.1), new(1.25)];
        LineHeightOptions =
        [
            new(1.4, nameof(Strings.Fonts_LineHeight_Compact)),
            new(1.6, nameof(Strings.Fonts_LineHeight_Normal)),
            new(1.9, nameof(Strings.Fonts_LineHeight_Relaxed)),
        ];
        Reload();
    }

    public IReadOnlyList<ScaleOption> ScaleOptions { get; }
    public IReadOnlyList<LineHeightOption> LineHeightOptions { get; }

    [ObservableProperty]
    public partial IReadOnlyList<FontChoice> InterfaceChoices { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<FontChoice> ReadingChoices { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<FontChoice> MonoChoices { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial FontChoice? SelectedInterface { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial FontChoice? SelectedReading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial FontChoice? SelectedMono { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges), nameof(PreviewFontSize))]
    public partial ScaleOption? SelectedScale { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges), nameof(PreviewLineHeight))]
    public partial LineHeightOption? SelectedLineHeight { get; set; }

    /// <summary>Tamanho do texto de leitura na pré-visualização (acompanha a escala escolhida).</summary>
    public double PreviewFontSize => 15 * (SelectedScale?.Value ?? 1);

    public double PreviewLineHeight => PreviewFontSize * (SelectedLineHeight?.Value ?? 1.6);

    public bool HasChanges =>
        !_loading && (
            SelectedInterface?.Key != _fonts.Current.Interface ||
            SelectedReading?.Key != _fonts.Current.Reading ||
            SelectedMono?.Key != _fonts.Current.Mono ||
            SelectedScale?.Value != _fonts.Current.Scale ||
            SelectedLineHeight?.Value != _fonts.Current.ReadingLineHeight);

    [ObservableProperty]
    public partial string GoogleFamily { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportGoogleCommand))]
    public partial bool IsImporting { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportMessage))]
    public partial string? ImportMessage { get; private set; }

    [ObservableProperty]
    public partial bool ImportFailed { get; private set; }

    public bool HasImportMessage => ImportMessage is not null;

    /// <summary>Fontes importadas, com a licença quando conhecida.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> ImportedSummary { get; private set; } = [];

    public bool HasImported => ImportedSummary.Count > 0;

    /// <summary>Recarrega as listas (após importar) mantendo o que estava escolhido.</summary>
    private void Reload(string? keepInterface = null, string? keepReading = null, string? keepMono = null)
    {
        _loading = true;
        try
        {
            var interfaceKey = keepInterface ?? SelectedInterface?.Key ?? _fonts.Current.Interface;
            var readingKey = keepReading ?? SelectedReading?.Key ?? _fonts.Current.Reading;
            var monoKey = keepMono ?? SelectedMono?.Key ?? _fonts.Current.Mono;
            InterfaceChoices = _fonts.Choices(FontRole.Interface);
            ReadingChoices = _fonts.Choices(FontRole.Reading);
            MonoChoices = _fonts.Choices(FontRole.Mono);
            SelectedInterface = Pick(InterfaceChoices, interfaceKey);
            SelectedReading = Pick(ReadingChoices, readingKey);
            SelectedMono = Pick(MonoChoices, monoKey);
            SelectedScale ??= ScaleOptions.FirstOrDefault(o => Math.Abs(o.Value - _fonts.Current.Scale) < 0.01) ?? ScaleOptions[1];
            SelectedLineHeight ??= LineHeightOptions.FirstOrDefault(o => Math.Abs(o.Value - _fonts.Current.ReadingLineHeight) < 0.01)
                                  ?? LineHeightOptions[1];
            ImportedSummary = _fonts.ImportedFamilies
                .Select(f => _fonts.ImportedLicense(f) is { } license ? $"{f} · {license}" : f)
                .ToList();
            OnPropertyChanged(nameof(HasImported));
        }
        finally
        {
            _loading = false;
        }
        OnPropertyChanged(nameof(HasChanges));
    }

    private static FontChoice Pick(IReadOnlyList<FontChoice> choices, string? key) =>
        choices.FirstOrDefault(c => c.Key == key) ?? choices[0];

    [RelayCommand]
    private void Apply()
    {
        _fonts.Save(SelectedInterface?.Key, SelectedReading?.Key, SelectedMono?.Key,
            SelectedScale?.Value ?? 1, SelectedLineHeight?.Value ?? 1.6);
        OnPropertyChanged(nameof(HasChanges));
    }

    /// <summary>Volta a pré-visualização para o que está aplicado.</summary>
    [RelayCommand]
    private void Revert()
    {
        SelectedScale = null;
        SelectedLineHeight = null;
        // "" (e não nulo) força a chave aplicada; a padrão (nula) cai na primeira opção.
        Reload(_fonts.Current.Interface ?? "", _fonts.Current.Reading ?? "", _fonts.Current.Mono ?? "");
    }

    /// <summary>Padrões do app (ainda precisa aplicar).</summary>
    [RelayCommand]
    private void UseDefaults()
    {
        SelectedInterface = InterfaceChoices[0];
        SelectedReading = ReadingChoices[0];
        SelectedMono = MonoChoices[0];
        SelectedScale = ScaleOptions[1];
        SelectedLineHeight = LineHeightOptions[1];
    }

    public Task ImportFilesAsync(IReadOnlyList<string> files) =>
        RunImportAsync(ct => _fonts.ImportFilesAsync(files, ct));

    private bool CanImportGoogle() => !IsImporting;

    [RelayCommand(CanExecute = nameof(CanImportGoogle))]
    private async Task ImportGoogleAsync()
    {
        if (string.IsNullOrWhiteSpace(GoogleFamily))
            return;
        await RunImportAsync(ct => _fonts.ImportGoogleFontAsync(GoogleFamily, ct));
        if (!ImportFailed)
            GoogleFamily = "";
    }

    private async Task RunImportAsync(Func<CancellationToken, Task<FontImportResult>> import)
    {
        IsImporting = true;
        ImportMessage = null;
        try
        {
            var result = await import(CancellationToken.None);
            var families = string.Join(", ", result.Families);
            ImportFailed = false;
            ImportMessage = result.License is { } license
                ? string.Format(Strings.Fonts_ImportedWithLicense, families, license)
                : string.Format(Strings.Fonts_Imported, families);
            Reload();
        }
        catch (FontImportException ex)
        {
            ImportFailed = true;
            ImportMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao importar fonte");
            ImportFailed = true;
            ImportMessage = Strings.Fonts_ImportFailed;
        }
        finally
        {
            IsImporting = false;
        }
    }

    partial void OnImportedSummaryChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasImported));
}
