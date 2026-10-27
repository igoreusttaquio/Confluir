# Confluir — Plano de implementação (prompt para o agente)

Você vai construir o **Confluir**, um cliente de email desktop que concentra várias contas (Gmail, Yahoo, Outlook/Hotmail e qualquer servidor IMAP genérico) numa interface única, moderna e personalizável.

Trabalhe por fases. Ao final de cada fase, o app deve compilar, rodar e ter a funcionalidade da fase testada. Não avance para a próxima fase deixando a anterior quebrada.

---

## 1. Stack

| Camada | Escolha | Observação |
|---|---|---|
| Runtime | .NET (versão LTS mais recente) | C# com nullable habilitado |
| UI | Avalonia (versão estável mais recente) | Padrão MVVM |
| MVVM | CommunityToolkit.Mvvm | `[ObservableProperty]`, `[RelayCommand]` |
| Email | MailKit + MimeKit | IMAP para leitura, SMTP para envio |
| OAuth Microsoft | MSAL.NET (`Microsoft.Identity.Client`) | Necessário para Outlook/Hotmail |
| OAuth Google | `Google.Apis.Auth` ou fluxo OAuth2 manual com loopback | Opcional; Gmail também aceita senha de app |
| Banco local | SQLite (`Microsoft.Data.Sqlite` + Dapper, ou EF Core) | Cache de pastas, cabeçalhos e corpos |
| Busca | SQLite FTS5 | Busca full-text offline |
| DI / Host | `Microsoft.Extensions.Hosting` + DependencyInjection | Serviços injetados nos ViewModels |
| Logs | Serilog (arquivo rotativo) | Nunca logar senhas, tokens ou corpo de email |
| Testes | xUnit | Testes nas camadas Core e Mail |

Antes de adicionar qualquer pacote de terceiros (ícones, temas, WebView), confirme no NuGet que ele existe, é mantido e é compatível com a versão de Avalonia usada.

---

## 2. Estrutura da solução

```
Confluir.sln
├── src/
│   ├── Confluir.App/          # Avalonia: Views, ViewModels, estilos, temas, recursos
│   ├── Confluir.Core/         # Modelos de domínio, interfaces, regras (sem dependência de UI)
│   ├── Confluir.Mail/         # MailKit: conexão IMAP/SMTP, sincronização, IDLE
│   ├── Confluir.Auth/         # Senha de app, OAuth2 (MSAL / Google), cofre de credenciais
│   └── Confluir.Data/         # SQLite, migrações, repositórios, FTS5
└── tests/
    ├── Confluir.Core.Tests/
    └── Confluir.Mail.Tests/
```

`Confluir.Core` não pode referenciar Avalonia nem MailKit. As outras camadas implementam interfaces definidas no Core.

---

## 3. Contas e autenticação (IMAP para todas)

O protocolo é sempre IMAP (leitura) e SMTP (envio). Só o método de autenticação muda por provedor.

| Provedor | IMAP | SMTP | Autenticação |
|---|---|---|---|
| Gmail | imap.gmail.com:993 (SSL) | smtp.gmail.com:465 (SSL) ou 587 (STARTTLS) | Senha de app (exige verificação em duas etapas) ou OAuth2/XOAUTH2 |
| Yahoo | imap.mail.yahoo.com:993 (SSL) | smtp.mail.yahoo.com:465 (SSL) | Senha de app |
| Outlook/Hotmail | outlook.office365.com:993 (SSL) | smtp-mail.outlook.com:587 (STARTTLS) | **OAuth2 obrigatório** (XOAUTH2 via MSAL) |
| IMAP genérico | Informado pelo usuário | Informado pelo usuário | Usuário/senha |

Confirme os hosts e portas atuais na documentação de cada provedor antes de fixá-los no código.

