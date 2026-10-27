using Confluir.Core.Models;
using MailKit;

namespace Confluir.Mail.Tests;

public class SpecialFolderResolverTests
{
    [Theory]
    [InlineData(FolderAttributes.Sent, SpecialFolderKind.Sent)]
    [InlineData(FolderAttributes.Drafts, SpecialFolderKind.Drafts)]
    [InlineData(FolderAttributes.Trash, SpecialFolderKind.Trash)]
    [InlineData(FolderAttributes.Junk, SpecialFolderKind.Junk)]
    [InlineData(FolderAttributes.Archive, SpecialFolderKind.Archive)]
    [InlineData(FolderAttributes.All, SpecialFolderKind.Archive)]
    [InlineData(FolderAttributes.HasNoChildren, SpecialFolderKind.None)]
    public void Usa_atributos_SPECIAL_USE(FolderAttributes attributes, SpecialFolderKind expected)
    {
        Assert.Equal(expected, SpecialFolderResolver.Resolve(attributes, "Qualquer", "[Gmail]/Qualquer", isInbox: false));
    }

    [Fact]
    public void Atributo_tem_precedencia_sobre_o_nome()
    {
        Assert.Equal(SpecialFolderKind.Trash,
            SpecialFolderResolver.Resolve(FolderAttributes.Trash, "Sent", "Sent", isInbox: false));
    }

    [Theory]
    [InlineData("Sent Items", SpecialFolderKind.Sent)]
    [InlineData("Itens Enviados", SpecialFolderKind.Sent)]
    [InlineData("rascunhos", SpecialFolderKind.Drafts)]
    [InlineData("Deleted Messages", SpecialFolderKind.Trash)]
    [InlineData("Lixeira", SpecialFolderKind.Trash)]
    [InlineData("Bulk Mail", SpecialFolderKind.Junk)]
    [InlineData("Arquivo", SpecialFolderKind.Archive)]
    [InlineData("Faturas", SpecialFolderKind.None)]
    public void Sem_SPECIAL_USE_usa_nomes_comuns(string name, SpecialFolderKind expected)
    {
        Assert.Equal(expected, SpecialFolderResolver.Resolve(FolderAttributes.None, name, name, isInbox: false));
    }

    [Theory]
    [InlineData("INBOX", false)]
    [InlineData("inbox", false)]
    [InlineData("Caixa de entrada", true)]
    public void Reconhece_a_caixa_de_entrada(string fullName, bool isInbox)
    {
        Assert.Equal(SpecialFolderKind.Inbox,
            SpecialFolderResolver.Resolve(FolderAttributes.None, fullName, fullName, isInbox));
    }
}
