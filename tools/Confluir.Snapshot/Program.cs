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
// Cliente do Google de mentira (só neste processo), para a tela de conta mostrar "Entrar com o Google".
Environment.SetEnvironmentVariable("CONFLUIR_Google__ClientId", "snapshot.apps.googleusercontent.com");
Environment.SetEnvironmentVariable("CONFLUIR_Google__ClientSecret", "snapshot");
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
var openedAt = Environment.TickCount64 + 600;
Pump(() => Environment.TickCount64 > openedAt, 1000);

// Recolher e abrir a primeira conta com clique de verdade: as pastas encolhem e crescem em vez de sumir de uma vez.
{
    var nodesList = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "Nodes");
    var account = vm.SidebarNodes.OfType<AccountNode>().First();
    var firstFolder = account.Folders[0];
    var header = nodesList.GetVisualDescendants().OfType<Button>()
        .First(b => b.Classes.Contains("accountHeader") && b.DataContext == account);
    var headerPoint = header.TranslatePoint(new Point(40, header.Bounds.Height / 2), window)!.Value;
    var slowMotion = typeof(SidebarView).GetField("SlowMotion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    // Registra a 1a pasta a cada passada de layout (o que vai para a tela), não só nas capturas: um quadro
    // com a linha cheia ao abrir, ou vazia no começo do recolher, é a piscada.
    var trace = new List<(double Height, double Opacity)>();
    void OnLayout(object? s, EventArgs e)
    {
        if (nodesList.ContainerFromItem(firstFolder) is Control row && row.IsVisible)
            trace.Add((row.Bounds.Height, row.Opacity));
    }
    nodesList.LayoutUpdated += OnLayout;
    string Run(Func<bool> done)
    {
        trace.Clear();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseDown(headerPoint, MouseButton.Left);
        window.MouseUp(headerPoint, MouseButton.Left);
        var until = Environment.TickCount64 + 3000;
        Pump(() => done() && Environment.TickCount64 > until - 2000, 4000);
        var distinct = trace.Where((v, i) => i == 0 || v != trace[i - 1]).ToList();
        return string.Join(" ", distinct.Select(v => $"{v.Height:0}/{v.Opacity:0.00}"));
    }
    static bool Monotonic(IEnumerable<double> values, bool rising)
    {
        var list = values.ToList();
        return list.Zip(list.Skip(1)).All(p => rising ? p.Second >= p.First - 0.01 : p.Second <= p.First + 0.01);
    }
    var full = (nodesList.ContainerFromItem(firstFolder) as Control)!.Bounds.Height;
    foreach (var speed in new[] { 1.0, 6.0 })
    {
        slowMotion.SetValue(null, speed);
        var collapse = Run(() => !vm.SidebarNodes.Contains(firstFolder));
        var collapseTrace = trace.ToList();
        Console.WriteLine($"conta recolhendo (x{speed}): {collapse}");
        Console.WriteLine($"  só encolhe: {Monotonic(collapseTrace.Select(v => v.Height), false) && Monotonic(collapseTrace.Select(v => v.Opacity), false)}; recolhida {!account.IsExpanded}");
        var expand = Run(() => account.IsExpanded);
        var expandTrace = trace.ToList();
        Console.WriteLine($"conta abrindo (x{speed}): {expand}");
        Console.WriteLine($"  só cresce: {Monotonic(expandTrace.Select(v => v.Height), true) && Monotonic(expandTrace.Select(v => v.Opacity), true)}; " +
                          $"primeiro quadro {(expandTrace.Count > 0 ? $"{expandTrace[0].Height:0}/{expandTrace[0].Opacity:0.00}" : "-")} (cheia {full:0}); termina {expandTrace.LastOrDefault().Height:0}/{expandTrace.LastOrDefault().Opacity:0.00}");
    }
    nodesList.LayoutUpdated -= OnLayout;
    slowMotion.SetValue(null, 1.0);
}
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
Console.WriteLine($"Gmail: escolha {gmail.CanChooseAuth}, entrar com o Google {gmail.UseOAuth}, botão \"{gmail.OAuthSignInText}\"");
gmail.UsePassword = true;
Pump(() => true);
Save(window, "add-account-gmail-password.png");
Console.WriteLine($"Gmail com senha de app: pede senha {!gmail.RequiresOAuth}, dica {gmail.HasAppPasswordHint}");
vm.GoBackCommand.Execute(null);