Requisitos:
- Tela "Adicionar conta": o usuário digita o email, o app detecta o provedor pelo domínio e preenche servidores e portas. Campos editáveis em "Configuração avançada".
- Botão "Testar conexão" que valida IMAP e SMTP antes de salvar, com mensagens de erro claras (senha errada, senha de app necessária, servidor inacessível).
- Para Outlook: fluxo OAuth2 interativo com MSAL (navegador do sistema + redirect loopback), escopos `https://outlook.office.com/IMAP.AccessAsUser.All`, `https://outlook.office.com/SMTP.Send` e `offline_access`. Renovação silenciosa do token. O Client ID do app registrado no Entra ID fica em configuração, não no código.
- Link de ajuda na tela explicando como gerar senha de app no Gmail e no Yahoo.
- **Segredos nunca em texto puro** no SQLite ou em arquivos. Use o cofre do sistema:
  - Windows: DPAPI (`ProtectedData`) ou Credential Manager
  - macOS: Keychain
  - Linux: libsecret (Secret Service)
  - Crie uma interface `ICredentialStore` com uma implementação por plataforma.
- Cada conta tem nome de exibição e **cor** escolhida pelo usuário, usada na UI para identificar a origem de cada email.

---

## 4. Sincronização

- Ao adicionar a conta: listar pastas (`GetFoldersAsync`), mapear pastas especiais (Inbox, Enviados, Rascunhos, Lixeira, Spam, Arquivo) via `SPECIAL-USE` com fallback por nome.
- Sincronização incremental por UID: guardar `UIDVALIDITY` e o último UID por pasta. Se `UIDVALIDITY` mudar, refazer a pasta.
- Usar `CONDSTORE`/`QRESYNC` quando o servidor suportar, para sincronizar flags (lido, sinalizado) de forma eficiente.
- Baixar primeiro só os cabeçalhos/envelope (remetente, assunto, data, flags, tamanho, prévia). Corpo e anexos sob demanda, com cache.
- **IMAP IDLE** na Inbox de cada conta, em conexão dedicada, para receber emails novos em tempo real. Reconectar automaticamente com backoff exponencial.
- Conexões por conta gerenciadas por um serviço com fila de operações, para não abrir conexões demais (limite por provedor).
- Ações do usuário (marcar como lido, mover, excluir, arquivar) aplicadas localmente na hora (UI otimista) e enviadas ao servidor em fila; se falhar, reverter e avisar.
- Funcionar offline: ler o que está em cache, enfileirar ações e reenviar quando a conexão voltar.

---

## 5. Interface

### Layout principal (3 colunas)
1. **Barra lateral**: "Caixa de entrada unificada" no topo, depois cada conta com suas pastas (recolhível). Contadores de não lidos. Cor da conta ao lado do nome.
2. **Lista de mensagens**: virtualizada (deve aguentar dezenas de milhares de itens sem travar). Cada item mostra uma faixa/ponto com a cor da conta, remetente, assunto, prévia, data relativa, ícones de anexo e sinalizado. Agrupar por data (Hoje, Ontem, Esta semana, Mais antigos).
3. **Painel de leitura**: cabeçalho com remetente, destinatários e data; corpo; lista de anexos com ícone por tipo e botão salvar/abrir.

Colunas redimensionáveis, com larguras salvas. Modo compacto opcional.

### Renderização do corpo HTML
- Avalie e escolha uma opção de WebView compatível com a versão do Avalonia usada (nativo, pacote da comunidade ou CefGlue). Registre a decisão e o motivo no README.
- Sanitizar o HTML antes de exibir: remover scripts, iframes, handlers de evento e formulários.
- **Bloquear imagens remotas por padrão**, com aviso "Mostrar imagens" e opção "Sempre mostrar deste remetente".
- Links abrem no navegador padrão, nunca dentro do app.
- Emails só-texto exibidos com a fonte de leitura configurada e links clicáveis.
- O corpo deve respeitar o tema escuro quando possível (injeção de CSS para emails sem estilo próprio).

### Composição
- Janela de novo email / responder / responder a todos / encaminhar.
- Seletor "De" com todas as contas.
- Autocompletar destinatários a partir de emails já vistos.
- Editor de texto rico básico (negrito, itálico, link, lista) e modo texto puro.
- Anexar por botão e por arrastar e soltar.
- Rascunho salvo automaticamente.

### Atalhos de teclado
`Ctrl+N` novo, `R` responder, `Shift+R` responder a todos, `F` encaminhar, `E` arquivar, `Del` excluir, `J/K` próximo/anterior, `/` ou `Ctrl+F` buscar, `Ctrl+,` configurações. Tela de atalhos com `?`.

