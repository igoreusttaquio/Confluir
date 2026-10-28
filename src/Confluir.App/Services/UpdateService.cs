using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Confluir.App.Resources;
using Confluir.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Confluir.App.Services;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Installing,
    Failed,
}

/// <param name="InstallerUrl">Nulo: não há instalador deste sistema na versão (só a página).</param>
public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? InstallerName, string? InstallerUrl,
    string? ChecksumsUrl);

/// <summary>
/// Atualizações pelo GitHub: procura a última versão publicada (ao abrir e a cada 12 horas, se ligado em
/// Configurações), e "Atualizar" baixa o instalador deste sistema, confere o SHA-256 e instala:
/// Windows roda o instalador em modo silencioso (ele reabre o app), Linux instala o .deb com pkexec e macOS troca o
/// Confluir.app pelo da imagem. Sem como instalar sozinho, abre o arquivo ou a página da versão.
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    public const string Repository = "igoreusttaquio/Confluir";
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

    private readonly ISettingsStore _settings;
    private readonly IUrlLauncher _launcher;
    private readonly Desktop.DesktopIntegration _desktop;
    private readonly ILogger<UpdateService> _logger;
    private readonly bool _enabled;
    private readonly HttpClient _http;
    private DispatcherTimer? _timer;

    public UpdateService(ISettingsStore settings, IUrlLauncher launcher, Desktop.DesktopIntegration desktop,
        ILogger<UpdateService> logger, bool enabled = true)
    {
        _settings = settings;
        _launcher = launcher;
        _desktop = desktop;
        _logger = logger;
        _enabled = enabled;
        // Cliente próprio: o instalador tem dezenas de MB (o compartilhado desiste em 15 s).
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Confluir", CurrentVersion.ToString(3)));
    }

    public static Version CurrentVersion { get; } = ParseVersion(
        typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion) ?? new Version(0, 0, 0);

    public string CurrentVersionText => CurrentVersion.ToString(3);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable), nameof(IsBusy), nameof(StatusText), nameof(CanInstall), nameof(IsDownloading), nameof(IsSpinning))]
    public partial UpdateState State { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableVersionText), nameof(StatusText), nameof(CanInstall))]
    public partial UpdateInfo? Available { get; private set; }

    /// <summary>Download, de 0 a 100.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Error { get; private set; }

    /// <summary>Há versão nova (o botão "Atualizar" aparece), inclusive enquanto baixa.</summary>
    public bool IsAvailable => Available is not null && State is UpdateState.Available or UpdateState.Downloading
        or UpdateState.Installing or UpdateState.Failed;

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing;

    public bool IsDownloading => State == UpdateState.Downloading;

    // O ícone dos botões gira enquanto trabalha, e pelo menos um instante depois do clique (uma busca rápida
    // terminaria antes de dar para ver).
    private static readonly TimeSpan MinimumSpin = TimeSpan.FromSeconds(1.2);
    private bool _holdSpin;

    public bool IsSpinning => IsBusy || _holdSpin;

    private void Spin()
    {
        _holdSpin = true;
        OnPropertyChanged(nameof(IsSpinning));
        DispatcherTimer.RunOnce(() =>
        {
            _holdSpin = false;
            OnPropertyChanged(nameof(IsSpinning));
        }, MinimumSpin);
    }

    public bool CanInstall => Available is not null && State is UpdateState.Available or UpdateState.Failed;

    public string? AvailableVersionText => Available?.Version.ToString(3);

    public string StatusText => State switch
    {
        UpdateState.Checking => Strings.Update_Checking,
        UpdateState.UpToDate => string.Format(Strings.Update_UpToDate, CurrentVersionText),
        UpdateState.Available => string.Format(Strings.Update_Available, AvailableVersionText),
        UpdateState.Downloading => string.Format(Strings.Update_Downloading, Progress),
        UpdateState.Installing => Strings.Update_Installing,
        UpdateState.Failed => Error ?? Strings.Update_Failed,
        _ => string.Format(Strings.Update_Current, CurrentVersionText),
    };

    public bool AutoCheck
    {
        get => _settings.Current.Updates.AutoCheck;
        set
        {
            _settings.Current.Updates.AutoCheck = value;
            _settings.Save();
            OnPropertyChanged();
            if (value)
                StartAutoCheck();
            else
                _timer?.Stop();
        }
    }

    /// <summary>Primeira busca alguns segundos depois de abrir, depois a cada 12 horas.</summary>
    public void StartAutoCheck()
    {
        if (!_enabled || !AutoCheck)
            return;
        _timer ??= new DispatcherTimer();
        _timer.Stop();
        _timer.Interval = FirstCheckDelay;
        _timer.Tick -= OnTimer;
        _timer.Tick += OnTimer;
        _timer.Start();
    }

    private void OnTimer(object? sender, EventArgs e)
    {
        _timer!.Interval = CheckInterval;
        if (!IsBusy && State != UpdateState.Available)
            _ = CheckAsync(quiet: true);
    }

    [RelayCommand]
    private Task Check()
    {
        Spin();
        return CheckAsync(quiet: false);
    }

    /// <param name="quiet">Busca automática: falha de rede não aparece como erro.</param>
    public async Task CheckAsync(bool quiet)
    {
        if (IsBusy)
            return;
        var previous = State;
        State = UpdateState.Checking;
        Error = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = await _http.SendAsync(request, cts.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Nenhuma versão publicada ainda.
                Available = null;
                State = UpdateState.UpToDate;
                return;
            }
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var info = ParseRelease(json, RuntimeAsset());
            Available = info is not null && info.Version > CurrentVersion ? info : null;
            State = Available is null ? UpdateState.UpToDate : UpdateState.Available;
            if (Available is not null)
                _logger.LogInformation("Versão nova disponível: {Version}", Available.Version);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Não foi possível procurar atualizações");
            if (quiet)
            {
                State = previous == UpdateState.Checking ? UpdateState.Idle : previous;
                return;
            }
            Error = Strings.Update_CheckFailed;
            State = UpdateState.Failed;
        }
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        if (Available is { } info)
            _ = _launcher.OpenAsync(info.PageUrl);
        else
            _ = _launcher.OpenAsync($"https://github.com/{Repository}/releases/latest");
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Available is not { } info || IsBusy)
            return;
        Spin();
        if (info.InstallerUrl is null)
        {
            _ = _launcher.OpenAsync(info.PageUrl);
            return;
        }

        Error = null;
        Progress = 0;
        State = UpdateState.Downloading;
        try
        {
            var file = await DownloadAsync(info);
            State = UpdateState.Installing;
            if (!await LaunchInstallerAsync(file))
                State = UpdateState.Available;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao atualizar para {Version}", info.Version);
            Error = ex is InvalidDataException ? Strings.Update_ChecksumFailed : Strings.Update_Failed;
            State = UpdateState.Failed;
        }
    }

    private async Task<string> DownloadAsync(UpdateInfo info)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Confluir-update", info.Tag);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, info.InstallerName!);
        var partial = path + ".part";

        using (var response = await _http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = File.Create(partial);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            var lastReport = 0.0;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                var percent = total > 0 ? Math.Floor(done * 100.0 / total) : 0;
                if (percent != lastReport)
                {
                    lastReport = percent;
                    Dispatcher.UIThread.Post(() => Progress = percent);
                }
            }
        }

        if (info.ChecksumsUrl is not null)
        {
            var sums = await _http.GetStringAsync(info.ChecksumsUrl);
            var expected = ChecksumFor(sums, info.InstallerName!);
            if (expected is not null)
            {
                await using var stream = File.OpenRead(partial);
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(partial);
                    throw new InvalidDataException($"SHA-256 não confere: {info.InstallerName}");
                }
            }
        }
        File.Move(partial, path, overwrite: true);
        return path;
    }

    /// <summary>Instala; verdadeiro se o app vai fechar para terminar.</summary>
    private async Task<bool> LaunchInstallerAsync(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            // O instalador só começa depois que este processo terminar (senão os arquivos estão em uso): um
            // PowerShell oculto espera o Confluir sair (até 30 s, depois força) e roda o instalador em modo
            // silencioso, com barra de progresso; /RELAUNCH faz o instalador reabrir o Confluir no fim.
            var script = WindowsInstallScript(Environment.ProcessId, file);
            Process.Start(new ProcessStartInfo("powershell.exe",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
                 "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            ExitForUpdate();
            return true;
        }
        if (OperatingSystem.IsLinux())
            return await InstallDebAsync(file);
        if (OperatingSystem.IsMacOS())
            return InstallMacApp(file);
        return false;
    }

    private async Task<bool> InstallDebAsync(string file)
    {
        var installed = Environment.ProcessPath is { } exe && exe.StartsWith("/opt/confluir/", StringComparison.Ordinal);
        if (installed)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo("pkexec", ["dpkg", "-i", file]) { UseShellExecute = false });
                if (process is not null)
                {
                    await process.WaitForExitAsync();
                    if (process.ExitCode == 0)
                    {
                        Process.Start(new ProcessStartInfo("/opt/confluir/Confluir") { UseShellExecute = false });
                        ExitForUpdate();
                        return true;
                    }
                    _logger.LogInformation("pkexec dpkg terminou com código {Code}", process.ExitCode);
                    // 126: a pessoa cancelou a senha. Fica a opção de tentar de novo.
                    if (process.ExitCode == 126)
                        return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "pkexec indisponível; abrindo o pacote");
            }
        }
        // Sem pkexec (ou fora do pacote): o instalador de programas do sistema abre o .deb.
        await OpenFileAsync(file);
        Error = Strings.Update_OpenedPackage;
        State = UpdateState.Failed;
        return true;
    }

    private bool InstallMacApp(string dmg)
    {
        // .../Confluir.app/Contents/MacOS/Confluir → .../Confluir.app
        var exe = Environment.ProcessPath ?? "";
        var marker = exe.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal);
        if (marker < 0)
        {
            _ = OpenFileAsync(dmg);
            Error = Strings.Update_OpenedPackage;
            State = UpdateState.Failed;
            return true;
        }
        var bundle = exe[..(marker + 4)];
        // Um script espera o app fechar, troca o pacote pelo da imagem (voltando o antigo se der errado) e reabre.
        var script = Path.Combine(Path.GetDirectoryName(dmg)!, "install.sh");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            while kill -0 {{Environment.ProcessId}} 2>/dev/null; do sleep 0.5; done
            MNT="$(mktemp -d)"
            hdiutil attach -nobrowse -readonly -mountpoint "$MNT" {{Quote(dmg)}} || { open {{Quote(bundle)}}; exit 1; }
            rm -rf {{Quote(bundle + ".old")}}
            if mv {{Quote(bundle)}} {{Quote(bundle + ".old")}} && ditto "$MNT/Confluir.app" {{Quote(bundle)}}; then
              rm -rf {{Quote(bundle + ".old")}}
            else
              rm -rf {{Quote(bundle)}}; mv {{Quote(bundle + ".old")}} {{Quote(bundle)}}
            fi
            hdiutil detach "$MNT" -quiet
            open {{Quote(bundle)}}
            """.Replace("\r\n", "\n"));
        Process.Start(new ProcessStartInfo("/bin/sh", [script]) { UseShellExecute = false });
        ExitForUpdate();
        return true;
    }

    private static async Task OpenFileAsync(string path)
    {
        var window = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window is not null)
            await window.Launcher.LaunchFileInfoAsync(new FileInfo(path));
    }

    /// <summary>
    /// Fecha o app para o instalador poder trocar os arquivos. Se em alguns segundos o processo ainda estiver de pé,
    /// força o fim (o instalador espera por isso).
    /// </summary>
    private void ExitForUpdate()
    {
        _logger.LogInformation("Fechando para atualizar");
        _ = Task.Delay(TimeSpan.FromSeconds(12)).ContinueWith(_ => Environment.Exit(0), TaskScheduler.Default);
        _desktop.Exit();
    }

    /// <summary>
    /// Script do PowerShell que espera o processo <paramref name="processId"/> sair (até 30 s; depois força) e roda
    /// o instalador em modo silencioso.
    /// </summary>
    public static string WindowsInstallScript(int processId, string installer) => $"""
        $p = Get-Process -Id {processId} -ErrorAction SilentlyContinue
        if ($p) {"{"} if (-not $p.WaitForExit(30000)) {"{"} Stop-Process -Id {processId} -Force -ErrorAction SilentlyContinue {"}"} {"}"}
        Start-Process -FilePath {PowerShellQuote(installer)} -ArgumentList '/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1'
        """;

    private static string PowerShellQuote(string text) => "'" + text.Replace("'", "''") + "'";

    private static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";

    // Partes puras (testadas) ------------------------------------------------------------------------

    /// <summary>"v1.2.3", "1.2.3-beta.1" ou "1.2.3+abc" → 1.2.3.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (!Version.TryParse(core, out var version))
            return null;
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    /// <summary>Instalador publicado para este sistema (nulo se não houver).</summary>
    public static string? RuntimeAsset() =>
        AssetFor(OperatingSystem.IsWindows() ? OSPlatform.Windows
            : OperatingSystem.IsMacOS() ? OSPlatform.OSX
            : OperatingSystem.IsLinux() ? OSPlatform.Linux
            : OSPlatform.FreeBSD, RuntimeInformation.ProcessArchitecture);

    public static string? AssetFor(OSPlatform os, Architecture arch)
    {
        if (os == OSPlatform.Windows)
            return arch == Architecture.X64 ? "Confluir-Setup-x64.exe" : null;
        if (os == OSPlatform.Linux)
            return arch == Architecture.X64 ? "confluir_amd64.deb" : null;
        if (os == OSPlatform.OSX)
            return arch == Architecture.Arm64 ? "Confluir-macOS-arm64.dmg" : "Confluir-macOS-x64.dmg";
        return null;
    }

    /// <summary>Lê a resposta de <c>releases/latest</c> da API do GitHub.</summary>
    public static UpdateInfo? ParseRelease(string json, string? assetName)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
            root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
            return null;
        var tag = root.GetProperty("tag_name").GetString();
        if (ParseVersion(tag) is not { } version)
            return null;
        var page = root.TryGetProperty("html_url", out var url) ? url.GetString() : null;

        string? installer = null, checksums = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var download = asset.GetProperty("browser_download_url").GetString();
                if (assetName is not null && string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
                    installer = download;
                else if (string.Equals(name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                    checksums = download;
            }
        }
        return new UpdateInfo(version, tag!, page ?? $"https://github.com/{Repository}/releases/tag/{tag}",
            installer is null ? null : assetName, installer, checksums);
    }

    /// <summary>Hash do arquivo numa lista no formato do <c>sha256sum</c> ("hash  nome").</summary>
    public static string? ChecksumFor(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && string.Equals(parts[1].TrimStart('*'), fileName, StringComparison.OrdinalIgnoreCase))
                return parts[0];
        }
        return null;
    }

    /// <summary>Só para as capturas de tela: finge uma versão nova.</summary>
    public void ShowPreview(UpdateInfo info)
    {
        Available = info;
        State = UpdateState.Available;
    }
}
