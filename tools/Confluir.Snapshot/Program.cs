using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Confluir.App;
using Confluir.App.Services;
using Confluir.App.ViewModels;
using Confluir.App.Views;
using Confluir.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

// Renderiza as telas do Confluir sem exibir janela e salva PNGs, para revisão visual.
// Uso: dotnet run --project tools/Confluir.Snapshot -- <pasta-de-saída>
var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "snapshots");
Directory.CreateDirectory(outDir);

var culture = CultureInfo.GetCultureInfo("pt-BR");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

// Pasta de dados temporária: não toca nas configurações reais do usuário.
var dataDir = Path.Combine(Path.GetTempPath(), "confluir-snapshot", Guid.NewGuid().ToString("N"));
using var host = AppHost.Build(dataDir);
var services = host.Services;

AppBuilder.Configure(() => new App(services))
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var theme = services.GetRequiredService<ThemeService>();
var vm = services.GetRequiredService<MainWindowViewModel>();
var window = services.GetRequiredService<MainWindow>();
window.Width = 1320;
window.Height = 820;
window.Show();
Pump(() => vm.Rows.Count > 0);

// Seleciona a segunda mensagem (a primeira linha é o cabeçalho "Hoje").
vm.SelectedRow = vm.Rows.OfType<MessageRow>().Skip(1).First();
Pump(() => vm.Detail is { IsLoading: false });

theme.SetTheme(ThemeMode.Light);
Save(window, "main-light.png");

theme.SetTheme(ThemeMode.Dark);
Save(window, "main-dark.png");

// Pasta com anexos, modo compacto e cor de destaque personalizada.
vm.Appearance.CompactMode = true;
theme.SetAccent("#EC4899");
theme.SetTheme(ThemeMode.Light);
var withAttachment = vm.Rows.OfType<MessageRow>().First(r => r.HasAttachments);
vm.SelectedRow = withAttachment;
Pump(() => vm.Detail is { IsLoading: false });
Save(window, "main-compact-accent.png");

theme.SetAccent(null);
vm.Appearance.CompactMode = false;

foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
{
    theme.SetTheme(mode);
    var panel = new Window
    {
        Width = 340, Height = 400,
        Content = new Border { Padding = new Thickness(16), Child = new AppearanceView { DataContext = vm.Appearance } },
    };
    panel.Show();
    Pump(() => true);
    Save(panel, $"appearance-{mode.ToString().ToLowerInvariant()}.png");
    panel.Close();
}

window.Close();
Directory.Delete(dataDir, recursive: true);

static void Pump(Func<bool> until, int timeoutMs = 5000)
{
    var deadline = Environment.TickCount64 + timeoutMs;
    do
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(15);
    } while (!until() && Environment.TickCount64 < deadline);
}

void Save(Window w, string name)
{
    // Deixa terminar as transições (troca de tema, crossfade) antes de capturar.
    var settle = Environment.TickCount64 + 450;
    Pump(() => Environment.TickCount64 > settle, 1000);
    var frame = w.CaptureRenderedFrame() ?? throw new InvalidOperationException("Sem frame renderizado");
    var path = Path.Combine(outDir, name);
    frame.Save(path, PngBitmapEncoderOptions.Default);
    Console.WriteLine(path);
}
