using MimeKit;

namespace Confluir.Mail.Tests;

public class MessageConverterTests
{
    [Fact]
    public void Converte_mensagem_com_texto_destinatarios_e_anexo()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Ana", "ana@exemplo.com"));
        message.To.Add(new MailboxAddress("Bruno", "bruno@exemplo.com"));
        message.Cc.Add(new MailboxAddress("", "carla@exemplo.com"));
        message.Subject = "Relatório";
        var builder = new BodyBuilder { TextBody = "Olá, segue o relatório.\n" };
        builder.Attachments.Add("relatorio.pdf", new byte[3000], new ContentType("application", "pdf"));
        message.Body = builder.ToMessageBody();

        var id = Guid.NewGuid();
        var body = MessageConverter.ToBody(message, id);

        Assert.Equal(id, body.MessageId);
        Assert.Equal("Olá, segue o relatório.", body.TextBody);
        Assert.Equal("Bruno <bruno@exemplo.com>", Assert.Single(body.To).ToString());
        Assert.Equal("carla@exemplo.com", Assert.Single(body.Cc).ToString());
        var attachment = Assert.Single(body.Attachments);
        Assert.Equal("relatorio.pdf", attachment.FileName);
        Assert.Equal("application/pdf", attachment.MimeType);
        Assert.InRange(attachment.Size, 2900, 3100);
    }

    [Fact]
    public void Tamanho_do_anexo_de_mensagem_recebida_desconta_o_base64()
    {
        var builder = new BodyBuilder { TextBody = "oi" };
        builder.Attachments.Add("dados.bin", new byte[3000], new ContentType("application", "octet-stream"));
        var original = new MimeMessage { Body = builder.ToMessageBody() };

        // Como chega do servidor: serializado e lido de novo, com o conteúdo ainda em base64.
        using var stream = new MemoryStream();
        var ct = TestContext.Current.CancellationToken;
        original.WriteTo(stream, ct);
        stream.Position = 0;
        var received = MimeMessage.Load(stream, ct);

        var attachment = Assert.Single(MessageConverter.ToBody(received, Guid.NewGuid()).Attachments);
        Assert.InRange(attachment.Size, 2900, 3100);
    }

    [Fact]
    public void Data_usa_o_cabecalho_ou_a_data_de_recebimento_quando_ele_falta_ou_e_absurdo()
    {
        var header = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.FromHours(-3));
        var received = new DateTimeOffset(2026, 10, 20, 12, 1, 0, TimeSpan.Zero);

        Assert.Equal(header, MessageConverter.BestDate(header, received));
        Assert.Equal(received, MessageConverter.BestDate(null, received));
        Assert.Equal(received, MessageConverter.BestDate(new DateTimeOffset(1, 1, 1, 0, 0, 0, TimeSpan.Zero), received));
        Assert.Equal(received, MessageConverter.BestDate(DateTimeOffset.UtcNow.AddYears(30), received));
        Assert.Equal(DateTimeOffset.UnixEpoch, MessageConverter.BestDate(null, null));
        Assert.Equal(DateTimeOffset.UnixEpoch, MessageConverter.BestDate(DateTimeOffset.MinValue, DateTimeOffset.MinValue));
    }

    [Fact]
    public void Sem_texto_puro_usa_o_HTML_convertido()
    {
        var message = new MimeMessage
        {
            Body = new BodyBuilder { HtmlBody = "<p>Oi <b>Ana</b>,</p><p>Tudo bem?</p>" }.ToMessageBody(),
        };

        Assert.Equal("Oi Ana,\nTudo bem?", MessageConverter.ToBody(message, Guid.NewGuid()).TextBody);
    }

    [Fact]
    public void HtmlToText_remove_estilos_scripts_e_decodifica_entidades()
    {
        const string html = """
            <html><head><style>p { color: red }</style><title>t</title></head>
            <body><script>alert(1)</script>
            <div>Pre&ccedil;o:&nbsp;R$&nbsp;10&amp;mais</div><br/>Linha 2<br>
            <ul><li>um</li><li>dois</li></ul></body></html>
            """;

        var text = MessageConverter.HtmlToText(html);

        Assert.DoesNotContain("color", text);
        Assert.DoesNotContain("alert", text);
        Assert.Contains("Preço: R$ 10&mais", text);
        Assert.Contains("Linha 2", text);
        Assert.Contains("um\ndois", text);
        Assert.DoesNotContain("\n\n\n", text);
    }
}
