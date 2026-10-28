using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Confluir.App.Localization;
using Confluir.App.Resources;
using Confluir.App.Services;
using Confluir.Core.Compose;
using Confluir.Core.Formatting;
using Confluir.Core.Models;
using Confluir.Core.Services;
using Confluir.Mail;
using Microsoft.Extensions.Logging;

namespace Confluir.App.ViewModels;

public sealed record FromOption(MailAccount Account)
{
    public string Label => $"{Account.DisplayName} — {Account.Email}";
}

/// <summary>Serviços da tela de composição.</summary>
public sealed record ComposeServices(
    IMailboxService Mailbox,
    IMailSender Sender,
    NavigationService Navigation,
    ILogger<ComposeViewModel> Logger);

public sealed partial class ComposeAttachmentViewModel(OutgoingAttachment attachment, ComposeViewModel owner) : ViewModelBase
{
    public OutgoingAttachment Attachment { get; } = attachment;
    public string FileName => Attachment.FileName;
    public string SizeText => FileSizes.Format(Attachment.Size);

    [RelayCommand]
    private void Remove() => owner.Attachments.Remove(this);
}

/// <summary>Nova mensagem, resposta ou encaminhamento, em tela própria.</summary>
public sealed partial class ComposeViewModel : ViewModelBase, INavigationPage
{
    private readonly ComposeServices _services;
    private readonly Action<string> _notify;
    private string? _inReplyTo;
    private IReadOnlyList<string>? _references;
    // Estado ao abrir a tela: descartar só pede confirmação se algo mudou desde então.
    private (string To, string Cc, string Bcc, string Subject, string Body, int Attachments) _initial = ("", "", "", "", "", 0);

    public ComposeViewModel(ComposeServices services, IReadOnlyList<MailAccount> accounts, Guid? accountId,
        Action<string> notify)
    {
        _services = services;
        _notify = notify;
        FromOptions = accounts.Select(a => new FromOption(a)).ToList();
        SelectedFrom = FromOptions.FirstOrDefault(o => o.Account.Id == accountId) ?? FromOptions.FirstOrDefault();
    }

    public ComposeMode Mode { get; private set; } = ComposeMode.New;

    public string Title => Mode switch
    {
        ComposeMode.Reply => Strings.Action_Reply,
        ComposeMode.ReplyAll => Strings.Action_ReplyAll,
        ComposeMode.Forward => Strings.Action_Forward,
        _ => Strings.Action_NewMessage,
    };

    public IReadOnlyList<FromOption> FromOptions { get; }

    [ObservableProperty]
    public partial FromOption? SelectedFrom { get; set; }

    [ObservableProperty]
    public partial string To { get; set; } = "";

    [ObservableProperty]
    public partial string Cc { get; set; } = "";

