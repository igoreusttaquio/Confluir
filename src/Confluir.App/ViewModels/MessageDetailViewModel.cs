using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Confluir.App.Localization;
using Confluir.App.Resources;
using Confluir.App.Services;
using Confluir.Core.Formatting;
using Confluir.Core.Models;
using Confluir.Core.Services;
using Confluir.Core.Settings;
using Confluir.Mail;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;

namespace Confluir.App.ViewModels;

/// <summary>Serviços usados pelo painel de leitura.</summary>
public sealed record ReadingServices(
    IMailboxService Mailbox,
    ThemeService Theme,
    ISettingsStore Settings,
    AttachmentService Attachments,
    ILogger<MessageDetailViewModel> Logger);

public interface IMessageActions
{
    bool CanArchive(MessageDetailViewModel message);
    Task ArchiveAsync(MessageDetailViewModel message);
    Task DeleteAsync(MessageDetailViewModel message);
    Task ToggleFlagAsync(MessageDetailViewModel message);
    Task ToggleReadAsync(MessageDetailViewModel message);
}

/// <summary>Painel de leitura de uma mensagem; o corpo é carregado sob demanda.</summary>
public sealed partial class MessageDetailViewModel : ViewModelBase
{
    // Fonte de leitura até a fase 7 (fontes configuráveis).
    private const string ReadingFont = "\"Segoe UI\", system-ui, -apple-system, \"Helvetica Neue\", Arial, sans-serif";

    private readonly ReadingServices _services;
    private MessageBody? _body;
    private bool _allowRemoteImages;

    public MessageDetailViewModel(MessageRow row, ReadingServices services)
    {
        _services = services;
        Row = row;
        var summary = row.Summary;
        FromName = row.From;
        FromAddress = summary.FromAddress;
        Initials = GetInitials(row.From);
        // Hash estável (string.GetHashCode muda a cada execução): o mesmo remetente mantém a mesma cor.
        var hash = summary.FromAddress.ToLowerInvariant().Aggregate(17u, (h, c) => unchecked(h * 31 + c));
        AvatarColor = AccountColors.All[(int)(hash % (uint)AccountColors.All.Count)].Hex;
        _allowRemoteImages = TrustedSenders.Contains(SenderKey);
    }

    private static CultureInfo Culture => LocalizationService.Instance.FormatCulture;
    private List<string> TrustedSenders => _services.Settings.Current.Reading.TrustedImageSenders;
    private string SenderKey => FromAddress.Trim().ToLowerInvariant();

    public MessageRow Row { get; }
    public string Subject => Row.Subject;
    public string AccountName => Row.Account.DisplayName;
    public string AccountColor => Row.Account.ColorHex;
    public string FromName { get; }
    public string FromAddress { get; }
    public string Initials { get; }
    public string AvatarColor { get; }

    /// <summary>Data completa no formato do idioma, ex.: "terça-feira, 27 de outubro de 2026, 14:45".</summary>
    public string DateText
    {
        get
        {
            var local = DateGrouping.SafeToOffset(Row.Summary.Date, DateTimeOffset.Now.Offset);
            return $"{local.ToString("D", Culture)}, {local.ToString("t", Culture)}";
        }
    }

