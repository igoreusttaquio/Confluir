using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Confluir.App.Localization;
using Confluir.App.Resources;
using Confluir.App.Services;
using Confluir.Core;
using Confluir.Core.Compose;
using Confluir.Core.Formatting;
using Confluir.Core.Models;
using Confluir.Core.Services;
using Confluir.Core.Settings;
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
    DraftService Drafts,
    NavigationService Navigation,
    ThemeService Theme,
    ISettingsStore Settings,
    AppPaths Paths,
    ILogger<ComposeViewModel> Logger);

public sealed partial class ComposeAttachmentViewModel(OutgoingAttachment attachment, ComposeViewModel owner) : ViewModelBase
{
    public OutgoingAttachment Attachment { get; } = attachment;
    public string FileName => Attachment.FileName;
    public string SizeText => FileSizes.Format(Attachment.Size);

    [RelayCommand]
    private void Remove() => owner.Attachments.Remove(this);
}

/// <summary>
/// Nova mensagem, resposta ou encaminhamento, em tela própria. O texto é salvo como rascunho enquanto se escreve
/// (no aparelho a cada pausa, no servidor de tempos em tempos e ao sair da tela).
/// </summary>
public sealed partial class ComposeViewModel : ViewModelBase, INavigationPage
{
    private static readonly TimeSpan LocalSaveDelay = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ServerSaveInterval = TimeSpan.FromSeconds(60);

    private readonly ComposeServices _services;
    private readonly Action<string> _notify;
    private readonly Guid _draftId;
    private readonly SemaphoreSlim _uploadGate = new(1, 1);
    private readonly DispatcherTimer _saveTimer;
    private string? _inReplyTo;
    private IReadOnlyList<string>? _references;
    private ServerDraft? _server;
    private bool _hasLocalDraft;
    private bool _finished; // enviada ou descartada
    private bool _loading;
    private DateTimeOffset _lastUpload = DateTimeOffset.Now;
    // Conteúdo ao abrir e no último salvamento: só salva (e só pede confirmação ao descartar) se algo mudou.
    private string _initialState = "";
    private string _savedState = "";

    public ComposeViewModel(ComposeServices services, IReadOnlyList<MailAccount> accounts, Guid? accountId,
        Action<string> notify, Guid? draftId = null)
    {
        _services = services;
        _notify = notify;
        _draftId = draftId ?? Guid.NewGuid();
        _saveTimer = new DispatcherTimer { Interval = LocalSaveDelay };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _ = SaveLocalAsync();
        };

        _loading = true;
        FromOptions = accounts.Select(a => new FromOption(a)).ToList();
        SelectedFrom = FromOptions.FirstOrDefault(o => o.Account.Id == accountId) ?? FromOptions.FirstOrDefault();
        IsRichTextAvailable = WebViewSupport.IsAvailable;
        IsPlainText = !IsRichTextAvailable || services.Settings.Current.Compose.PlainText;
        _loading = false;

        Attachments.CollectionChanged += (_, _) => ScheduleSave();
        _initialState = _savedState = State();
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

    /// <summary>Corpo em texto simples (modo texto; no modo formatado acompanha o HTML).</summary>
    [ObservableProperty]
    public partial string Body { get; set; } = "";

    /// <summary>Corpo formatado (conteúdo do editor).</summary>
    public string Html { get; private set; } = "";

    /// <summary>O HTML mudou por fora do editor (abrir rascunho, voltar do modo texto): o editor precisa recarregar.</summary>
    public event EventHandler? HtmlReplaced;

    /// <summary>Pede ao editor o conteúdo mais recente (o editor avisa alterações com um pequeno atraso).</summary>
    public Func<Task>? FlushEditor { get; set; }