    [ObservableProperty]
    public partial string Bcc { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowCcBcc { get; set; }

    [ObservableProperty]
    public partial string Subject { get; set; } = "";

    [ObservableProperty]
    public partial string Body { get; set; } = "";

    public ObservableCollection<ComposeAttachmentViewModel> Attachments { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    public partial bool IsSending { get; private set; }

    public bool CanSend => !IsSending && SelectedFrom is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    public partial bool IsConfirmingDiscard { get; private set; }

    /// <summary>Página na frente (INavigationPage).</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>O foco inicial vai para o corpo em respostas e para "Para" em mensagens novas.</summary>
    public bool FocusBody => Mode is ComposeMode.Reply or ComposeMode.ReplyAll;

    private bool IsDirty => (To, Cc, Bcc, Subject, Body, Attachments.Count) != _initial;

    /// <summary>Prepara resposta ou encaminhamento a partir de uma mensagem aberta.</summary>
    public void InitFrom(ComposeMode mode, MessageSummary original, MessageBody body, MailAccount originalAccount)
    {
        Mode = mode;
        SelectedFrom = FromOptions.FirstOrDefault(o => o.Account.Id == originalAccount.Id) ?? SelectedFrom;
        Subject = ReplyBuilder.Subject(original.Subject, mode);
        var date = DateGrouping.SafeToOffset(original.Date, DateTimeOffset.Now.Offset);
        var culture = LocalizationService.Instance.FormatCulture;
        var dateText = $"{date.ToString("D", culture)} {date.ToString("t", culture)}";
        var from = new MailAddress(original.FromName, original.FromAddress);

        if (mode is ComposeMode.Reply or ComposeMode.ReplyAll)
        {
            var (to, cc) = ReplyBuilder.Recipients(mode, from, body.ReplyTo, body.To, body.Cc, originalAccount.Email);
            To = AddressParser.Format(to);
            Cc = AddressParser.Format(cc);
            ShowCcBcc = cc.Count > 0;
            _inReplyTo = body.MessageIdHeader;
            _references = ReplyBuilder.References(body.References, body.MessageIdHeader);
            Body = $"\n\n{string.Format(Strings.Compose_QuoteHeader, dateText, from)}\n{ReplyBuilder.Quote(body.TextBody)}";
        }
        else
        {
            Body = $"\n\n{Strings.Compose_ForwardHeader}\n" +
                   $"{Strings.Compose_From}: {from}\n{Strings.Compose_Date}: {dateText}\n" +
                   $"{Strings.Compose_Subject}: {original.Subject}\n{Strings.Reading_To}: {AddressParser.Format(body.To)}\n\n" +
                   body.TextBody;
            foreach (var a in body.Attachments)
                Attachments.Add(new ComposeAttachmentViewModel(
                    new OutgoingAttachment(a.FileName, a.Size, SourceMessageId: original.Id, SourceIndex: a.Index), this));
        }
        _initial = (To, Cc, Bcc, Subject, Body, Attachments.Count);
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(FocusBody));
    }

    /// <summary>Anexa arquivos do disco (botão ou arrastar e soltar).</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                continue;
            Attachments.Add(new ComposeAttachmentViewModel(new OutgoingAttachment(info.Name, info.Length, FilePath: info.FullName), this));
        }
    }

    /// <summary>Sugestões para o campo de destinatários (endereços já vistos).</summary>
    public async Task<IEnumerable<object>> SuggestAsync(string? text, CancellationToken ct)
    {
        // Sugere a partir do último destinatário digitado.
        var token = (text ?? "").Split(',', ';').Last().Trim();
        if (token.Length < 2)
            return [];
        var found = await _services.Mailbox.SearchAddressesAsync(token, 8, ct);
        return found.Select(a => (object)a.ToString());
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        Error = null;
        if (SelectedFrom is null)
            return;
        if (!AddressParser.TryParse(To, out var to, out var bad) ||
            !AddressParser.TryParse(Cc, out var cc, out bad) ||
            !AddressParser.TryParse(Bcc, out var bcc, out bad))
        {
            Error = string.Format(Strings.Compose_InvalidAddress, bad);
            return;
        }
        if (to.Count + cc.Count + bcc.Count == 0)
        {
            Error = Strings.Compose_NoRecipients;
            return;
        }

        var message = new OutgoingMessage(SelectedFrom.Account.Id, to, cc, bcc, Subject.Trim(), Body, HtmlBody: null,
            Attachments.Select(a => a.Attachment).ToList(), _inReplyTo, _references);

        IsSending = true;
        try
        {
            await _services.Sender.SendAsync(message,
                (a, ct) => _services.Mailbox.GetAttachmentAsync(a.SourceMessageId!.Value, a.SourceIndex, ct));
            _services.Navigation.GoBack();
            _notify(Strings.Compose_Sent);
        }
        catch (Exception ex)
        {
            _services.Logger.LogWarning(ex, "Falha ao enviar mensagem");
            Error = $"{Strings.Compose_SendFailed} {ErrorMessages.For(ex, SelectedFrom.Account.Provider)}";
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>Voltar/descartar: pede confirmação inline se algo foi escrito.</summary>
    [RelayCommand]
    private void Discard()
    {
        if (IsDirty && !IsConfirmingDiscard)
        {
            IsConfirmingDiscard = true;
            return;
        }
        _services.Navigation.GoBack();
    }

    [RelayCommand]
    private void ConfirmDiscard()
    {
        IsConfirmingDiscard = false;
        _services.Navigation.GoBack();
    }

    [RelayCommand]
    private void CancelDiscard() => IsConfirmingDiscard = false;

    [RelayCommand]
    private void ToggleCcBcc() => ShowCcBcc = !ShowCcBcc;

    partial void OnSelectedFromChanged(FromOption? value) => OnPropertyChanged(nameof(CanSend));
}
