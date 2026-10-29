using System.Net;
using System.Text.Json;
using Confluir.Core.Providers;
using Confluir.Core.Services;
using Microsoft.Extensions.Logging;

namespace Confluir.Mail.Autoconfig;

/// <summary>
/// Sugere servidores para um endereço: provedor conhecido → domínio hospedado no Google Workspace ou no
/// Microsoft 365 (registro MX) → ISPDB do Thunderbird → palpite. Para fora só vai o domínio (nunca o endereço).
/// </summary>
public sealed class ServerSettingsResolver(HttpClient http, ILogger<ServerSettingsResolver> logger) : IServerSettingsResolver
{
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(8);

    public async Task<ProviderPreset?> ResolveAsync(string email, CancellationToken ct = default)
    {
        if (KnownProviders.Detect(email) is { } known)
            return known;

        var domain = KnownProviders.GetDomain(email);
        if (domain is null)
            return null;

        if (KnownProviders.FromMailExchangers(email, await LookupMxAsync(domain, ct)) is { } hosted)
            return hosted;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LookupTimeout);
            var url = $"https://autoconfig.thunderbird.net/v1.1/{Uri.EscapeDataString(domain)}";
            using var response = await http.GetAsync(url, timeout.Token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var xml = await response.Content.ReadAsStringAsync(timeout.Token);
                if (IspdbParser.Parse(xml, email) is { } preset)
                    return preset;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogInformation("Autoconfiguração indisponível para {Domain}: {Error}", domain, ex.GetType().Name);
        }

        return KnownProviders.Guess(email);
    }

    /// <summary>
    /// Servidores de email do domínio (MX): pelo DNS do sistema e, sem resposta, por DNS sobre HTTPS. Falha → lista
    /// vazia: a detecção segue pelos outros caminhos.
    /// </summary>
    private async Task<IReadOnlyList<string>> LookupMxAsync(string domain, CancellationToken ct)
    {
        if (await MxLookup.QueryAsync(domain, ct) is { Count: > 0 } hosts)
            return hosts;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LookupTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(domain)}&type=MX");
            request.Headers.Accept.ParseAdd("application/dns-json");
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return [];
            return ParseMx(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogInformation("Consulta MX indisponível para {Domain}: {Error}", domain, ex.GetType().Name);
            return [];
        }
    }

    /// <summary>Hosts da resposta JSON do DNS sobre HTTPS (registros tipo 15, "10 aspmx.l.google.com.").</summary>
    public static IReadOnlyList<string> ParseMx(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
            return [];
        return answers.EnumerateArray()
            .Where(a => a.TryGetProperty("type", out var type) && type.GetInt32() == 15)
            .Select(a => a.GetProperty("data").GetString() ?? "")
            .Select(data => data.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "")
            .Where(host => host.Length > 0)
            .ToList();
    }
}
