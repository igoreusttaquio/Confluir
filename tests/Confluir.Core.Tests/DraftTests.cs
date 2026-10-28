using Confluir.Core.Compose;
using Confluir.Core.Models;

namespace Confluir.Core.Tests;

public sealed class DraftTests
{
    [Fact]
    public void Message_id_do_rascunho_identifica_o_rascunho_local()
    {
        var id = Guid.NewGuid();
        var header = DraftMessageId.For(id);

        Assert.Equal(id, DraftMessageId.Parse(header));
        Assert.Equal(id, DraftMessageId.Parse($"<{header}>"));
        Assert.Null(DraftMessageId.Parse("<CAF123@mail.gmail.com>"));
        Assert.Null(DraftMessageId.Parse(null));
    }

    [Theory]
    [InlineData("", "", "", false)]
    [InlineData("ana@exemplo.com", "", "", true)]
    [InlineData("", "Assunto", "", true)]
    [InlineData("", "", "Olá", true)]
    [InlineData("  ", " ", "\n", false)]
    public void Rascunho_vazio_nao_vale_guardar(string to, string subject, string text, bool hasContent)
    {
        var draft = new Draft(Guid.NewGuid(), Guid.NewGuid(), ComposeMode.New, to, "", "", subject, "", text, false,
            [], null, null, DateTimeOffset.UnixEpoch);

        Assert.Equal(!hasContent, draft.IsEmpty);
        Assert.False((draft with { Attachments = [new OutgoingAttachment("a.txt", 1, "a.txt")] }).IsEmpty);
    }
}