// Assinaturas: mensagem nova com a da conta; trocar "De" troca a assinatura; tela de edição.
{
    var withSignatures = vm.AccountNodes.Select(n => n.Account).ToList();
    withSignatures[0] = withSignatures[0] with { Signature = "<div><b>Você Silva</b></div><div>Pessoal</div>" };
    withSignatures[1] = withSignatures[1] with { Signature = "<div><b>Você Silva</b> · Engenharia</div><div>Empresa Exemplo</div>" };
    var sigCompose = new ComposeViewModel(services.GetRequiredService<ComposeServices>(), withSignatures, withSignatures[0].Id, _ => { });
    Console.WriteLine($"assinatura na nova: {sigCompose.Html.Contains("Pessoal")}, texto: {sigCompose.Body.Contains("-- \nVocê Silva")}");
    sigCompose.SelectedFrom = sigCompose.FromOptions[1];
    // Sem WebView (headless) a escrita fica em texto simples: a troca acontece no texto.
    Pump(() => sigCompose.Body.Contains("Engenharia"), 2000);
    Console.WriteLine($"trocou De (texto {sigCompose.IsPlainText}): nova {sigCompose.Body.Contains("Engenharia")}, antiga saiu {!sigCompose.Body.Contains("\nPessoal")}");
    sigCompose.SelectedFrom = sigCompose.FromOptions[2];
    Pump(() => !sigCompose.Body.Contains("Engenharia"), 2000);
    sigCompose.SelectedFrom = sigCompose.FromOptions[0];
    Pump(() => sigCompose.Body.Contains("Pessoal"), 2000);
    Console.WriteLine($"sem assinatura e de volta: {sigCompose.Body.Contains("Pessoal")} (texto: {sigCompose.Body.Replace("\n", "⏎")})");
    vm.EditSignature(withSignatures[1]);
    Pump(() => vm.Navigation.CurrentPage is SignatureEditorViewModel);
    Save(window, "signature-editor.png");
    vm.GoBackCommand.Execute(null);
    vm.OpenSettingsCommand.Execute(null);
    ((SettingsViewModel)vm.Navigation.CurrentPage!).Section = SettingsSection.Compose;
    Save(window, "settings-signatures.png");
    vm.GoBackCommand.Execute(null);
}

