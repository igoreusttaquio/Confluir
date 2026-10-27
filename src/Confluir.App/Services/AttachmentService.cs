using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Confluir.Core;
using Confluir.Core.Models;
using Confluir.Core.Services;
using Microsoft.Extensions.Logging;

namespace Confluir.App.Services;

/// <summary>Salvar e abrir anexos, baixando o conteúdo do servidor sob demanda.</summary>
public sealed class AttachmentService(IMailboxService mailbox, AppPaths paths, ILogger<AttachmentService> logger)
{
    // Tipos que executam código ao abrir: só podem ser salvos (o usuário decide o que fazer com eles).
    private static readonly HashSet<string> Executable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx", ".bat", ".cmd", ".com", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".scr", ".pif", ".lnk", ".url", ".hta", ".cpl", ".msc", ".jar", ".reg", ".dll", ".sys",
        ".app", ".sh", ".command", ".scpt", ".iso", ".img", ".vhd",
    };

    private string TempRoot => Path.Combine(paths.DataDirectory, "temp", "anexos");

    public static bool CanOpen(string fileName) => !Executable.Contains(Path.GetExtension(fileName));

    /// <returns>Falso se o usuário cancelou a escolha do arquivo.</returns>
    public async Task<bool> SaveAsync(Guid messageId, AttachmentInfo attachment, CancellationToken ct = default)
    {
        if (Window is not { } window)
            return false;
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = SafeFileName(attachment.FileName),
            ShowOverwritePrompt = true,
        });
        if (file is null)
            return false;

        var data = await mailbox.GetAttachmentAsync(messageId, attachment.Index, ct);
        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(data, ct);
        return true;
    }

    /// <summary>Grava numa pasta temporária do app e abre com o programa padrão do sistema.</summary>
    public async Task OpenAsync(Guid messageId, AttachmentInfo attachment, CancellationToken ct = default)
    {
        if (!CanOpen(attachment.FileName))
            throw new InvalidOperationException("Tipo de arquivo executável: só pode ser salvo.");
        if (Window is not { } window)
            return;

        CleanupOldFiles();
        var dir = Path.Combine(TempRoot, messageId.ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SafeFileName(attachment.FileName));
        await File.WriteAllBytesAsync(path, await mailbox.GetAttachmentAsync(messageId, attachment.Index, ct), ct);
        await window.Launcher.LaunchFileInfoAsync(new FileInfo(path));
    }

    /// <summary>Nome sem caminhos nem caracteres inválidos (o nome vem do remetente e não é confiável).</summary>
    public static string SafeFileName(string name)
    {
        var file = Path.GetFileName(name.Replace('\\', '/').Split('/').Last());
        foreach (var c in Path.GetInvalidFileNameChars())
            file = file.Replace(c, '_');
        file = file.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(file) ? "anexo" : file;
    }

    // Arquivos temporários com mais de um dia são apagados.
    private void CleanupOldFiles()
    {
        try
        {
            if (!Directory.Exists(TempRoot))
                return;
            foreach (var dir in new DirectoryInfo(TempRoot).EnumerateDirectories())
            {
                if (dir.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))
                    dir.Delete(recursive: true);
            }
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Não foi possível limpar anexos temporários");
        }
    }

    private static Window? Window =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
