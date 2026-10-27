# Confluir

Cliente de email desktop que reúne várias contas (Gmail, Yahoo, Outlook/Hotmail e IMAP genérico) numa interface única. Feito em .NET 10 + Avalonia 12.

O plano completo está em [PLANO_CONFLUIR.md](PLANO_CONFLUIR.md).

## Estado atual

- **Fase 1 — Esqueleto**: solução e DI, tema claro/escuro/sistema com troca instantânea, cor de destaque configurável, layout de 3 colunas redimensionáveis (larguras salvas), lista virtualizada, modo compacto, ícones Lucide e ícone do app.
- **Fase 2 — Conta IMAP com senha de app**: tela "Adicionar conta" (detecta o provedor pelo domínio; para outros domínios consulta a ISPDB do Thunderbird ou sugere `imap.`/`smtp.<domínio>`), teste de IMAP e SMTP com mensagens claras (com tentativa automática da porta SMTP alternativa 465 ↔ 587), senha no cofre do sistema, pastas com SPECIAL-USE, lista com os cabeçalhos das **200 mensagens mais recentes** de cada pasta, leitura em texto puro, marcar como lida no servidor e remover conta.

- **Idiomas**: Português (Brasil), English e Español, com troca instantânea em Aparência → Idioma (padrão: seguir o sistema; English se o idioma do sistema não for suportado). Datas e números seguem o idioma. Detalhes em [docs/PLANO_IDIOMAS.md](docs/PLANO_IDIOMAS.md).

Limitações conhecidas até a fase 3: sem cache local (tudo vem do servidor a cada abertura), só as 200 mensagens mais recentes por pasta, sem IDLE. Outlook/Hotmail aguarda o OAuth2 (fase 5).

Para ver a interface com dados de exemplo, sem tocar em servidores: `dotnet run --project src/Confluir.App -- --dados-falsos`.

## Compilar e rodar

Requisitos: SDK do .NET 10 (fixado em `global.json`).

```sh
dotnet build Confluir.sln
dotnet run --project src/Confluir.App
dotnet test --solution Confluir.sln
```

Dados do app (configurações, logs, fontes) ficam em `%APPDATA%\Confluir` no Windows, `~/.config/Confluir` no Linux e `~/Library/Application Support/Confluir` no macOS (`Environment.SpecialFolder.ApplicationData`).

### Ferramentas

| Projeto | Para quê |
|---|---|
| `tools/Confluir.Snapshot` | Renderiza as telas sem abrir janela (Avalonia Headless + Skia) e salva PNGs para revisão visual. Usa uma pasta de dados temporária. `dotnet run --project tools/Confluir.Snapshot -- <pasta>` |
| `tools/Confluir.IconGen` | Gera `assets/icone/confluir.icns` e `assets/icone/png/*.png` a partir do SVG. `dotnet run --project tools/Confluir.IconGen -- assets/confluir-icone.svg assets/icone` |

## Estrutura

```
src/Confluir.App    Avalonia: Views, ViewModels, temas, estilos, recursos
src/Confluir.Core   Modelos, interfaces, regras (sem UI nem MailKit)
src/Confluir.Mail   IMAP/SMTP (fase 2+)
src/Confluir.Auth   Senha de app, OAuth2, cofre de credenciais (fase 2+)
src/Confluir.Data   SQLite, FTS5 (fase 3+)
tests/              xUnit v3 (Core, Mail, Data)
tools/              Snapshot e IconGen
```

## Decisões técnicas