// Sobre: versão, links e créditos.
vm.OpenAboutCommand.Execute(null);
Pump(() => vm.Navigation.CurrentPage is AboutViewModel);
Save(window, "about.png");
Console.WriteLine($"sobre: {AboutViewModel.Credits.Sum(g => g.Items.Count)} créditos em {AboutViewModel.Credits.Count} grupos");
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
var firstPage = vm.Rows.OfType<MessageRow>().Count();
var more = vm.LoadMoreResultsCommand.ExecuteAsync(null);
Pump(() => more.IsCompleted && !vm.IsListLoading, 8000);
Console.WriteLine($"busca: {firstPage} resultados, mais disponiveis: {vm.CanLoadMoreResults}; depois de Mostrar mais: {vm.Rows.OfType<MessageRow>().Count()}");
var oneAccount = vm.SearchAccounts.Skip(1).First();
vm.SearchAccount = oneAccount;
var byAccount = vm.SearchNowCommand.ExecuteAsync(null);
Pump(() => byAccount.IsCompleted && !vm.IsListLoading, 8000);
Console.WriteLine($"busca so na conta {oneAccount.Label}: todos dela: {vm.Rows.OfType<MessageRow>().All(r => r.Account.Id == oneAccount.Account!.Id)}");
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
    {
        // Atalho editável: Alterar + teclas reais pela janela; depois um conflito e a troca.
        var settingsVm = (SettingsViewModel)vm.Navigation.CurrentPage!;
        var toggleRead = settingsVm.Shortcuts.First(s => s.Info.Action == ShortcutAction.ToggleRead);
        toggleRead.ChangeCommand.Execute(null);
        Pump(() => true);
        window.KeyPress(Key.U, RawInputModifiers.Control, PhysicalKey.U, "u");
        Pump(() => true);
        Console.WriteLine($"atalho gravado: Marcar lida = {toggleRead.Keys}; ainda em Configurações: {vm.Navigation.CurrentPage is SettingsViewModel}");
        var archive = settingsVm.Shortcuts.First(s => s.Info.Action == ShortcutAction.Archive);
        archive.ChangeCommand.Execute(null);
        window.KeyPress(Key.U, RawInputModifiers.Control, PhysicalKey.U, "u");
        Pump(() => true);
        Console.WriteLine($"conflito: \"{archive.Message}\" troca: {archive.CanSwap}");
        Save(window, "settings-shortcuts-conflict.png");
        archive.CancelCommand.Execute(null);
        vm.GoBackCommand.Execute(null);
        Pump(() => vm.Navigation.IsAtRoot);
        var current = vm.Rows.OfType<MessageRow>().First(r => r.Summary.Id == row.Summary.Id);
        vm.SelectedRow = current;
        var freshList = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "Messages");
        Pump(() => freshList.ContainerFromItem(current) is not null);
        freshList.ContainerFromItem(current)?.Focus();
        Pump(() => vm.SelectedRow == current);
        var readBefore = current.IsRead;
        window.KeyPress(Key.U, RawInputModifiers.Control, PhysicalKey.U, "u");
        Pump(() => vm.Rows.OfType<MessageRow>().First(r => r.Summary.Id == row.Summary.Id).IsRead != readBefore, 1500);
        Console.WriteLine($"Ctrl+U (novo atalho) alternou lida: {vm.Rows.OfType<MessageRow>().First(r => r.Summary.Id == row.Summary.Id).IsRead != readBefore}");
        vm.OpenSettingsCommand.Execute(null);
        ((SettingsViewModel)vm.Navigation.CurrentPage!).ResetAllShortcutsCommand.Execute(null);
        Console.WriteLine($"restaurar todos: Marcar lida = {toggleRead.Keys}");
    }
    ((SettingsViewModel)vm.Navigation.CurrentPage!).Section = SettingsSection.Notifications;
    Save(window, "settings-notifications.png");
    {
        // Atualização disponível: botão na barra lateral e cartão em Avançado.
        var updates = services.GetRequiredService<UpdateService>();
        updates.ShowPreview(new UpdateInfo(new Version(9, 9, 0), "v9.9.0", "https://example.com", "x.exe", "https://example.com/x.exe", null));
        ((SettingsViewModel)vm.Navigation.CurrentPage!).Section = SettingsSection.Advanced;
        Save(window, "settings-updates.png");
        Console.WriteLine($"atualização: \"{updates.StatusText}\", botão visível: {updates.IsAvailable}, pode instalar: {updates.CanInstall}");
        // Clicar faz o ícone girar (por pelo menos um instante), e ele para depois.
        var spinIcon = window.GetVisualDescendants().OfType<Lucide.Avalonia.LucideIcon>()
            .First(i => i.Kind == Lucide.Avalonia.LucideIconKind.RefreshCw && i.IsEffectivelyVisible && i.FindAncestorOfType<SettingsView>() is not null);
        updates.CheckCommand.Execute(null);
        Pump(() => true, 300);
        Console.WriteLine($"girando ao clicar: {updates.IsSpinning}, classe spin no ícone: {spinIcon.Classes.Contains("spin")}");
        Pump(() => !updates.IsSpinning, 25000);
        Console.WriteLine($"parou depois: {!updates.IsSpinning}, estado \"{updates.StatusText}\"");
    }
    vm.GoBackCommand.Execute(null);

    // Nova pasta pelo menu da conta (botão direito de verdade): campo inline, cria "Clientes/Acme", seleciona;
    // repetir o nome avisa.
    Pump(() => vm.Navigation.IsAtRoot);
    var folderAccount = vm.SidebarNodes.OfType<AccountNode>().First();
    var nodes = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "Nodes");
    Pump(() => nodes.ContainerFromItem(folderAccount) is not null);
    var accountPoint = ((Control)nodes.ContainerFromItem(folderAccount)!).TranslatePoint(new Point(60, 16), window)!.Value;
    window.MouseDown(accountPoint, MouseButton.Right);
    window.MouseUp(accountPoint, MouseButton.Right);
    var accountMenu = nodes.ContextFlyout as MenuFlyout;
    Pump(() => accountMenu?.IsOpen == true, 2000);
    var newFolderItem = accountMenu?.Items.OfType<MenuItem>().FirstOrDefault();
    Console.WriteLine($"menu da conta aberto: {accountMenu?.IsOpen == true}; itens: {string.Join(" | ", accountMenu?.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()) ?? [])}");
    Save(window, "account-menu.png");
    newFolderItem?.Command?.Execute(null);
    accountMenu?.Hide();
    Pump(() => folderAccount.IsCreatingFolder, 1000);
    Console.WriteLine($"Nova pasta pelo menu abriu o campo: {folderAccount.IsCreatingFolder}");
    // Botão direito numa pasta não abre menu de conta.
    var folderRow = (Control)nodes.ContainerFromItem(folderAccount.Folders[0])!;
    var folderPoint = folderRow.TranslatePoint(new Point(60, 10), window)!.Value;
    window.MouseDown(folderPoint, MouseButton.Right);
    window.MouseUp(folderPoint, MouseButton.Right);
    Pump(() => true);
    Console.WriteLine($"menu da conta sobre pasta: {accountMenu?.IsOpen == true || nodes.ContextFlyout?.IsOpen == true}");
    accountMenu?.Hide();
    folderAccount.AskCreateFolderCommand.Execute(null);
    folderAccount.NewFolderName = "Clientes/Acme";
    Pump(() => true);
    Save(window, "new-folder.png");
    var create = folderAccount.ConfirmCreateFolderCommand.ExecuteAsync(null);
    Pump(() => create.IsCompleted);
    var createdNode = vm.SidebarNodes.OfType<FolderNode>().FirstOrDefault(f => f.Folder.FullName == "Clientes/Acme");
    Console.WriteLine($"nova pasta: na barra lateral {createdNode is not null}, selecionada {vm.SelectedNode == createdNode}, campo fechou {!folderAccount.IsCreatingFolder}, aviso \"{vm.StatusMessage}\"");
    folderAccount.AskCreateFolderCommand.Execute(null);
    folderAccount.NewFolderName = "clientes / acme";
    create = folderAccount.ConfirmCreateFolderCommand.ExecuteAsync(null);
    Pump(() => create.IsCompleted);
    Console.WriteLine($"pasta repetida: \"{folderAccount.FolderError}\"");
    folderAccount.CancelCreateFolderCommand.Execute(null);

    // Excluir pasta: botão direito numa pasta criada pelo usuário abre o menu; a confirmação fica sob a pasta.
    Pump(() => true, 200);
    var acme = vm.SidebarNodes.OfType<FolderNode>().First(f => f.Folder.FullName == "Clientes/Acme");
    Pump(() => nodes.ContainerFromItem(acme) is not null);
    var acmePoint = ((Control)nodes.ContainerFromItem(acme)!).TranslatePoint(new Point(60, 12), window)!.Value;
    window.MouseDown(acmePoint, MouseButton.Right);
    window.MouseUp(acmePoint, MouseButton.Right);
    var folderMenu = nodes.ContextFlyout as MenuFlyout;
    Pump(() => folderMenu?.IsOpen == true, 2000);
    Console.WriteLine($"menu da pasta aberto: {folderMenu?.IsOpen == true}; itens: {string.Join(" | ", folderMenu?.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()) ?? [])}");
    Save(window, "folder-menu.png");
    (folderMenu?.Items.OfType<MenuItem>().First().Command)?.Execute(null);
    folderMenu?.Hide();
    Pump(() => acme.IsConfirmingDelete, 1000);
    Save(window, "folder-delete-confirm.png");
    // Tema escuro com o botão ocupado ("Excluindo…"): continua legível.
    theme.SetTheme(ThemeMode.Dark);
    var dangerButton = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("danger") && b.IsEffectivelyVisible);
    dangerButton.IsEnabled = false;
    Save(window, "folder-deleting-dark.png");
    var dangerText = dangerButton.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible);
    Console.WriteLine($"botão excluir ocupado no escuro: texto {dangerText.Foreground}, opacidade {dangerButton.Opacity}");
    dangerButton.IsEnabled = true;
    theme.SetTheme(ThemeMode.Light);
    Console.WriteLine($"confirmação: \"{acme.DeleteConfirmText}\"");
    var deleting = acme.ConfirmDeleteCommand.ExecuteAsync(null);
    Pump(() => deleting.IsCompleted, 3000);
    Console.WriteLine($"pasta excluída: some da barra {!vm.SidebarNodes.OfType<FolderNode>().Any(f => f.Folder.FullName == "Clientes/Acme")}, aviso \"{vm.StatusMessage}\"");
    // Pasta especial: sem menu.
    var inboxRow = (Control)nodes.ContainerFromItem(folderAccount.Folders[0])!;
    var inboxPoint = inboxRow.TranslatePoint(new Point(60, 10), window)!.Value;
    window.MouseDown(inboxPoint, MouseButton.Right);
    window.MouseUp(inboxPoint, MouseButton.Right);
    Pump(() => true, 300);
    Console.WriteLine($"menu sobre a Caixa de entrada: {nodes.ContextFlyout?.IsOpen == true}");
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

