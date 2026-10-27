using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Confluir.App.Localization;
using Confluir.App.Resources;
using Confluir.App.Services;
using Confluir.Core.Formatting;
using Confluir.Core.Models;
using Confluir.Core.Services;
using Confluir.Mail;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;

namespace Confluir.App.ViewModels;

/// <summary>Painel de leitura de uma mensagem; o corpo é carregado sob demanda.</summary>
public sealed partial class MessageDetailViewModel : ViewModelBase
{
    private readonly IMailboxService _mailbox;
    private readonly ILogger _logger;

    public MessageDetailViewModel(MessageRow row, IMailboxService mailbox, ILogger logger)
    {
        _mailbox = mailbox;
        _logger = logger;
        Row = row;
        var summary = row.Summary;
        FromName = row.From;
        FromAddress = summary.FromAddress;
        Initials = GetInitials(row.From);
        // Hash estável (string.GetHashCode muda a cada execução): o mesmo remetente mantém a mesma cor.
        var hash = summary.FromAddress.ToLowerInvariant().Aggregate(17u, (h, c) => unchecked(h * 31 + c));
        AvatarColor = AccountColors.All[(int)(hash % (uint)AccountColors.All.Count)].Hex;
    }

    private static CultureInfo Culture => LocalizationService.Instance.FormatCulture;

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

    [ObservableProperty]
    public partial string Body { get; private set; } = "";

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

    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        Error = null;
        try
        {
            var body = await _mailbox.GetMessageBodyAsync(Row.Summary.Id, ct);
            To = string.Join(", ", body.To);
            Cc = string.Join(", ", body.Cc);
            Body = body.TextBody;
            Attachments = body.Attachments.Select(a => new AttachmentViewModel(a)).ToList();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao carregar o corpo da mensagem {MessageId}", Row.Summary.Id);
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

public sealed class AttachmentViewModel(AttachmentInfo info)
{
    public string FileName => info.FileName;

    public string SizeText => FormatSize(info.Size);

    public LucideIconKind Icon => info.MimeType switch
    {
        var t when t.StartsWith("image/", StringComparison.Ordinal) => LucideIconKind.FileImage,
        var t when t.Contains("spreadsheet", StringComparison.Ordinal) || t == "text/csv" => LucideIconKind.FileSpreadsheet,
        var t when t.StartsWith("text/", StringComparison.Ordinal) || t == "application/pdf" => LucideIconKind.FileText,
        "application/zip" or "application/x-7z-compressed" or "application/x-rar-compressed" => LucideIconKind.FileArchive,
        _ => LucideIconKind.File,
    };

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