    /// <summary>Sem WebView (motor ausente) só há o modo texto.</summary>
    public bool IsRichTextAvailable { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRichText))]
    public partial bool IsPlainText { get; set; }

    public bool IsRichText => !IsPlainText;

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

    /// <summary>Página na frente (INavigationPage). Ao sair da tela, por qualquer caminho, o rascunho é salvo.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>O foco inicial vai para o corpo em respostas e para "Para" em mensagens novas.</summary>
    public bool FocusBody => Mode is ComposeMode.Reply or ComposeMode.ReplyAll;

    private bool IsDirty => State() != _initialState;

    /// <summary>Prepara resposta ou encaminhamento a partir de uma mensagem aberta.</summary>
    public void InitFrom(ComposeMode mode, MessageSummary original, MessageBody body, MailAccount originalAccount)
    {
        _loading = true;
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
            var header = string.Format(Strings.Compose_QuoteHeader, dateText, from);
            Body = $"\n\n{header}\n{ReplyBuilder.Quote(body.TextBody)}";
            Html = ComposeHtml.Reply(header, body);
        }
        else
        {
            (string, string)[] headers =
            [
                (Strings.Compose_From, from.ToString()),
                (Strings.Compose_Date, dateText),
                (Strings.Compose_Subject, original.Subject),
                (Strings.Reading_To, AddressParser.Format(body.To)),
            ];
            Body = $"\n\n{Strings.Compose_ForwardHeader}\n" +
                   string.Concat(headers.Select(h => $"{h.Item1}: {h.Item2}\n")) + "\n" + body.TextBody;
            Html = ComposeHtml.Forward(Strings.Compose_ForwardHeader, headers, body);
            foreach (var a in body.Attachments)
                Attachments.Add(new ComposeAttachmentViewModel(
                    new OutgoingAttachment(a.FileName, a.Size, SourceMessageId: original.Id, SourceIndex: a.Index), this));
        }
        FinishLoading();
    }

    /// <summary>Continua um rascunho salvo no aparelho.</summary>
    public void InitFromDraft(Draft draft)
    {
        _loading = true;
        Mode = draft.Mode;
        SelectedFrom = FromOptions.FirstOrDefault(o => o.Account.Id == draft.AccountId) ?? SelectedFrom;
        To = draft.To;
        Cc = draft.Cc;
        Bcc = draft.Bcc;
        ShowCcBcc = draft.Cc.Length > 0 || draft.Bcc.Length > 0;
        Subject = draft.Subject;
        Html = draft.Html;
        Body = draft.Text;
        IsPlainText = draft.IsPlainText || !IsRichTextAvailable;
        _inReplyTo = draft.InReplyTo;
        _references = draft.References;
        _server = draft.Server;
        _hasLocalDraft = true;
        foreach (var a in draft.Attachments)
            Attachments.Add(new ComposeAttachmentViewModel(a, this));
        FinishLoading();
    }

    /// <summary>Continua um rascunho do servidor sem cópia local (escrito em outro aparelho ou cliente).</summary>
    public void InitFromServerDraft(MessageSummary message, MessageBody body, ServerDraft? location)
    {
        _loading = true;
        To = AddressParser.Format(body.To);
        Cc = AddressParser.Format(body.Cc);
        ShowCcBcc = body.Cc.Count > 0;
        Subject = message.Subject;
        Html = ComposeHtml.Original(body);
        Body = body.TextBody;
        _server = location;
        foreach (var a in body.Attachments)
            Attachments.Add(new ComposeAttachmentViewModel(
                new OutgoingAttachment(a.FileName, a.Size, SourceMessageId: message.Id, SourceIndex: a.Index), this));
        FinishLoading();
    }

    private void FinishLoading()
    {
        _loading = false;
        _initialState = _savedState = State();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(FocusBody));
        HtmlReplaced?.Invoke(this, EventArgs.Empty);
    }

    // O editor avisa que o texto mudou; o HTML só vem quando pedido (salvar, enviar, sair).
    private bool _editorDirty;

    public void OnEditorDirty()
    {
        _editorDirty = true;
        ScheduleSave();
    }

    /// <summary>Conteúdo vindo do editor (resposta a um pedido de <see cref="FlushEditor"/>).</summary>
    public void OnEditorContent(string html)
    {
        _editorDirty = false;
        Html = html;
    }

    /// <summary>Texto simples atual; no modo formatado é convertido do HTML fora da thread da interface.</summary>
    private Task<string> CurrentTextAsync()
    {
        if (IsPlainText)
            return Task.FromResult(Body);
        var html = Html;
        return Task.Run(() => ComposeHtml.ToText(html));
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

    /// <summary>
    /// Arquivo solto no editor: o WebView entrega o conteúdo, não o caminho. Fica numa pasta do rascunho
    /// (apagada ao enviar ou descartar), para continuar disponível se o rascunho for reaberto.
    /// </summary>
    public async Task AddFileContentAsync(string fileName, byte[] data)
    {
        var dir = Path.Combine(_services.Paths.DataDirectory, "drafts", _draftId.ToString("N"));
        Directory.CreateDirectory(dir);
        var safeName = string.Concat(Path.GetFileName(fileName).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(dir, $"{Guid.NewGuid():N}-{safeName}");
        await File.WriteAllBytesAsync(path, data);
        Attachments.Add(new ComposeAttachmentViewModel(new OutgoingAttachment(safeName, data.Length, FilePath: path), this));
    }

    public void ShowTooLarge(string fileName) => Error = string.Format(Strings.Compose_FileTooLarge, fileName);

    /// <summary>Sugestões para o campo de destinatários (endereços já vistos).</summary>
    public async Task<IEnumerable<object>> SuggestAsync(string? text, CancellationToken ct)
    {
        // Sugere a partir do último destinatário digitado.
        var token = (text ?? "").Split(',', ';').Last().Trim();
        if (token.Length < 2)
            return [];
        try
        {
            var found = await _services.Mailbox.SearchAddressesAsync(token, 8, ct);
            return found.Select(a => (object)a.ToString());
        }
        catch (OperationCanceledException)
        {
            // Normal: o usuário digitou mais uma letra e a busca anterior foi cancelada.
            return [];
        }
        catch (Exception ex)
        {
            _services.Logger.LogDebug(ex, "Falha ao sugerir destinatários");
            return [];
        }
    }

    // Envio -----------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task SendAsync()
    {
        Error = null;
        if (SelectedFrom is null || IsSending)
            return;
        await FlushAsync();
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

        IsSending = true;
        var text = await CurrentTextAsync();
        var html = IsPlainText ? null : ComposeHtml.Document(Html);
        var message = new OutgoingMessage(SelectedFrom.Account.Id, to, cc, bcc, Subject.Trim(), text, html,
            Attachments.Select(a => a.Attachment).ToList(), _inReplyTo, _references);
        var draft = Snapshot(text);
        var provider = SelectedFrom.Account.Provider;

        // O envio continua em segundo plano: a tela fecha na hora e o aviso informa o resultado.
        _finished = true;
        _saveTimer.Stop();
        IsSending = false;
        _services.Navigation.GoBack();
        _notify(Strings.Compose_Sending);
        _ = SendInBackgroundAsync(message, draft, provider);
    }

    private async Task SendInBackgroundAsync(OutgoingMessage message, Draft draft, MailProvider provider)
    {
        try
        {
            await Task.Run(() => _services.Sender.SendAsync(message, _services.Drafts.LoadAttachmentAsync));
            _notify(Strings.Compose_Sent);
            DeleteDraftInBackground();
        }
        catch (Exception ex)
        {
            _services.Logger.LogWarning(ex, "Falha ao enviar mensagem");
            _notify(string.Format(Strings.Compose_SendFailedKept, ErrorMessages.For(ex, provider)));
            // Nada se perde: a mensagem fica como rascunho (no aparelho e, se der, no servidor).
            try
            {
                await _services.Drafts.SaveLocalAsync(draft);
                await _services.Drafts.UploadAsync(draft);
            }
            catch (Exception saveEx)
            {
                _services.Logger.LogInformation(saveEx, "Rascunho da mensagem não enviada ficou só no aparelho");
            }
        }
    }

    // Voltar, descartar -----------------------------------------------------------------------------

    /// <summary>Voltar: o rascunho é salvo ao sair da tela (<see cref="OnIsActiveChanged"/>).</summary>
    [RelayCommand]
    private void Back() => _services.Navigation.GoBack();

    /// <summary>Descartar: pede confirmação inline se algo foi escrito ou já existe rascunho.</summary>
    [RelayCommand]
    private void Discard()
    {
        if ((IsDirty || _editorDirty || _hasLocalDraft || _server is not null) && !IsConfirmingDiscard)
        {
            IsConfirmingDiscard = true;
            return;
        }
        ConfirmDiscard();
    }

    [RelayCommand]
    private void ConfirmDiscard()
    {
        IsConfirmingDiscard = false;
        _finished = true;
        _saveTimer.Stop();
        DeleteDraftInBackground();
        _services.Navigation.GoBack();
        if (_hasLocalDraft || _server is not null)
            _notify(Strings.Compose_DraftDiscarded);
    }

    [RelayCommand]
    private void CancelDiscard() => IsConfirmingDiscard = false;

    [RelayCommand]
    private void ToggleCcBcc() => ShowCcBcc = !ShowCcBcc;

    /// <summary>Enviada ou descartada: o rascunho (local, servidor e arquivos soltos) deixa de existir.</summary>
    private void DeleteDraftInBackground()
    {
        var accountId = SelectedFrom?.Account.Id ?? Guid.Empty;
        _ = Task.Run(async () =>
        {
            // Espera um envio ao servidor em andamento terminar, para não deixar cópia para trás.
            await _uploadGate.WaitAsync();
            try
            {
                await _services.Drafts.DeleteAsync(_draftId, accountId, _server);
                var dir = Path.Combine(_services.Paths.DataDirectory, "drafts", _draftId.ToString("N"));
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                _services.Logger.LogWarning(ex, "Falha ao apagar o rascunho");
            }
            finally
            {
                _uploadGate.Release();
            }
        });
    }

    // Rascunho --------------------------------------------------------------------------------------

    private string State() =>
        string.Join('\u0001', SelectedFrom?.Account.Id, To, Cc, Bcc, Subject, IsPlainText ? Body : Html, IsPlainText,
            string.Join('\u0002', Attachments.Select(a => a.Attachment)));

    private Draft Snapshot(string text) => new(
        _draftId, SelectedFrom?.Account.Id ?? Guid.Empty, Mode, To, Cc, Bcc, Subject, Html, text, IsPlainText,
        Attachments.Select(a => a.Attachment).ToList(), _inReplyTo, _references, DateTimeOffset.Now, _server);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(To) or nameof(Cc) or nameof(Bcc) or nameof(Subject) or nameof(Body) or nameof(SelectedFrom))
            ScheduleSave();
    }

    private void ScheduleSave()
    {
        if (_loading || _finished)
            return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task FlushAsync()
    {
        if (!IsPlainText && FlushEditor is { } flush)
        {
            try
            {
                await flush();
            }
            catch (Exception ex)
            {
                _services.Logger.LogDebug(ex, "Editor não respondeu ao pedir o conteúdo");
            }
        }
    }

    /// <summary>Salva no aparelho se algo mudou; de tempos em tempos também no servidor.</summary>
    private async Task SaveLocalAsync(bool upload = false)
    {
        if (_finished || SelectedFrom is null)
            return;
        if (_editorDirty)
            await FlushAsync();
        var state = State();
        var changed = state != _savedState;
        if (changed && (state != _initialState || _hasLocalDraft))
        {
            try
            {
                await _services.Drafts.SaveLocalAsync(Snapshot(await CurrentTextAsync()));
                _savedState = state;
                _hasLocalDraft = true;
            }
            catch (Exception ex)
            {
                _services.Logger.LogWarning(ex, "Falha ao salvar o rascunho no aparelho");
                return;
            }
        }
        if (_hasLocalDraft && (upload || (changed && DateTimeOffset.Now - _lastUpload >= ServerSaveInterval)))
            await UploadAsync();
    }

    private async Task UploadAsync()
    {
        await _uploadGate.WaitAsync();
        try
        {
            if (_finished)
                return;
            _lastUpload = DateTimeOffset.Now;
            var saved = await _services.Drafts.UploadAsync(Snapshot(await CurrentTextAsync()));
            _server = saved.Server;
        }
        catch (Exception ex)
        {
            // Continua no aparelho (marcado para envio) e vai ao servidor na próxima vez.
            _services.Logger.LogInformation(ex, "Rascunho salvo só no aparelho por enquanto");
        }
        finally
        {
            _uploadGate.Release();
        }
    }

    partial void OnIsActiveChanged(bool value)
    {
        if (!value && !_finished)
            _ = LeaveAsync();
    }

    private async Task LeaveAsync()
    {
        _saveTimer.Stop();
        await FlushAsync();
        var wasSaved = _hasLocalDraft;
        var dirty = State() != _savedState;
        if (!dirty && !wasSaved)
            return;
        _notify(Strings.Compose_DraftSaved);
        await SaveLocalAsync(upload: dirty || _server is null);
    }

    private async Task ToPlainTextAsync()
    {
        if (FlushEditor is { } flush)
        {
            try
            {
                await flush();
            }
            catch (Exception ex)
            {
                _services.Logger.LogDebug(ex, "Editor não respondeu ao pedir o conteúdo");
            }
        }
        var html = Html;
        Body = await Task.Run(() => ComposeHtml.ToText(html));
    }

    partial void OnSelectedFromChanged(FromOption? value) => OnPropertyChanged(nameof(CanSend));

    partial void OnIsPlainTextChanged(bool value)
    {
        if (_loading)
            return;
        // Texto → formatado: o texto vira HTML; formatado → texto: o HTML do editor vira texto.
        if (!value)
        {
            Html = ComposeHtml.FromText(Body);
            HtmlReplaced?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _ = ToPlainTextAsync();
        }
        _services.Settings.Current.Compose.PlainText = value;
        _services.Settings.Save();
        ScheduleSave();
    }
}
