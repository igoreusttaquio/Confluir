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

// Pasta de dados temporária: não toca nas configurações reais do usuário.
var dataDir = Path.Combine(Path.GetTempPath(), "confluir-snapshot", Guid.NewGuid().ToString("N"));
using var host = AppHost.Build(dataDir, useFakeData: true);
var services = host.Services;

// Capturas em pt-BR por padrão, independente do idioma da máquina.
services.GetRequiredService<ISettingsStore>().Current.Appearance.Language = "pt-BR";

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

// Troca de idioma ao vivo (pelo seletor de Aparência), com a mensagem aberta.
foreach (var (code, name) in new[] { ("en", "en"), ("es", "es") })
{
    vm.Appearance.SelectedLanguage = vm.Appearance.LanguageOptions.First(o => o.Code == code);
    Save(window, $"main-{name}.png");
}
vm.Appearance.SelectedLanguage = vm.Appearance.LanguageOptions.First(o => o.Code == "pt-BR");

// Confirmação inline de remoção de conta.
theme.SetTheme(ThemeMode.Light);
var accountNode = vm.SidebarNodes.OfType<AccountNode>().Skip(1).First();
accountNode.AskRemoveCommand.Execute(null);
Save(window, "remove-account-confirm.png");
accountNode.CancelRemoveCommand.Execute(null);

// Tela "Adicionar conta" (navegação dentro da janela): Gmail detectado.
vm.AddAccountCommand.Execute(null);
var gmail = (AccountEditorViewModel)vm.Navigation.CurrentPage!;
gmail.Email = "voce@gmail.com";
Pump(() => gmail.DetectionMessage is not null && !gmail.IsDetecting);
Save(window, "add-account-gmail.png");
vm.GoBackCommand.Execute(null);

// Outlook sem ID do Entra ID configurado.
vm.AddAccountCommand.Execute(null);
var outlook = (AccountEditorViewModel)vm.Navigation.CurrentPage!;
outlook.Email = "ana@hotmail.com";
Pump(() => outlook.DetectionMessage is not null && !outlook.IsDetecting);
Save(window, "add-account-outlook.png");
vm.GoBackCommand.Execute(null);

// Edição de conta existente.
vm.SidebarNodes.OfType<AccountNode>().First().EditCommand.Execute(null);
var editor = (AccountEditorViewModel)vm.Navigation.CurrentPage!;
editor.AskRemoveCommand.Execute(null);
Save(window, "edit-account.png");
vm.GoBackCommand.Execute(null);

// Domínio desconhecido, tema escuro, com resultado de teste.
theme.SetTheme(ThemeMode.Dark);
vm.AddAccountCommand.Execute(null);
var custom = (AccountEditorViewModel)vm.Navigation.CurrentPage!;
custom.Email = "joana@exemplo-inexistente.dev";
custom.Password = "senha";
Pump(() => custom.ShowAdvanced && !custom.IsDetecting, 12000);
custom.ImapStatus.Set(TestState.Ok, "Conectado");
custom.SmtpStatus.Set(TestState.Failed, Confluir.App.Resources.Strings.Error_ServerUnreachable);
Save(window, "add-account-custom-dark.png");
vm.GoBackCommand.Execute(null);

window.Close();

// Primeiro uso, sem contas (serviço IMAP real, pasta de dados vazia).
theme.SetTheme(ThemeMode.Light);
var emptyDir = Path.Combine(dataDir, "vazio");
using (var emptyHost = AppHost.Build(emptyDir))
{
    var emptyVm = emptyHost.Services.GetRequiredService<MainWindowViewModel>();
    var emptyWindow = emptyHost.Services.GetRequiredService<MainWindow>();
    emptyWindow.Width = 1320;
    emptyWindow.Height = 820;
    emptyWindow.Show();
    Pump(() => !emptyVm.HasAccounts);
    Save(emptyWindow, "no-accounts.png");
    emptyWindow.Close();
}

// O pool do SQLite mantém o arquivo do banco aberto até ser limpo.
Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
try
{
    Directory.Delete(dataDir, recursive: true);
}
catch (IOException)
{
    // Pasta temporária: se algo ainda estiver aberto, o sistema limpa depois.
}

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

void Save(Window w, string name, int settleMs = 450)
{
    // Por padrão deixa terminar as transições (troca de tema, navegação) antes de capturar.
    var settle = Environment.TickCount64 + settleMs;
    Pump(() => Environment.TickCount64 > settle, 1000);
    var frame = w.CaptureRenderedFrame() ?? throw new InvalidOperationException("Sem frame renderizado");
    var path = Path.Combine(outDir, name);
    frame.Save(path, PngBitmapEncoderOptions.Default);
    Console.WriteLine(path);
}
