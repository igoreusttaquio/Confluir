using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Confluir.App.Resources;
using Confluir.App.Services;
using Confluir.Core.Models;
using Confluir.Core.Services;

namespace Confluir.App.ViewModels;

/// <summary>
/// Assinatura de uma conta, em tela própria (o editor é um WebView: numa tela com rolagem ele passaria por cima do
/// resto). Texto formatado, links e imagens (coladas ou escolhidas; vão embutidas no email).
/// </summary>
public sealed partial class SignatureEditorViewModel : ViewModelBase, INavigationPage
{
    /// <summary>Imagens maiores deixariam todo email pesado.</summary>
    public const int MaxImageBytes = 512 * 1024;

    private readonly IAccountStore _accounts;
    private readonly Action _goBack;
    private readonly Action<string> _notify;

    public SignatureEditorViewModel(MailAccount account, IAccountStore accounts, Action goBack, Action<string> notify)
    {
        Account = account;
        _accounts = accounts;
        _goBack = goBack;
        _notify = notify;
        Html = account.Signature ?? "";
        PlainText = ComposeHtml.HasContent(Html) ? ComposeHtml.ToText(Html).Trim() : "";
        OnReplies = account.SignatureOnReplies;
    }

    /// <summary>Sem WebView (ex.: Linux sem WebKitGTK) a assinatura é editada como texto simples.</summary>
    public bool IsRichTextAvailable { get; } = WebViewSupport.IsAvailable;

    public bool IsPlainOnly => !IsRichTextAvailable;

    [ObservableProperty]
    public partial string PlainText { get; set; } = "";

    public MailAccount Account { get; }

    public string Title => string.Format(Strings.Signature_TitleFor, Account.DisplayName);

    /// <summary>Conteúdo do editor (o editor entrega ao salvar).</summary>
    public string Html { get; set; }

    [ObservableProperty]
    public partial bool OnReplies { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    /// <summary>Pede ao editor o conteúdo atual antes de salvar.</summary>
    public Func<Task>? FlushEditor { get; set; }

    [RelayCommand]
    private void Back() => _goBack();

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsPlainOnly)
            Html = ComposeHtml.FromText(PlainText.Trim());
        else if (FlushEditor is { } flush)
            await flush();
        var signature = ComposeHtml.HasContent(Html) ? Html.Trim() : null;
        // A conta pode ter mudado enquanto esta tela estava aberta (nome, servidores): grava sobre a versão atual.
        var current = _accounts.Accounts.FirstOrDefault(a => a.Id == Account.Id) ?? Account;
        _accounts.Add(current with { Signature = signature, SignatureOnReplies = OnReplies });
        _notify(signature is null ? Strings.Signature_Removed : Strings.Signature_Saved);
        _goBack();
    }

    /// <summary>Imagem escolhida no disco: entra no texto como data: (o envio a transforma em anexo embutido).</summary>
    public string? ImageDataUri(string fileName, byte[] data)
    {
        if (data.Length > MaxImageBytes)
        {
            Error = string.Format(Strings.Signature_ImageTooLarge, fileName, MaxImageBytes / 1024);
            return null;
        }
        var type = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => null,
        };
        if (type is null)
        {
            Error = Strings.Signature_ImageType;
            return null;
        }
        Error = null;
        return $"data:{type};base64,{Convert.ToBase64String(data)}";
    }
}
