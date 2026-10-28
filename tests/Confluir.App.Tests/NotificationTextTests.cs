using Confluir.App.Services.Desktop;
using Confluir.Core.Models;

namespace Confluir.App.Tests;

public sealed class NotificationTextTests
{
    private static MessageSummary Message(string name, string address, string subject) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, address, subject, "", DateTimeOffset.UnixEpoch, false, false, false);

    [Fact]
    public void Uma_mensagem_mostra_remetente_assunto_e_conta()
    {
        var (title, body) = DesktopIntegration.Describe([Message("Ana Souza", "ana@x.com", "Proposta")], "Trabalho");

        Assert.Equal("Ana Souza", title);
        Assert.Equal("Proposta\nTrabalho", body);
    }

    [Fact]
    public void Sem_nome_usa_o_endereco()
    {
        var (title, _) = DesktopIntegration.Describe([Message("", "ana@x.com", "Oi")], null);
        Assert.Equal("ana@x.com", title);
    }

    [Fact]
    public void Varias_mostram_quantas_e_ate_tres_remetentes()
    {
        var messages = new[] { "Ana", "Bruno", "Ana", "Carla", "Davi", "Eva" }
            .Select(n => Message(n, $"{n}@x.com", "Assunto")).ToList();

        var (title, body) = DesktopIntegration.Describe(messages, null);

        Assert.Contains("6", title);
        Assert.StartsWith("Ana, Bruno, Carla ", body);
        Assert.Contains("2", body); // "e mais 2" (Davi, Eva)
        Assert.DoesNotContain("Davi", body);
    }
}
