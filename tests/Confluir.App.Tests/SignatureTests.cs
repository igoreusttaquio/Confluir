using System.Text.Json;
using Confluir.App.Services;
using Confluir.Core.Models;
using Confluir.Core.Providers;

namespace Confluir.App.Tests;

public sealed class SignatureTests
{
    private const string Work = "<div><b>Ana Souza</b></div><div>Engenharia · (11) 5555-0000</div>";
    private const string Personal = "<div>Ana</div>";

    private static MessageBody Body(string text) => new(Guid.NewGuid(), [], [], text, [], null);

    [Fact]
    public void Mensagem_nova_tem_espaco_para_escrever_e_a_assinatura_no_fim()
    {
        var html = ComposeHtml.NewMessage(Work);
        Assert.StartsWith("<div><br></div><div><br></div>", html);
        Assert.EndsWith("<!--confluir-signature--><div class=\"signature\">" + Work + "</div><!--/confluir-signature-->", html);
        // Sem assinatura, só os marcadores (para uma troca de conta achar o lugar).
        Assert.Equal("<div><br></div><div><br></div><!--confluir-signature--><!--/confluir-signature-->", ComposeHtml.NewMessage(null));
    }

    [Fact]
    public void Na_resposta_a_assinatura_vem_antes_da_citacao()
    {
        var html = ComposeHtml.Reply("Em 1 de jan., Bia escreveu:", Body("oi"), Work);
        var signature = html.IndexOf("Engenharia", StringComparison.Ordinal);
        var header = html.IndexOf("Bia escreveu", StringComparison.Ordinal);
        var quote = html.IndexOf("<blockquote", StringComparison.Ordinal);
        Assert.True(signature > 0 && signature < header && header < quote);
    }

    [Fact]
    public void Trocar_a_conta_troca_a_assinatura_e_volta()
    {
        var html = "<div>Olá, tudo bem?</div>" + ComposeHtml.SignatureBlock(Work);

        var personal = ComposeHtml.ReplaceSignature(html, Personal);
        Assert.Contains("<div>Olá, tudo bem?</div>", personal);
        Assert.Contains(Personal, personal);
        Assert.DoesNotContain("Engenharia", personal);

        var none = ComposeHtml.ReplaceSignature(personal, null);
        Assert.DoesNotContain(Personal, none);
        Assert.Contains("<!--confluir-signature--><!--/confluir-signature-->", none);

        // Conta sem assinatura → conta com assinatura: os marcadores continuavam lá.
        Assert.Contains("Engenharia", ComposeHtml.ReplaceSignature(none, Work));
    }

    [Fact]
    public void Sem_marcadores_nada_muda()
    {
        const string html = "<div>assinatura apagada pelo usuário</div>";
        Assert.Equal(html, ComposeHtml.ReplaceSignature(html, Work));
    }

    [Fact]
    public void No_modo_texto_a_assinatura_vem_depois_do_separador_padrao()
    {
        Assert.Equal("-- \nAna Souza\nEngenharia · (11) 5555-0000", ComposeHtml.SignatureText(Work));
        Assert.Equal("", ComposeHtml.SignatureText(null));

        var text = "Oi!\n\n" + ComposeHtml.SignatureText(Work);
        Assert.Equal("Oi!\n\n-- \nAna", ComposeHtml.ReplaceSignatureText(text, Work, Personal));
        Assert.Equal("texto editado", ComposeHtml.ReplaceSignatureText("texto editado", Work, Personal));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("<div><br></div>", false)]
    [InlineData("<div>  </div>", false)]
    [InlineData("<div>Ana</div>", true)]
    [InlineData("<img src=\"data:image/png;base64,AAAA\">", true)]
    public void Assinatura_vazia_conta_como_nenhuma(string? html, bool expected) =>
        Assert.Equal(expected, ComposeHtml.HasContent(html));

    [Fact]
    public void Contas_salvas_antes_da_assinatura_continuam_validas()
    {
        var old = new MailAccount(Guid.NewGuid(), "Pessoal", "ana@gmail.com", MailProvider.Gmail, "#4C6FFF", "ana@gmail.com",
            KnownProviders.Gmail);
        var json = JsonSerializer.Serialize(old);
        using var doc = JsonDocument.Parse(json);
        // JSON de uma versão antiga: sem os campos novos.
        var legacy = JsonSerializer.Serialize(doc.RootElement.EnumerateObject()
            .Where(p => p.Name is not (nameof(MailAccount.Signature) or nameof(MailAccount.SignatureOnReplies)))
            .ToDictionary(p => p.Name, p => p.Value));

        var loaded = JsonSerializer.Deserialize<MailAccount>(legacy)!;
        Assert.Null(loaded.Signature);
        Assert.True(loaded.SignatureOnReplies);
    }
}