{
    // Spam: na caixa de entrada vira "Marcar como spam" e sai da lista; na pasta Spam, "Não é spam".
    Pump(() => vm.Navigation.IsAtRoot);
    var inbox = vm.SidebarNodes.OfType<FolderNode>().First(f => f.Folder.Kind == Confluir.Core.Models.SpecialFolderKind.Inbox);
    vm.SelectedNode = inbox;
    Pump(() => vm.Rows.OfType<MessageRow>().Any(r => r.Summary.FolderId == inbox.Folder.Id), 3000);
    var spamRow = vm.Rows.OfType<MessageRow>().First();
    Console.WriteLine($"spam na caixa de entrada: \"{vm.SpamActionText([spamRow])}\", pode: {vm.CanSpamRows([spamRow])}");
    var spamTask = vm.ToggleSpamRowsAsync([spamRow]);
    Pump(() => spamTask.IsCompleted);
    Console.WriteLine($"spam: saiu da lista {!vm.Rows.Contains(spamRow)}, aviso \"{vm.StatusMessage}\" com desfazer {vm.HasStatusUndo}");
    var junk = vm.SidebarNodes.OfType<FolderNode>().First(f => f.Folder.Kind == Confluir.Core.Models.SpecialFolderKind.Junk);
    vm.SelectedNode = junk;
    Pump(() => vm.Rows.OfType<MessageRow>().Any(r => r.Summary.FolderId == junk.Folder.Id), 3000);
    var junkRow = vm.Rows.OfType<MessageRow>().First();
    vm.SelectedRow = junkRow;
    Pump(() => vm.Detail is { IsLoading: false });
    Console.WriteLine($"na pasta Spam: \"{vm.SpamActionText([junkRow])}\", barra da mensagem: \"{vm.Detail?.SpamTooltip}\"");
    Save(window, "spam-folder.png");

    // Seleção múltipla: depois de arquivar (e de marcar lida) a barra "x selecionadas" some.
    vm.SelectedNode = inbox;
    Pump(() => vm.Rows.OfType<MessageRow>().Any(r => r.Summary.FolderId == inbox.Folder.Id), 3000);
    var messagesList = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "Messages");
    void SelectThree()
    {
        messagesList.SelectedItems!.Clear();
        foreach (var r in vm.Rows.OfType<MessageRow>().Take(3))
            messagesList.SelectedItems.Add(r);
        Pump(() => vm.HasMultipleSelection, 1000);
    }
    SelectThree();
    Console.WriteLine($"seleção: {vm.SelectionCountText}, barra {vm.HasMultipleSelection}");
    var bulk = vm.ArchiveSelectedCommand.ExecuteAsync(null);
    Pump(() => bulk.IsCompleted);
    Console.WriteLine($"depois de arquivar: barra {vm.HasMultipleSelection}, selecionadas {vm.SelectedMessages.Count}");
    SelectThree();
    bulk = vm.ToggleReadSelectedCommand.ExecuteAsync(null);
    Pump(() => bulk.IsCompleted);
    Pump(() => !vm.HasMultipleSelection, 1000);
    Console.WriteLine($"depois de marcar lida: barra {vm.HasMultipleSelection}");

    // Mesma coisa na pasta Spam: excluir várias, e o X.
    vm.SelectedNode = junk;
    Pump(() => vm.Rows.OfType<MessageRow>().Any(r => r.Summary.FolderId == junk.Folder.Id), 3000);
    messagesList = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "Messages");
    SelectThree();
    Console.WriteLine($"spam: seleção {vm.SelectionCountText}, barra {vm.HasMultipleSelection}");
    bulk = vm.DeleteSelectedCommand.ExecuteAsync(null);
    Pump(() => bulk.IsCompleted);
    Pump(() => true, 300);
    Console.WriteLine($"spam depois de excluir: barra {vm.HasMultipleSelection}, selecionadas vm {vm.SelectedMessages.Count}, lista {messagesList.SelectedItems?.Count}, erro {bulk.Exception?.InnerException?.Message}");
    SelectThree();
    // Lista que já perdeu a seleção sem avisar (o caso da pasta real): o X ainda fecha a barra.
    vm.SetSelection(vm.Rows.OfType<MessageRow>().Take(3));
    vm.ClearSelectionCommand.Execute(null);
    Pump(() => true, 300);
    Console.WriteLine($"spam depois do X: barra {vm.HasMultipleSelection}");

    // Janela estreita: a leitura vira tela própria em vez de ser cortada; volta ao alargar.
    window.Width = 900;
    Pump(() => vm.IsNarrow, 1500);
    Console.WriteLine($"janela 900: estreita {vm.IsNarrow}, duas colunas {vm.IsTwoColumnLayout}");
    Save(window, "narrow.png");
    window.Width = 1320;
    Pump(() => !vm.IsNarrow, 1500);
    Console.WriteLine($"janela 1320: estreita {vm.IsNarrow}, duas colunas {vm.IsTwoColumnLayout}");

    // Barra recolhida: o contador da caixa unificada inteiro.
    vm.ToggleSidebarCommand.Execute(null);
    Pump(() => true, 400);
    Save(window, "rail-badge.png");
    Console.WriteLine($"contador recolhido: \"{vm.UnifiedInbox.RailCount}\"");
    vm.ToggleSidebarCommand.Execute(null);
    Pump(() => true, 400);

    // Cabeçalho com o grupo todo em cópia: recolhido em "+N mais", expande e recolhe; endereço com menu.
    vm.SelectedNode = inbox;
    Pump(() => vm.Rows.OfType<MessageRow>().Any(r => r.Summary.FolderId == inbox.Folder.Id), 3000);
    MessageDetailViewModel? group = null;
    foreach (var r in vm.Rows.OfType<MessageRow>().Take(40))
    {
        vm.SelectedRow = r;
        Pump(() => vm.Detail is { IsLoading: false } d && d.Row == r, 2000);
        if (vm.Detail is { HasCcMore: true } found)
        {
            group = found;
            break;
        }
    }
    if (group is null)
        Console.WriteLine("cópias: nenhuma mensagem com o grupo em cópia");
    else
    {
        Console.WriteLine($"cópias recolhidas: {group.VisibleCc.Count} visíveis, \"{group.CcMoreText}\"");
        Save(window, "recipients-collapsed.png");
        group.ToggleRecipientsCommand.Execute(null);
        Pump(() => true, 200);
        Console.WriteLine($"cópias expandidas: {group.VisibleCc.Count}, mostrar menos {group.CanCollapseRecipients}");
        Save(window, "recipients-expanded.png");
        group.ToggleRecipientsCommand.Execute(null);
        Pump(() => true, 200);

        // Clique de verdade num endereço: abre o menu; copiar e escrever.
        var chip = window.GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("addr") && b.DataContext is AddressViewModel a && group.VisibleCc.Contains(a) && b.IsEffectivelyVisible);
        var chipAddress = (AddressViewModel)chip.DataContext!;
        var chipPoint = chip.TranslatePoint(new Point(chip.Bounds.Width / 2, chip.Bounds.Height / 2), window)!.Value;
        window.MouseDown(chipPoint, MouseButton.Left);
        window.MouseUp(chipPoint, MouseButton.Left);
        var chipMenu = chip.Flyout as MenuFlyout;
        Pump(() => chipMenu?.IsOpen == true, 2000);
        var chipItems = window.GetVisualDescendants().OfType<MenuItem>().ToList();
        Console.WriteLine($"menu do endereço aberto: {chipMenu?.IsOpen == true}; itens: {string.Join(" | ", chipMenu?.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()) ?? [])}");
        Save(window, "recipient-menu.png");
        var copyItem = chipMenu?.Items.OfType<MenuItem>().Last();
        copyItem?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Pump(() => vm.StatusMessage?.Contains(chipAddress.Email) == true, 1000);
        Console.WriteLine($"copiar: aviso \"{vm.StatusMessage}\"");
        chipMenu?.Hide();
        chipAddress.WriteCommand.Execute(null);
        Pump(() => vm.Navigation.CurrentPage is ComposeViewModel, 1000);
        Console.WriteLine($"escrever para: abriu {(vm.Navigation.CurrentPage as ComposeViewModel)?.To}");
        vm.GoBackCommand.Execute(null);
        Pump(() => vm.Navigation.IsAtRoot, 1000);
    }

    // Busca por período escolhido (sem texto): mantém os períodos rápidos e mostra as datas.
    vm.IsAdvancedSearchOpen = true;
    vm.SearchPeriod = vm.SearchPeriods.First(p => p.IsCustom);
    vm.SearchFromDate = DateTime.Today.AddDays(-3);
    vm.SearchToDate = DateTime.Today;
    Pump(() => vm.IsSearchActive && !vm.IsListLoading, 3000);
    var inRange = vm.Rows.OfType<MessageRow>().ToList();
    Console.WriteLine($"período personalizado: {vm.SearchPeriods.Count} opções, {inRange.Count} mensagens, todas no intervalo {inRange.All(r => r.Summary.Date.LocalDateTime.Date >= DateTime.Today.AddDays(-3))}");
    Save(window, "search-custom-period.png");
    vm.ClearSearchCommand.Execute(null);
    vm.IsAdvancedSearchOpen = false;
    Pump(() => !vm.IsSearchActive, 2000);

    // Sincronizar agora: o ícone gira por um instante.
    vm.RefreshCommand.Execute(null);
    var spinningNow = vm.IsSyncSpinning;
    Pump(() => !vm.IsSyncSpinning, 4000);
    Console.WriteLine($"sincronizar: girou {spinningNow}, parou {!vm.IsSyncSpinning}");

    // Barra de rolagem da lista: a padrão do tema (alarga sob o mouse e o polegar segue o arraste), só com as cores
    // do painel de leitura.
    {
        var listBar = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
            .First(s => s.Orientation == Avalonia.Layout.Orientation.Vertical && s.FindAncestorOfType<MessageListView>() is not null && s.IsEffectivelyVisible);
        var listScroll = listBar.FindAncestorOfType<ScrollViewer>()!;
        var thumb = listBar.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().First(x => x.IsEffectivelyVisible);
        // Largura desenhada (a barra padrão alarga com uma transformação, que Bounds não mostra).
        double Drawn() => thumb.TranslatePoint(new Point(thumb.Bounds.Width, 0), window)!.Value.X - thumb.TranslatePoint(new Point(0, 0), window)!.Value.X;
        string Pseudo() => string.Join(" ", ((Avalonia.Controls.IPseudoClasses)listBar.Classes).GetType() is not null
            ? listBar.Classes.Where(c => c.StartsWith(':')) : []);
        var idle = Drawn();
        var idleState = Pseudo();
        var onBar = listBar.TranslatePoint(new Point(listBar.Bounds.Width / 2, listBar.Bounds.Height / 2), window)!.Value;
        window.MouseMove(onBar);
        Pump(() => Drawn() > idle + 1, 2500);
        Pump(() => true, 500);
        var expanded = Drawn();
        var expandedState = Pseudo() + $" AllowAutoHide={listBar.AllowAutoHide} IsExpanded={listBar.IsExpanded} ShowDelay={listBar.ShowDelay} ScrollViewer.AllowAutoHide={listBar.FindAncestorOfType<ScrollViewer>()?.AllowAutoHide}";
        // O headless não dispara o atraso de expansão da barra padrão; força o estado expandido para medir o visual.
        if (!listBar.IsExpanded)
        {
            typeof(Avalonia.Controls.Primitives.ScrollBar).GetMethod("Expand", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(listBar, null);
            Pump(() => Drawn() > idle + 1, 2000);
            Pump(() => true, 500);
            expanded = Drawn();
            expandedState += " (forçado)";
        }
        var fill = (thumb.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Background is not null)?.Background ?? thumb.Background) as Avalonia.Media.ISolidColorBrush;
        Save(window, "list-scrollbar-hover.png");
        var thumbVisual = listBar.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>().First(r => r.Name == "ThumbVisual");
        Console.WriteLine($"  polegar sob o mouse ({Application.Current!.ActualThemeVariant}): {thumbVisual.Fill}");
        window.MouseMove(new Point(5, 5));
        Pump(() => true, 300);
        Console.WriteLine($"  polegar sem o mouse ({Application.Current!.ActualThemeVariant}): {thumbVisual.Fill}");
        theme.SetTheme(Application.Current!.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? ThemeMode.Light : ThemeMode.Dark);
        Pump(() => true, 300);
        Console.WriteLine($"  polegar sem o mouse ({Application.Current!.ActualThemeVariant}): {thumbVisual.Fill}");
        theme.SetTheme(Application.Current!.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? ThemeMode.Light : ThemeMode.Dark);
        Pump(() => true, 300);
        window.MouseMove(onBar);
        Pump(() => true, 300);

        // Arrasto de verdade: aperta no meio do polegar, desce 120 px, solta.
        var start = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
        var thumbTop = thumb.TranslatePoint(new Point(0, 0), listBar)!.Value.Y;
        var before = listScroll.Offset.Y;
        window.MouseMove(start);
        window.MouseDown(start, MouseButton.Left);
        for (var dy = 10; dy <= 120; dy += 10)
        {
            window.MouseMove(start + new Point(0, dy), RawInputModifiers.LeftMouseButton);
            Pump(() => true, 20);
        }
        var thumbMoved = thumb.TranslatePoint(new Point(0, 0), listBar)!.Value.Y - thumbTop;
        window.MouseUp(start + new Point(0, 120), MouseButton.Left);
        Pump(() => true, 200);
        Console.WriteLine($"rolagem da lista: tema {(listBar.Theme is null ? "padrão" : "próprio")}, polegar {idle:0.#}px parado [{idleState}] / {expanded:0.#}px sob o mouse [{expandedState}], cor {fill?.Color}");
        Console.WriteLine($"arrasto: mouse desceu 120px, polegar desceu {thumbMoved:0}px, lista rolou {listScroll.Offset.Y - before:0}px");
        listScroll.Offset = new Vector(0, 0);
        window.MouseMove(new Point(5, 5));
        Pump(() => true, 300);
    }
    // Mãozinha no que é clicável (e cursor de texto nos campos).
    string CursorOf(Control? c) => c is null ? "(não achado)" : c.Cursor?.ToString() ?? "padrão";
    vm.SelectedRow = vm.Rows.OfType<MessageRow>().First();
    Pump(() => vm.Detail is { IsLoading: false }, 2000);
    Pump(() => true, 300);
    var all = window.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible).ToList();
    Console.WriteLine("cursores: " + string.Join(", ", new (string Name, Control? Control)[]
    {
        ("mensagem", all.OfType<ListBoxItem>().FirstOrDefault(i => i.DataContext is MessageRow)),
        ("pasta", all.OfType<ListBoxItem>().FirstOrDefault(i => i.DataContext is FolderNode)),
        ("filtro", all.OfType<RadioButton>().FirstOrDefault(r => r.Classes.Contains("chip"))),
        ("botão", all.OfType<Button>().FirstOrDefault(b => b.Classes.Contains("icon"))),
        ("endereço", all.OfType<Button>().FirstOrDefault(b => b.Classes.Contains("addr"))),
        ("busca (texto)", all.OfType<TextBox>().FirstOrDefault(t => t.Name == "SearchBox")),
    }.Select(p => $"{p.Name}={CursorOf(p.Control)}")));


    // Leitura: remetentes com imagens sempre exibidas, filtro e remoção de vários de uma vez.
    var trusted = services.GetRequiredService<ISettingsStore>().Current.Reading.TrustedImageSenders;
    trusted.AddRange(Enumerable.Range(1, 18).Select(i => $"noticias{i}@{(i % 3 == 0 ? "loja.com" : "jornal.com.br")}"));
    vm.OpenSettingsCommand.Execute(null);
    var readingSettings = (SettingsViewModel)vm.Navigation.CurrentPage!;
    readingSettings.Section = SettingsSection.Reading;
    readingSettings.TrustedSenderFilter = "loja";
    Pump(() => true, 200);
    var filtered = readingSettings.VisibleTrustedSenders.Count;
    readingSettings.AllVisibleTrustedSelected = true;
    readingSettings.TrustedSenderFilter = "";
    readingSettings.VisibleTrustedSenders[0].IsSelected = true;
    Pump(() => true, 200);
    Save(window, "settings-trusted-senders.png");
    var marked = readingSettings.RemoveSelectedTrustedText;
    readingSettings.RemoveSelectedTrustedSendersCommand.Execute(null);
    Console.WriteLine($"remetentes: filtro \"loja\" mostrou {filtered}, botão \"{marked}\", depois de remover sobram {readingSettings.TrustedSenders.Count} (config {trusted.Count}), nenhum da loja: {trusted.All(t => !t.EndsWith("loja.com"))}");
    vm.GoBackCommand.Execute(null);
}

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
    // Clique de verdade no botão "Adicionar conta" do estado vazio (a lista ficava por cima e pegava o clique).
    var addButton = emptyWindow.GetVisualDescendants().OfType<Button>()
        .First(b => b.IsEffectivelyVisible && b.FindAncestorOfType<MessageListView>() is not null && b.Command == emptyVm.AddAccountCommand);
    var addPoint = addButton.TranslatePoint(new Point(addButton.Bounds.Width / 2, addButton.Bounds.Height / 2), emptyWindow)!.Value;
    var emptyPanel = addButton.FindAncestorOfType<StackPanel>()!;
    while (emptyPanel.Parent is StackPanel outer)
        emptyPanel = outer;
    bool HitsButton() => (emptyWindow.InputHitTest(addPoint) as Visual)?.FindAncestorOfType<Button>(includeSelf: true) == addButton;
    emptyPanel.ZIndex = 0;
    Pump(() => true, 50);
    var hitWithout = HitsButton();
    emptyPanel.ZIndex = 1;
    Pump(() => true, 50);
    Console.WriteLine($"sem contas: clique chega ao botão sem a correção {hitWithout}, com ela {HitsButton()}");
    emptyWindow.MouseDown(addPoint, MouseButton.Left);
    emptyWindow.MouseUp(addPoint, MouseButton.Left);
    Pump(() => emptyVm.Navigation.CurrentPage is AccountEditorViewModel, 2000);
    Console.WriteLine($"sem contas, clique em Adicionar conta abriu a tela: {emptyVm.Navigation.CurrentPage is AccountEditorViewModel}");
    emptyWindow.Close();
}

// Página do editor de texto, para testar o script num navegador (o headless não tem WebView).
File.WriteAllText(Path.Combine(outDir, "editor.html"), EditorDocument.Build(theme.CurrentPalette(), ComposeHtml.MailFont, ""));

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
