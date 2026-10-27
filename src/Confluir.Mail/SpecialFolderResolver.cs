using Confluir.Core.Models;
using MailKit;

namespace Confluir.Mail;

/// <summary>Identifica pastas especiais via SPECIAL-USE (RFC 6154), com nomes comuns como alternativa.</summary>
public static class SpecialFolderResolver
{
    private static readonly (SpecialFolderKind Kind, string[] Names)[] NameFallbacks =
    [
        (SpecialFolderKind.Sent, ["Sent", "Sent Items", "Sent Mail", "Sent Messages", "Enviados", "Itens Enviados", "Itens enviados", "Mensagens enviadas"]),
        (SpecialFolderKind.Drafts, ["Drafts", "Draft", "Rascunhos", "Rascunho"]),
        (SpecialFolderKind.Trash, ["Trash", "Deleted", "Deleted Items", "Deleted Messages", "Bin", "Lixeira", "Lixo", "Itens Excluídos", "Itens excluídos"]),
        (SpecialFolderKind.Junk, ["Junk", "Junk E-mail", "Junk Email", "Spam", "Bulk", "Bulk Mail", "Lixo Eletrônico", "Lixo eletrônico"]),
        (SpecialFolderKind.Archive, ["Archive", "Archives", "All Mail", "Arquivo", "Arquivados", "Todos os e-mails"]),
    ];

    public static SpecialFolderKind Resolve(FolderAttributes attributes, string name, string fullName, bool isInbox)
    {
        if (isInbox || fullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
            return SpecialFolderKind.Inbox;

        if (attributes.HasFlag(FolderAttributes.Sent)) return SpecialFolderKind.Sent;
        if (attributes.HasFlag(FolderAttributes.Drafts)) return SpecialFolderKind.Drafts;
        if (attributes.HasFlag(FolderAttributes.Trash)) return SpecialFolderKind.Trash;
        if (attributes.HasFlag(FolderAttributes.Junk)) return SpecialFolderKind.Junk;
        if (attributes.HasFlag(FolderAttributes.Archive)) return SpecialFolderKind.Archive;
        // \All (ex.: "[Gmail]/Todos os e-mails") funciona como arquivo.
        if (attributes.HasFlag(FolderAttributes.All)) return SpecialFolderKind.Archive;

        foreach (var (kind, names) in NameFallbacks)
        {
            if (names.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return kind;
        }
        return SpecialFolderKind.None;
    }
}