---

## 6. Temas, cores e ícones

- Base: `FluentTheme` do Avalonia com `ThemeVariant` **Claro / Escuro / Seguir o sistema**. Troca de tema instantânea, sem reiniciar.
- Avalie o uso do **FluentAvalonia** (NavigationView, controles no estilo WinUI) ou do **Semi.Avalonia** como camada visual. Escolha um e mantenha consistência.
- **Cor de destaque** configurável pelo usuário (seletor de cor + algumas predefinidas).
- Todas as cores da UI vêm de recursos (`DynamicResource`) definidos em dicionários de tema separados (`Themes/Light.axaml`, `Themes/Dark.axaml`). Nada de cor fixa nas Views.
- **Paleta oficial** (contraste WCAG AA já verificado nos pares texto/fundo). Implementar exatamente estes tokens:

  **Marca** (ícone, splash, ilustrações)
  | Token | Cor |
  |---|---|
  | `Brand.Blue` | `#3D6BFF` |
  | `Brand.Coral` | `#FF7A66` |
  | `Brand.Teal` | `#14C7B4` |
  | `Brand.Amber` | `#FFB938` |

  **Tokens de tema**
  | Token | Claro | Escuro |
  |---|---|---|
  | `Background` | `#F6F7FB` | `#0B1020` |
  | `Surface` (painéis, lista) | `#FFFFFF` | `#111833` |
  | `SurfaceAlt` (barra lateral, hover) | `#EEF1FA` | `#1A2347` |
  | `Border` | `#DDE2F0` | `#26305A` |
  | `TextPrimary` | `#141A33` | `#E8ECFF` |
  | `TextSecondary` | `#5B6385` | `#9AA3C7` |
  | `Accent` (botões, seleção, não lido) | `#2F5BEA` | `#7B97FF` |
  | `OnAccent` (texto sobre Accent) | `#FFFFFF` | `#0B1020` |
  | `Success` | `#15803D` | `#4ADE80` |
  | `Warning` | `#B45309` | `#FBBF24` |
  | `Danger` | `#C8322A` | `#FF8A80` |

  **Cores de conta** (o usuário escolhe uma por conta; usadas na faixa da lista, no ponto da barra lateral e no seletor "De"):
  Azul `#3D6BFF`, Coral `#FF7A66`, Turquesa `#14C7B4`, Âmbar `#FFB938`, Violeta `#8B5CF6`, Rosa `#EC4899`, Verde `#22C55E`, Ciano `#06B6D4`.

  - A cor de destaque configurável pelo usuário substitui só o token `Accent` (e calcula `OnAccent` por contraste). O padrão é o azul acima.
  - Mensagem selecionada: `Accent` com ~12% de opacidade sobre `Surface`. Não lido: assunto em negrito e ponto `Accent`.
  - Referência visual da paleta em `assets/confluir-paleta.png`.
- **Ícones**: usar um único conjunto de ícones vetoriais em toda a UI. Opções, em ordem de preferência:
  1. Lucide (traço limpo e moderno)
  2. Fluent System Icons (combina com FluentTheme)
  3. Material Symbols
  Verifique qual tem pacote Avalonia mantido; se nenhum servir, importe os SVGs/paths como `StreamGeometry` em um dicionário de recursos.
- Animações sutis: transição ao abrir mensagem, hover nos itens, troca de tema suave. Nada exagerado.
- Ícone do app: arquivos em `assets/icone/` (SVG fonte, escolhido entre as opções entregues). Gerar `.ico` (Windows, com 16/32/48/256), `.icns` (macOS) e PNGs (Linux) a partir do SVG.

---

## 7. Fontes customizadas

O usuário deve poder escolher fontes separadas para:
1. **Interface** (menus, lista, botões)
2. **Leitura** (corpo de emails em texto puro e prévia)
3. **Monoespaçada** (cabeçalhos técnicos, código)

Mais tamanho base da fonte (escala global) e altura de linha da leitura.

