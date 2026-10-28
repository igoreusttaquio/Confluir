using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Confluir.App;
using Confluir.App.Services;
using Confluir.App.ViewModels;
using Confluir.App.Views;
using Confluir.Core.Rules;
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

// Modo compacto (uma linha por mensagem) com a barra lateral recolhida.
theme.SetTheme(ThemeMode.Light);
vm.Appearance.CompactMode = true;
Pump(() => true);
var sidebarColumn = window.FindControl<Grid>("Columns")!.ColumnDefinitions[0];
var widths = new List<string>();
var t0 = Environment.TickCount64;
vm.ToggleSidebarCommand.Execute(null);
for (var i = 0; i < 16; i++)
{
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    widths.Add($"{Environment.TickCount64 - t0}ms={sidebarColumn.Width.Value:0}");
    Thread.Sleep(20);
}
Console.WriteLine("animacao barra lateral: " + string.Join(" ", widths));
Save(window, "compact-collapsed.png");
vm.ToggleSidebarCommand.Execute(null);
vm.Appearance.CompactMode = false;

// Texto da lista grande.
vm.Appearance.IsListTextLarge = true;
Save(window, "list-text-large.png");
vm.Appearance.IsListTextMedium = true;

// Duas colunas: a lista ocupa a largura; a mensagem abre em tela própria.
vm.Appearance.IsTwoColumns = true;
Save(window, "two-columns.png");
vm.SelectedRow = vm.Rows.OfType<MessageRow>().First(r => r.HasAttachments);
vm.OpenMessageCommand.Execute(null);
var page = (MessagePageViewModel)vm.Navigation.CurrentPage!;
Pump(() => !page.Detail.IsLoading);
Save(window, "message-page.png");
vm.GoBackCommand.Execute(null);
vm.Appearance.IsThreeColumns = true;
vm.SelectedRow = vm.Rows.OfType<MessageRow>().First(r => r.HasAttachments);
Pump(() => vm.Detail is { IsLoading: false });

// Ações: sinalizar e depois arquivar (a mensagem sai da lista e a seguinte é selecionada).
var archived = (MessageRow)vm.SelectedRow!;
await vm.Detail!.ToggleFlagCommand.ExecuteAsync(null);
Save(window, "action-flagged.png");

// Responder: tela de composição com destinatário, assunto "Re:" e citação.
await vm.Detail!.ReplyCommand.ExecuteAsync(null);
var compose = (ComposeViewModel)vm.Navigation.CurrentPage!;
compose.ShowCcBcc = true;
Save(window, "compose-reply.png");
compose.ConfirmDiscardCommand.Execute(null);
Pump(() => vm.Navigation.IsAtRoot);
await vm.Detail!.ArchiveCommand.ExecuteAsync(null);
Pump(() => vm.Detail is { IsLoading: false });
Console.WriteLine($"arquivada saiu da lista: {vm.Rows.OfType<MessageRow>().All(r => r.Summary.Id != archived.Summary.Id)}; " +
                  $"selecionada agora: {(vm.SelectedRow as MessageRow)?.Subject}");
Save(window, "action-archived.png");
// Desfazer o arquivamento: a mensagem volta à lista.
Console.WriteLine($"aviso apos arquivar: \"{vm.StatusMessage}\" com desfazer: {vm.HasStatusUndo}");
var undoing = vm.UndoStatusCommand.ExecuteAsync(null);
Pump(() => undoing.IsCompleted && vm.Rows.OfType<MessageRow>().Any(r => r.Summary.Id == archived.Summary.Id), 5000);
Console.WriteLine($"desfazer devolveu a arquivada: {vm.Rows.OfType<MessageRow>().Any(r => r.Summary.Id == archived.Summary.Id)}");

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

// Configurações: regras (com exemplos) e fontes; editor de regra criado a partir de uma mensagem.
theme.SetTheme(ThemeMode.Light);
var ruleService = services.GetRequiredService<RuleService>();
ruleService.Save(new MailRule(Guid.NewGuid(), "Newsletters", true, null, RuleMatch.Any,
    [new RuleCondition(RuleField.Subject, RuleOperator.StartsWith, "Newsletter"), new RuleCondition(RuleField.From, RuleOperator.Contains, "news")],
    new RuleActions(true, false, RuleDestination.Archive)));
ruleService.Save(new MailRule(Guid.NewGuid(), "Faturas", true, null, RuleMatch.All,
    [new RuleCondition(RuleField.Subject, RuleOperator.Contains, "fatura")],
    new RuleActions(false, true, RuleDestination.None)));