| Tema | Escolha | Motivo |
|---|---|---|
| UI | Avalonia 12.1.4 | Última estável no NuGet. |
| Camada visual | **FluentAvaloniaUI 3.1** (`FluentAvaloniaTheme`) | Compilado para Avalonia 12.1, mantido (ago/2026), visual WinUI coerente com Fluent e geração da rampa de destaque a partir de uma cor (`CustomAccentColor`). O Semi.Avalonia também é compatível, mas segue outra linguagem visual. O `FluentAvaloniaTheme` substitui o `FluentTheme` (é derivado dele). |
| Ícones | **Lucide.Avalonia 0.2.28** | Primeira opção do plano. O pacote declara dependência de Avalonia ≥ 11.3; foi testado renderizando no Avalonia 12 sem problemas. Atualizado semanalmente. Plano B, se quebrar: FluentIcons.Avalonia (compilado para Avalonia 12). |
| Seletor de cor | `ColorPicker` do Avalonia (`Avalonia.Controls.ColorPicker`) | O FA 3 removeu o `ColorPickerButton`; o controle oficial já é dependência do FA. |
| Cores | Tokens da paleta oficial em `Themes/Light.axaml` e `Themes/Dark.axaml` via `ThemeDictionaries`, sempre usados com `DynamicResource` | A cor de destaque do usuário é gravada no dicionário raiz (`Accent`, `OnAccent` calculado por contraste WCAG, `AccentSubtle` a 12%), que tem precedência sobre os dois temas. |
| Strings e idiomas | `Resources/Strings.resx` (pt-BR, neutro) + `Strings.en.resx` + `Strings.es.resx` | No XAML, `{l:Tr Chave}` (atualiza ao trocar de idioma); no C#, a classe gerada `Strings`. Testes em `Confluir.App.Tests` garantem as mesmas chaves e marcadores nos três idiomas. **Toda string nova precisa ir para os três arquivos.** |
| Testes | xUnit v3 no Microsoft Testing Platform | Exigido pelo `dotnet test` do .NET 10 (`global.json` → `test.runner`). |
| Navegação | Telas dentro da janela principal (`NavigationService`, Esc volta) em vez de janelas/diálogos modais; confirmações são inline | Preferência do projeto. |
| Email | MailKit/MimeKit 4.18 | Uma conexão IMAP por conta (`ImapConnectionPool`), reconexão automática se o servidor derrubar a conexão ociosa. |
| Cofre de credenciais | `Microsoft.Identity.Client.Extensions.Msal` (`SystemCredentialStore`) | Mantido pela Microsoft e já necessário para o MSAL (fase 5). Windows: arquivo cifrado com DPAPI do usuário; macOS: Keychain; Linux: libsecret. Se o cofre não estiver disponível, falha em vez de gravar em texto puro. **Testado só no Windows até agora.** |
| Contas | `accounts.json` na pasta de dados | Só metadados e servidores; nada de segredo. Migra para o SQLite na fase 3. |
| Autoconfiguração | ISPDB do Thunderbird (`autoconfig.thunderbird.net`) | Cobre milhares de provedores. Só o **domínio** é enviado, nunca o endereço. |
| WebView para HTML | *A decidir na fase 4* | |

## Configurar contas

Use o botão **Adicionar conta** (rodapé da barra lateral). Gmail, Yahoo e qualquer servidor IMAP/SMTP com usuário e senha funcionam desde a fase 2; Outlook/Hotmail exige OAuth2 e chega na fase 5. Para remover uma conta, clique com o botão direito no nome dela.

### Senha de app — Gmail

1. Ative a verificação em duas etapas em <https://myaccount.google.com/security>.
2. Acesse <https://myaccount.google.com/apppasswords>, dê um nome (ex.: "Confluir") e gere a senha.
3. Use a senha de 16 caracteres no lugar da sua senha normal.

### Senha de app — Yahoo

1. Em <https://login.yahoo.com/account/security>, abra **Gerar senha de app** (ou "Gerenciar senhas de app").
2. Dê um nome ao app, gere e use a senha exibida.

### Outlook/Hotmail — registrar o app no Entra ID

O Outlook exige OAuth2 (XOAUTH2). É preciso um app registrado:

1. Em <https://entra.microsoft.com> → **Aplicativos** → **Registros de aplicativo** → **Novo registro**.
2. Tipos de conta: **Contas em qualquer diretório organizacional e contas pessoais da Microsoft**.
3. URI de redirecionamento: plataforma **Aplicativos móveis e da área de trabalho**, valor `http://localhost`.
4. Em **Autenticação**, habilite **Permitir fluxos de cliente público**.
5. Em **Permissões de API** → **Adicionar** → **APIs que minha organização usa** → *Office 365 Exchange Online* → permissões delegadas `IMAP.AccessAsUser.All` e `SMTP.Send` (além de `offline_access`).
6. Copie o **ID do aplicativo (cliente)**; ele vai na configuração do Confluir (nunca no código).
