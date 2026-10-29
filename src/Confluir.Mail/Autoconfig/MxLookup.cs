using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Confluir.Mail.Autoconfig;

/// <summary>
/// Consulta MX pelos servidores DNS do próprio sistema (UDP, porta 53), como o <c>nslookup</c>. O .NET só resolve
/// endereços; redes corporativas costumam bloquear DNS sobre HTTPS, então este é o caminho principal.
/// </summary>
public static class MxLookup
{
    private const ushort TypeMx = 15;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Hosts MX do domínio; vazio se não houver resposta (sem DNS, bloqueado, domínio sem MX).</summary>
    public static async Task<IReadOnlyList<string>> QueryAsync(string domain, CancellationToken ct)
    {
        foreach (var server in DnsServers())
        {
            try
            {
                using var udp = new UdpClient(server.AddressFamily);
                var id = (ushort)Random.Shared.Next(ushort.MaxValue);
                var query = BuildQuery(id, domain);
                await udp.SendAsync(query, new IPEndPoint(server, 53), ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(QueryTimeout);
                var reply = await udp.ReceiveAsync(timeout.Token);
                if (ParseResponse(reply.Buffer, id) is { } hosts)
                    return hosts;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                // Próximo servidor.
            }
        }
        return [];
    }

    /// <summary>Servidores DNS das interfaces ativas (IPv4 primeiro), sem repetir.</summary>
    private static IEnumerable<IPAddress> DnsServers()
    {
        IEnumerable<IPAddress> all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().DnsAddresses)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }
        // Endereços fe80::/fec0:: (site-local) só valem com o escopo da interface; o IPv4 resolve o caso comum.
        foreach (var address in all.Where(a => !a.IsIPv6SiteLocal && !a.IsIPv6LinkLocal)
                     .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                     .Distinct())
            yield return address;
    }

    public static byte[] BuildQuery(ushort id, string domain)
    {
        var packet = new List<byte>(32 + domain.Length);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100); // consulta recursiva
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1); // uma pergunta
        packet.AddRange(header.ToArray());
        foreach (var label in domain.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(new System.Globalization.IdnMapping().GetAscii(label));
            packet.Add((byte)bytes.Length);
            packet.AddRange(bytes);
        }
        packet.Add(0);
        packet.AddRange([0, (byte)TypeMx, 0, 1]); // tipo MX, classe IN
        return [.. packet];
    }

    /// <summary>Hosts das respostas MX; nulo se a resposta não for desta consulta ou vier com erro.</summary>
    public static IReadOnlyList<string>? ParseResponse(byte[] message, ushort id)
    {
        if (message.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(message) != id)
            return null;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(2));
        var rcode = flags & 0x000F;
        if ((flags & 0x8000) == 0 || rcode is not (0 or 3)) // 3 = domínio não existe: resposta válida, sem MX
            return null;
        int questions = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4));
        int answers = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(6));
        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            ReadName(message, ref offset);
            offset += 4;
        }
        var hosts = new List<string>();
        for (var i = 0; i < answers && offset + 10 <= message.Length; i++)
        {
            ReadName(message, ref offset);
            var type = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset));
            var length = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 8));
            var data = offset + 10;
            if (type == TypeMx && length > 2)
            {
                var nameOffset = data + 2; // depois da preferência
                hosts.Add(ReadName(message, ref nameOffset));
            }
            offset = data + length;
        }
        return hosts;
    }

    /// <summary>Nome do DNS a partir de <paramref name="offset"/>, seguindo ponteiros de compressão.</summary>
    private static string ReadName(byte[] message, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        for (var hops = 0; hops < 64 && position < message.Length; hops++)
        {
            var length = message[position];
            if (length == 0)
            {
                position++;
                break;
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= message.Length)
                    break;
                var pointer = ((length & 0x3F) << 8) | message[position + 1];
                if (!jumped)
                    offset = position + 2;
                jumped = true;
                position = pointer;
                continue;
            }
            if (position + 1 + length > message.Length)
                break;
            labels.Add(Encoding.ASCII.GetString(message, position + 1, length));
            position += 1 + length;
        }
        if (!jumped)
            offset = position;
        return string.Join('.', labels);
    }
}
