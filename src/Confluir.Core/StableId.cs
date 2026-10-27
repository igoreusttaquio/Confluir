using System.Security.Cryptography;
using System.Text;

namespace Confluir.Core;

/// <summary>Guid determinístico a partir de partes (ex.: conta + pasta + UID), estável entre execuções.</summary>
public static class StableId
{
    public static Guid From(params object[] parts)
    {
        var text = string.Join('\u001F', parts.Select(p => Convert.ToString(p, System.Globalization.CultureInfo.InvariantCulture)));
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(Encoding.UTF8.GetBytes(text), hash); // identificador, não uso criptográfico
        return new Guid(hash);
    }
}
