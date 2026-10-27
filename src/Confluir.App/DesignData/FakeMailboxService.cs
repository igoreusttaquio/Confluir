using Confluir.Core.Models;
using Confluir.Core.Providers;
using Confluir.Core.Services;

namespace Confluir.App.DesignData;

/// <summary>
/// Dados falsos e determinísticos para desenvolver a interface antes da integração IMAP.
/// Gera alguns milhares de mensagens por caixa de entrada para exercitar a virtualização da lista.
/// </summary>
public sealed class FakeMailboxService : IMailboxService
{
    private const int InboxSize = 5000;

    private static readonly (string Name, string Address)[] Senders =
    [
        ("Mariana Costa", "mariana.costa@exemplo.com.br"),
        ("Rafael Lima", "rafael.lima@exemplo.com"),
        ("Juliana Rocha", "ju.rocha@exemplo.com.br"),
        ("Carlos Mendes", "carlos@mendes.dev"),
        ("Beatriz Alves", "bia.alves@exemplo.com"),
        ("Equipe Nuvem", "noreply@nuvem.exemplo.com"),
        ("Banco Horizonte", "avisos@horizonte.exemplo.com.br"),
        ("Livraria Página", "contato@pagina.exemplo.com.br"),
        ("Fernanda Dias", "fernanda.dias@exemplo.com"),
        ("Thiago Martins", "thiago@martins.exemplo.com"),
        ("GitForge", "notifications@gitforge.exemplo.com"),
        ("Agenda Coletiva", "eventos@agenda.exemplo.org"),
        ("Patrícia Nunes", "patricia.nunes@exemplo.com.br"),
        ("Lucas Ferreira", "lucas.f@exemplo.com"),
        ("Viagens Rumo", "reservas@rumo.exemplo.com.br"),
        ("Camila Ribeiro", "camila@ribeiro.exemplo.com"),
    ];

    private static readonly (string Subject, string Preview)[] Topics =
    [
        ("Reunião de alinhamento na quinta", "Oi! Podemos remarcar para as 15h? Preparei a pauta com os pontos que ficaram pendentes da última conversa."),
        ("Sua fatura de outubro chegou", "O valor total da fatura é R$ 1.284,90 com vencimento no dia 10. Acesse o app para ver os detalhes."),
        ("Fotos da viagem 📷", "Separei as melhores fotos do fim de semana na serra. Tem umas do pôr do sol que ficaram incríveis."),
        ("[confluir] Nova issue: sincronização IDLE", "Ao perder a conexão, o cliente não tenta reconectar. Passos para reproduzir: desligar o Wi-Fi por 30s..."),
        ("Proposta comercial — versão final", "Segue em anexo a proposta revisada com os ajustes de escopo e o novo cronograma de entregas."),
        ("Convite: aniversário da Lia", "Vai ser no sábado, a partir das 16h, lá em casa. Confirma até quarta pra gente organizar tudo?"),
        ("Seu pedido foi enviado", "Boa notícia! O pedido #48213 saiu para entrega e deve chegar em até 3 dias úteis."),
        ("Relatório semanal de métricas", "Os acessos cresceram 12% em relação à semana anterior. O gráfico de retenção está na página 2."),
        ("Re: dúvida sobre o contrato", "Conversei com o jurídico e a cláusula 7 pode ser ajustada sem problemas. Te mando a minuta amanhã."),
        ("Lembrete: consulta amanhã às 9h", "Este é um lembrete automático da sua consulta. Chegue com 15 minutos de antecedência."),
        ("Newsletter: novidades do mês", "Neste mês: três recursos novos, um guia de produtividade e os eventos que vêm por aí."),
        ("Planilha do orçamento", "Atualizei a planilha com os gastos de setembro. Dá uma olhada na aba de projeções quando puder."),
        ("Confirmação de reserva", "Sua reserva para 2 hóspedes está confirmada. Check-in a partir das 14h. Código: RX7-2291."),
        ("Feedback sobre o protótipo", "Testei o protótipo com cinco pessoas. A navegação ficou bem clara, mas o fluxo de cadastro confundiu."),
        ("Pull request #212 aprovado", "Seu pull request foi aprovado por 2 revisores e está pronto para o merge."),
        ("Almoço sexta?", "Bora naquele restaurante novo perto do escritório? Ouvi falar bem do prato do dia."),
    ];

