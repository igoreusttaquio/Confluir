using Confluir.Core.Models;
using Confluir.Core.Providers;
using MailAddress = Confluir.Core.Models.MailAddress;

namespace Confluir.Mail.Tests;

public class AddressParserAndSenderTests
{
    [Fact]
    public void Le_varios_destinatarios_com_virgula_e_ponto_e_virgula()
    {
        Assert.True(AddressParser.TryParse("Ana <ana@exemplo.com>, bruno@exemplo.com; \"Silva, Carla\" <carla@exemplo.com>;",
            out var list, out var invalid));
        Assert.Null(invalid);
        Assert.Equal(
            [new MailAddress("Ana", "ana@exemplo.com"), new MailAddress("", "bruno@exemplo.com"), new MailAddress("Silva, Carla", "carla@exemplo.com")],
            list);
    }

    [Theory]
    [InlineData("ana@exemplo.com, isso não é email")]
    [InlineData("ana@")]
    [InlineData("ana@localhost")]
    public void Aponta_o_item_invalido(string text)
    {
        Assert.False(AddressParser.TryParse(text, out _, out var invalid));
        Assert.NotNull(invalid);
    }

    [Fact]
    public void Vazio_e_valido_e_sem_enderecos()
    {
        Assert.True(AddressParser.TryParse("  ", out var list, out _));
        Assert.Empty(list);
    }

    [Fact]
    public async Task Mensagem_montada_tem_remetente_conversa_e_anexos()
    {
        var account = new MailAccount(Guid.NewGuid(), "Pessoal", "eu@exemplo.com", MailProvider.Imap, "#3D6BFF",
            "eu@exemplo.com", KnownProviders.Gmail, SenderName: "Igor");
        var file = Path.GetTempFileName();
        await File.WriteAllTextAsync(file, "conteúdo", TestContext.Current.CancellationToken);
        var outgoing = new OutgoingMessage(account.Id, [new MailAddress("Ana", "ana@exemplo.com")], [],
            [new MailAddress("", "oculto@exemplo.com")], "Re: Relatório", "Oi, Ana", null,
            [new OutgoingAttachment("relatorio.pdf", 8, FilePath: file),
             new OutgoingAttachment("foto.jpg", 3, SourceMessageId: Guid.NewGuid(), SourceIndex: 1)],
            InReplyTo: "<orig@exemplo.com>", References: ["<a@exemplo.com>", "<orig@exemplo.com>"]);

        var mime = await SmtpMailSender.BuildAsync(outgoing, account,
            (a, _) => Task.FromResult(new byte[] { 1, 2, 3 }), TestContext.Current.CancellationToken);

        Assert.Equal("\"Igor\" <eu@exemplo.com>", mime.From.ToString());
        Assert.Equal("oculto@exemplo.com", mime.Bcc.ToString());
        Assert.Equal("orig@exemplo.com", mime.InReplyTo);
        Assert.Equal(["a@exemplo.com", "orig@exemplo.com"], mime.References);
        Assert.Equal("Oi, Ana", mime.TextBody);
        Assert.Equal(["relatorio.pdf", "foto.jpg"], mime.Attachments.OfType<MimeKit.MimePart>().Select(p => p.FileName));
        Assert.Equal("application/pdf", mime.Attachments.OfType<MimeKit.MimePart>().First().ContentType.MimeType);
        File.Delete(file);
    }
}
