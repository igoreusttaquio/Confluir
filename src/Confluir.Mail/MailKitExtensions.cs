using Confluir.Core.Models;
using MailKit.Security;

namespace Confluir.Mail;

internal static class MailKitExtensions
{
    public static SecureSocketOptions ToSocketOptions(this SecurityMode mode) => mode switch
    {
        SecurityMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SecurityMode.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.None,
    };
}