    private static readonly AttachmentInfo[] AttachmentPool =
    [
        new("proposta-v3.pdf", "application/pdf", 482_133),
        new("orcamento-2026.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 96_420),
        new("IMG_2041.jpg", "image/jpeg", 2_381_004),
        new("notas-reuniao.txt", "text/plain", 3_210),
        new("arquivos-projeto.zip", "application/zip", 14_902_331),
        new("contrato-assinado.pdf", "application/pdf", 1_204_557),
    ];

    private readonly List<MailAccount> _accounts = [];
    private readonly Dictionary<Guid, List<MailFolder>> _folders = [];
    private readonly Dictionary<Guid, List<MessageSummary>> _messages = [];
    private readonly Dictionary<Guid, MessageSummary> _byId = [];

    public FakeMailboxService(TimeProvider time)
    {
        var now = time.GetLocalNow();
        var random = new Random(20261027);

        AddAccount("Pessoal", "voce@gmail.com", MailProvider.Gmail, AccountColors.Blue, now, random, extraFolder: null);
        AddAccount("Trabalho", "voce@empresa.com.br", MailProvider.Outlook, AccountColors.Coral, now, random, extraFolder: "Clientes");
        AddAccount("Projetos", "voce@yahoo.com.br", MailProvider.Yahoo, AccountColors.Teal, now, random, extraFolder: "Faturas");
    }

    public Task<IReadOnlyList<MailAccount>> GetAccountsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MailAccount>>(_accounts);

    public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(Guid accountId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MailFolder>>(_folders[accountId]);

    public Task<IReadOnlyList<MessageSummary>> GetMessagesAsync(Guid folderId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MessageSummary>>(_messages[folderId]);

    public Task<IReadOnlyList<MessageSummary>> GetUnifiedInboxAsync(CancellationToken ct = default)
    {
        var inboxes = _folders.Values.SelectMany(f => f).Where(f => f.Kind == SpecialFolderKind.Inbox);
        IReadOnlyList<MessageSummary> all = inboxes
            .SelectMany(f => _messages[f.Id])
            .OrderByDescending(m => m.Date)
            .ToList();
        return Task.FromResult(all);
    }

    public async Task<MessageBody> GetMessageBodyAsync(Guid messageId, CancellationToken ct = default)
    {
        await Task.Delay(60, ct); // simula a busca sob demanda
        var m = _byId[messageId];
        var account = _accounts.First(a => a.Id == m.AccountId);
        var random = new Random(messageId.GetHashCode());

        var body =
            $"Olá,\n\n{m.Preview}\n\n" +
            "Qualquer dúvida, é só responder este email. Fico no aguardo do seu retorno para seguirmos " +
            "com os próximos passos.\n\n" +
            $"Abraços,\n{m.FromName}\n\n--\n{m.FromAddress}";

        var cc = random.Next(4) == 0 ? new[] { new MailAddress("Rafael Lima", "rafael.lima@exemplo.com") } : [];
        var attachments = m.HasAttachments
            ? AttachmentPool.OrderBy(_ => random.Next()).Take(random.Next(1, 4)).ToArray()
            : [];

        // Algumas mensagens em HTML (newsletter com fundo próprio, imagem remota e link) para exercitar o leitor.
        string? html = m.Subject.StartsWith("Newsletter", StringComparison.Ordinal) || m.Subject.Contains("fatura") ||
                       m.Subject.Contains("pedido")
            ? $"""
              <html><head><style>.cta {"{"} background:#3d6bff; color:#fff; padding:10px 18px; border-radius:6px; text-decoration:none; {"}"}</style></head>
              <body><table width="100%" bgcolor="#f4f6fb" cellpadding="24"><tr><td>
                <img src="https://images.example.com/logo.png" alt="logo" width="120">
                <h2 style="color:#141a33">{System.Net.WebUtility.HtmlEncode(m.Subject)}</h2>
                <p>{System.Net.WebUtility.HtmlEncode(m.Preview)}</p>
                <p><a class="cta" href="https://exemplo.com/detalhes">Ver detalhes</a></p>
                <img src="https://rastreio.example.com/pixel.gif" width="1" height="1">
              </td></tr></table></body></html>
              """
            : null;

        return new MessageBody(m.Id, [new MailAddress(account.DisplayName, account.Email)], cc, body, attachments, html);
    }

    public Task SetReadAsync(Guid messageId, bool isRead, CancellationToken ct = default) => Task.CompletedTask;

    public Task SetFlaggedAsync(Guid messageId, bool isFlagged, CancellationToken ct = default) => Task.CompletedTask;

    public Task ArchiveAsync(Guid messageId, CancellationToken ct = default) => Remove(messageId);

    public Task DeleteAsync(Guid messageId, CancellationToken ct = default) => Remove(messageId);

    private Task Remove(Guid messageId)
    {
        if (_byId.Remove(messageId, out var m))
            _messages[m.FolderId].Remove(m);
        return Task.CompletedTask;
    }

    public Task<byte[]> GetAttachmentAsync(Guid messageId, int attachmentIndex, CancellationToken ct = default) =>
        Task.FromResult(System.Text.Encoding.UTF8.GetBytes("Anexo de exemplo do Confluir.\n"));

    // Dados falsos não sincronizam: os eventos nunca disparam.
    public event EventHandler<MailboxChangedEventArgs>? MailboxChanged { add { } remove { } }
    public event EventHandler<AccountStatusEventArgs>? AccountStatusChanged { add { } remove { } }
    public event EventHandler<OperationFailedEventArgs>? OperationFailed { add { } remove { } }

    public void RequestSync(Guid? accountId = null)
    {
    }

    public SyncStatus GetStatus(Guid accountId) => SyncStatus.UpToDate;

    private void AddAccount(string name, string email, MailProvider provider, AccountColor color,
        DateTimeOffset now, Random random, string? extraFolder)
    {
        var servers = KnownProviders.Detect(email)?.Servers ?? KnownProviders.Gmail;
        var account = new MailAccount(Guid.NewGuid(), name, email, provider, color.Hex, email, servers);
        _accounts.Add(account);

        var specs = new List<(string Name, SpecialFolderKind Kind, int Count, double UnreadRate)>
        {
            ("INBOX", SpecialFolderKind.Inbox, InboxSize, 0.06),
            ("Sent", SpecialFolderKind.Sent, 180, 0),
            ("Drafts", SpecialFolderKind.Drafts, 4, 0),
            ("Archive", SpecialFolderKind.Archive, 600, 0),
            ("Junk", SpecialFolderKind.Junk, 23, 0.8),
            ("Trash", SpecialFolderKind.Trash, 41, 0),
        };
        if (extraFolder is not null)
            specs.Insert(4, (extraFolder, SpecialFolderKind.None, 75, 0.1));

        var folders = new List<MailFolder>();
        foreach (var spec in specs)
        {
            var folderId = Guid.NewGuid();
            var messages = Generate(account, folderId, spec.Count, spec.UnreadRate, now, random);
            _messages[folderId] = messages;
            foreach (var m in messages)
                _byId[m.Id] = m;
            folders.Add(new MailFolder(folderId, account.Id, spec.Name, spec.Name, spec.Kind,
                messages.Count(m => !m.IsRead)));
        }
        _folders[account.Id] = folders;
    }

    private static List<MessageSummary> Generate(MailAccount account, Guid folderId, int count,
        double unreadRate, DateTimeOffset now, Random random)
    {
        var list = new List<MessageSummary>(count);
        var date = now.AddMinutes(-random.Next(2, 40));
        for (var i = 0; i < count; i++)
        {
            var (fromName, fromAddress) = Senders[random.Next(Senders.Length)];
            var (subject, preview) = Topics[random.Next(Topics.Length)];
            // Não lidas concentradas nas mais recentes, como numa caixa real.
            var unreadChance = unreadRate == 0 ? 0 : i < 25 ? Math.Max(unreadRate, 0.45) : i <= 300 ? unreadRate * 3 : 0;
            var isRead = random.NextDouble() >= unreadChance;
            list.Add(new MessageSummary(
                Guid.NewGuid(), account.Id, folderId, fromName, fromAddress, subject, preview, date,
                IsRead: isRead,
                IsFlagged: random.Next(20) == 0,
                HasAttachments: random.Next(7) == 0));
            date = date.AddMinutes(-random.Next(5, 300));
        }
        return list;
    }
}
