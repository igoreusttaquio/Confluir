using System.Net;
using Confluir.Core.Providers;
using Confluir.Core.Services;
using Microsoft.Extensions.Logging;

namespace Confluir.Mail.Autoconfig;

/// <summary>
/// Sugere servidores para um endereço: provedor conhecido → ISPDB do Thunderbird → palpite.
/// Para a ISPDB só o domínio é enviado (nunca o endereço completo).
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
}