Fontes disponíveis:
- **Fontes do sistema**: listar via `FontManager.Current.SystemFonts`.
- **Fontes embutidas no app**: incluir 2 ou 3 fontes livres como padrão (sugestão: Inter para a interface, Literata ou Source Serif 4 para leitura, JetBrains Mono para monoespaçada), carregadas como recurso `avares://`.
- **Importar arquivo**: botão "Importar fonte" aceitando `.ttf`, `.otf` ou `.zip` (extrair os arquivos de fonte do zip). Copiar para a pasta de dados do app (`<AppData>/Confluir/fonts/`) e registrar.
- **Importar do Google Fonts pelo nome**: campo onde o usuário digita o nome da família (ex.: "Nunito"). O app baixa os arquivos TTF da família, salva na mesma pasta de fontes e registra. Implementar via a API de CSS do Google Fonts (solicitando TTF) ou pelo repositório público `google/fonts` no GitHub; escolher o caminho mais confiável e tratar erros (família não encontrada, sem conexão). Mostrar a licença da fonte (OFL na maioria dos casos).
- **Pré-visualização ao vivo** na tela de configurações antes de aplicar.
- Carregamento de fontes do disco em tempo de execução: implemente com a API de coleções de fonte do Avalonia (`FontManager` / `IFontCollection`) e confirme na documentação da versão usada qual é a forma suportada. A fonte importada precisa continuar disponível após reiniciar o app.
- As fontes escolhidas são aplicadas por recursos globais (`DynamicResource`) para que a troca seja instantânea em toda a UI.
- Se uma fonte configurada sumir (arquivo apagado), voltar ao padrão sem quebrar o app.

---

## 8. Busca

- Tabela FTS5 com remetente, destinatários, assunto e corpo em texto.
- Barra de busca no topo, resultados em todas as contas por padrão, com filtros: conta, pasta, tem anexo, não lido, período.
- Operadores simples: `de:`, `para:`, `assunto:`, `tem:anexo`.
- Se o resultado local for insuficiente, oferecer "Buscar no servidor" (IMAP SEARCH) para a conta selecionada.

---

## 9. Notificações e sistema

- Notificação nativa do sistema para emails novos (com opção por conta e "não perturbe").
- Ícone na bandeja/tray com contador de não lidos.
- Minimizar para a bandeja (opcional).
- Iniciar com o sistema (opcional).

---

## 10. Configurações (persistidas em JSON na pasta de dados do app)

Abas: **Contas**, **Aparência** (tema, cor de destaque, densidade, fontes), **Leitura** (imagens remotas, marcar como lido após X segundos), **Notificações**, **Atalhos**, **Avançado** (logs, limpar cache, pasta de dados).

---

## 11. Fases de entrega

1. **Esqueleto**: solução, DI, tema claro/escuro funcionando, layout de 3 colunas com dados falsos, conjunto de ícones integrado, ícone do app.
2. **Uma conta IMAP com senha de app**: adicionar conta, testar conexão, listar pastas, listar mensagens (cabeçalhos), ler email em texto puro. Credencial no cofre do sistema.
3. **Cache e sincronização**: SQLite, sincronização incremental por UID, flags, IDLE, modo offline.
4. **HTML seguro**: WebView, sanitização, bloqueio de imagens remotas, anexos.
5. **Múltiplas contas**: caixa de entrada unificada, cores por conta, OAuth2 do Outlook com MSAL.
6. **Envio**: composição, responder/encaminhar, anexos, rascunhos, SMTP.
7. **Fontes customizadas**: sistema, embutidas, importar arquivo, importar do Google Fonts, pré-visualização.
8. **Busca, notificações, atalhos e polimento**.

---

## 12. Regras gerais

- Nada de senha, token ou conteúdo de email em logs.
- Toda operação de rede é assíncrona e cancelável; a UI nunca congela.
- Erros de rede mostrados de forma amigável, com opção de tentar de novo.
- Strings da interface em português do Brasil, centralizadas em arquivo de recursos (preparado para tradução futura).
- Código com testes nas camadas Core, Mail (usando servidor IMAP de teste ou mocks) e Data.
- README com: como compilar, como registrar o app no Entra ID para o Outlook, como gerar senha de app no Gmail/Yahoo, e decisões técnicas tomadas (WebView, biblioteca de ícones, camada de tema).