    [ObservableProperty]
    public partial string To { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCc))]
    public partial string Cc { get; private set; } = "";

    public bool HasCc => Cc.Length > 0;

    /// <summary>Texto puro (usado quando não há WebView disponível).</summary>
    [ObservableProperty]
    public partial string Body { get; private set; } = "";

    /// <summary>Documento HTML seguro para o WebView.</summary>
    [ObservableProperty]
    public partial string? Document { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlockedImages), nameof(BlockedImagesText))]
    public partial int BlockedImages { get; private set; }

    public bool HasBlockedImages => BlockedImages > 0;

    public string BlockedImagesText => LocalizationService.Instance.Plural("Reading_ImagesBlocked", BlockedImages);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachments))]
    public partial IReadOnlyList<AttachmentViewModel> Attachments { get; private set; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [RelayCommand]
    private Task RetryAsync() => LoadAsync(CancellationToken.None);

    /// <summary>Definido por quem exibe a mensagem: abre esta mesma mensagem em tela própria.</summary>
    public Action<MessageDetailViewModel>? OpenInPageRequested { get; set; }

    [RelayCommand]
    private void OpenInPage() => OpenInPageRequested?.Invoke(this);

    /// <summary>Quem executa arquivar/excluir/sinalizar (precisa mexer na lista e nos contadores).</summary>
    public IMessageActions? Actions { get; set; }

    public bool CanArchive => Actions?.CanArchive(this) ?? false;

    public string FlagTooltip => Row.IsFlagged ? Strings.Action_Unflag : Strings.Action_Flag;
    public string ReadTooltip => Row.IsRead ? Strings.Action_MarkUnread : Strings.Action_MarkRead;

    [RelayCommand]
    private Task Archive() => Actions?.ArchiveAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private Task Delete() => Actions?.DeleteAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private async Task ToggleFlag()
    {
        if (Actions is null)
            return;
        await Actions.ToggleFlagAsync(this);
        OnPropertyChanged(nameof(FlagTooltip));
    }

    [RelayCommand]
    private async Task ToggleRead()
    {
        if (Actions is null)
            return;
        await Actions.ToggleReadAsync(this);
        OnPropertyChanged(nameof(ReadTooltip));
    }

    [RelayCommand]
    private void ShowImages()
    {
        _allowRemoteImages = true;
        RebuildDocument();
    }

    [RelayCommand]
    private void AlwaysShowImages()
    {
        if (!TrustedSenders.Contains(SenderKey))
        {
            TrustedSenders.Add(SenderKey);
            _services.Settings.Save();
        }
        ShowImages();
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        Error = null;
        try
        {
            var body = await _services.Mailbox.GetMessageBodyAsync(Row.Summary.Id, ct);
            _body = body;
            To = string.Join(", ", body.To);
            Cc = string.Join(", ", body.Cc);
            Body = body.TextBody;
            Attachments = body.Attachments.Select(a => new AttachmentViewModel(a, this)).ToList();
            RebuildDocument();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _services.Logger.LogError(ex, "Falha ao carregar o corpo da mensagem {MessageId}", Row.Summary.Id);
            var result = MailErrorClassifier.Classify(ex, Row.Account.Provider);
            Error = result.Error is ConnectionError.ServerUnreachable or ConnectionError.Timeout
                ? Strings.Reading_NotAvailableOffline
                : $"{Strings.Reading_Error} {ErrorMessages.For(result)}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Remonta o HTML (ex.: tema mudou ou o usuário liberou as imagens).</summary>
    public void RebuildDocument()
    {
        if (_body is null)
            return;
        var document = EmailDocumentBuilder.Build(_body, _allowRemoteImages, _services.Theme.CurrentPalette(), ReadingFont);
        BlockedImages = document.BlockedRemoteImages;
        Document = document.Html;
    }

    internal async Task SaveAttachmentAsync(AttachmentViewModel attachment)
    {
        attachment.IsBusy = true;
        attachment.Error = null;
        try
        {
            await _services.Attachments.SaveAsync(Row.Summary.Id, attachment.Info);
        }
        catch (Exception ex)
        {
            _services.Logger.LogWarning(ex, "Falha ao salvar anexo da mensagem {MessageId}", Row.Summary.Id);
            attachment.Error = Strings.Attachment_Error;
        }
        finally
        {
            attachment.IsBusy = false;
        }
    }

    internal async Task OpenAttachmentAsync(AttachmentViewModel attachment)
    {
        attachment.IsBusy = true;
        attachment.Error = null;
        try
        {
            await _services.Attachments.OpenAsync(Row.Summary.Id, attachment.Info);
        }
        catch (Exception ex)
        {
            _services.Logger.LogWarning(ex, "Falha ao abrir anexo da mensagem {MessageId}", Row.Summary.Id);
            attachment.Error = Strings.Attachment_Error;
        }
        finally
        {
            attachment.IsBusy = false;
        }
    }

    public override void OnLanguageChanged()
    {
        foreach (var attachment in Attachments)
            attachment.OnLanguageChanged();
        base.OnLanguageChanged();
    }

    private static string GetInitials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => string.Concat(parts[0][..1], parts[^1][..1]).ToUpperInvariant(),
        };
    }
}

public sealed partial class AttachmentViewModel(AttachmentInfo info, MessageDetailViewModel owner) : ViewModelBase
{
    public AttachmentInfo Info { get; } = info;

    public string FileName => Info.FileName;

    public string SizeText => FormatSize(Info.Size);

    /// <summary>Executáveis (.exe, .bat, .js…) só podem ser salvos.</summary>
    public bool CanOpen => AttachmentService.CanOpen(Info.FileName);

    public string OpenTooltip => CanOpen ? Strings.Attachment_Open : Strings.Attachment_OpenBlocked;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    public LucideIconKind Icon => Info.MimeType switch
    {
        var t when t.StartsWith("image/", StringComparison.Ordinal) => LucideIconKind.FileImage,
        var t when t.Contains("spreadsheet", StringComparison.Ordinal) || t == "text/csv" => LucideIconKind.FileSpreadsheet,
        var t when t.StartsWith("text/", StringComparison.Ordinal) || t == "application/pdf" => LucideIconKind.FileText,
        "application/zip" or "application/x-7z-compressed" or "application/x-rar-compressed" => LucideIconKind.FileArchive,
        _ => LucideIconKind.File,
    };

    [RelayCommand]
    private Task Save() => owner.SaveAttachmentAsync(this);

    [RelayCommand]
    private Task Open() => CanOpen ? owner.OpenAttachmentAsync(this) : Task.CompletedTask;

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size.ToString("0.#", LocalizationService.Instance.FormatCulture)} {units[unit]}";
    }
}