vm.OpenSettingsCommand.Execute(null);
var settingsPage = (SettingsViewModel)vm.Navigation.CurrentPage!;
settingsPage.Section = SettingsSection.Rules;
Save(window, "settings-rules.png");
settingsPage.Section = SettingsSection.Fonts;
Save(window, "settings-fonts.png");
vm.GoBackCommand.Execute(null);
vm.SelectedRow = vm.Rows.OfType<MessageRow>().Skip(2).First();
Pump(() => vm.Detail is { IsLoading: false });
vm.CreateRule(vm.Detail!);
var ruleEditor = (RuleEditorViewModel)vm.Navigation.CurrentPage!;
ruleEditor.MarkRead = true;
ruleEditor.IsDestinationArchive = true;
Save(window, "rule-editor.png");
vm.GoBackCommand.Execute(null);

// Busca com o filtro avançado aberto.
vm.SearchText = "orcamento";
vm.IsAdvancedSearchOpen = true;
// Sem await: a busca continua no laço da interface, que só roda dentro do Pump.
var searching = vm.SearchNowCommand.ExecuteAsync(null);
Pump(() => searching.IsCompleted && !vm.IsListLoading && vm.Rows.Count > 0, 8000);
Save(window, "search.png");
vm.IsAdvancedSearchOpen = false;
var clearing = vm.ClearSearchCommand.ExecuteAsync(null);
Pump(() => clearing.IsCompleted && !vm.IsSearchActive && !vm.IsListLoading, 8000);

// Seleção de várias mensagens: barra e painel de ações em lote.
{
    var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "Messages");
    list.SelectedItems!.Clear();
    foreach (var r in vm.Rows.OfType<MessageRow>().Skip(1).Take(3))
        list.SelectedItems.Add(r);
    Pump(() => vm.HasMultipleSelection);
    Save(window, "multi-select.png");
    list.SelectedItems.Clear();
    Pump(() => !vm.HasMultipleSelection);
}

// Clique direito numa mensagem: o menu de contexto precisa abrir com as ações.
{
    var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "Messages");
    var target = vm.Rows.OfType<MessageRow>().Skip(3).First();
    list.ScrollIntoView(target);
    Pump(() => list.ContainerFromItem(target) is not null);
    var container = (Control)list.ContainerFromItem(target)!;
    var point = container.TranslatePoint(new Point(40, 10), window)!.Value;
    window.MouseDown(point, MouseButton.Right);
    window.MouseUp(point, MouseButton.Right);
    var rowMenu = list.ContextFlyout as MenuFlyout;
    Pump(() => rowMenu?.IsOpen == true, 2000);
    var headers = rowMenu?.Items.OfType<MenuItem>().Where(i => i.IsVisible).Select(i => i.Header?.ToString()) ?? [];
    Console.WriteLine($"menu de contexto aberto: {rowMenu?.IsOpen == true}; itens: {string.Join(" | ", headers)}");
    rowMenu?.Hide();
    Pump(() => rowMenu?.IsOpen != true, 1000);

}

// Atalhos de teclado (teclas de verdade na janela): Ctrl+Q, Ctrl+Shift+S; Delete digitando na busca não apaga.
{
    var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "Messages");
    var row = vm.Rows.OfType<MessageRow>().Skip(5).First();
    list.SelectedItem = row;
    list.Focus();
    Pump(() => vm.SelectedRow == row);
    var (read, flagged) = (row.IsRead, row.IsFlagged);
    window.KeyPress(Key.Q, RawInputModifiers.Control, PhysicalKey.Q, "q");
    window.KeyPress(Key.S, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.S, "S");
    Pump(() => row.IsRead != read && row.IsFlagged != flagged, 1500);
    Console.WriteLine($"atalhos: Ctrl+Q alternou lida: {row.IsRead != read}; Ctrl+Shift+S alternou sinalizada: {row.IsFlagged != flagged}");
    Console.WriteLine($"aviso: \"{vm.StatusMessage}\" com desfazer: {vm.HasStatusUndo}");
    Save(window, "feedback-toast.png");
    await vm.UndoStatusCommand.ExecuteAsync(null);
    Console.WriteLine($"desfazer voltou a sinalização: {row.IsFlagged == flagged}");
    window.GetVisualDescendants().OfType<MessageListView>().First().FocusSearch();
    Pump(() => true);
    window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
    Pump(() => true);
    Console.WriteLine($"atalhos: Delete com foco na busca manteve a mensagem: {vm.Rows.OfType<MessageRow>().Contains(row)}");
    vm.OpenSettingsCommand.Execute(null);
    ((SettingsViewModel)vm.Navigation.CurrentPage!).Section = SettingsSection.Shortcuts;
    Save(window, "settings-shortcuts.png");
    ((SettingsViewModel)vm.Navigation.CurrentPage!).Section = SettingsSection.Notifications;
    Save(window, "settings-notifications.png");
    vm.GoBackCommand.Execute(null);
}

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
