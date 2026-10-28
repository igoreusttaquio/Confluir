using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Confluir.Core;
using Confluir.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Confluir.App.Services;

public enum FontRole
{
    Interface,
    Reading,
    Mono,
}

public enum FontOrigin
{
    /// <summary>Padrão do app para o papel (chave nula).</summary>
    Default,
    Bundled,
    Imported,
    System,
}

/// <summary>Uma opção de fonte. <see cref="Key"/> é o que vai para as configurações (nulo = padrão).</summary>
public sealed record FontChoice(string? Key, string FamilyName, FontOrigin Origin, FontFamily Family);

/// <summary>Resultado de uma importação: famílias adicionadas e a licença, quando conhecida.</summary>
public sealed record FontImportResult(IReadOnlyList<string> Families, string? License);

public sealed class FontImportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Coleção de fontes carregadas de arquivos (embutidas no app ou importadas pelo usuário).</summary>
internal sealed class FileFontCollection(Uri key) : FontCollectionBase
{
    public override Uri Key => key;

    public bool Add(Uri source) => TryAddFontSource(source);
}

/// <summary>
/// Fontes da interface, da leitura e monoespaçada: as embutidas no app (Inter, Source Serif 4, JetBrains Mono),
/// as do sistema e as importadas pelo usuário (arquivo ou Google Fonts), guardadas em &lt;dados&gt;/fonts/&lt;família&gt;.
/// A escolha vale na hora, por recursos globais; uma fonte que sumiu volta ao padrão.
/// </summary>
public sealed partial class FontService(ISettingsStore settings, AppPaths paths, HttpClient http, ILogger<FontService> logger)
{
    public const string BundledInterface = "Inter";
    public const string BundledReading = "Source Serif 4";
    public const string BundledMono = "JetBrains Mono";

    /// <summary>Fonte padrão da leitura (a do sistema), também usada como alternativa no CSS.</summary>
    public const string SystemSansCss = "\"Segoe UI\", system-ui, -apple-system, \"Helvetica Neue\", Arial, sans-serif";
    private const string MonoFallbackCss = "Consolas, \"Cascadia Mono\", Menlo, monospace";

    private static readonly Uri BundledKey = new("fonts:ConfluirBundled", UriKind.Absolute);
    private static readonly Uri ImportedKey = new("fonts:ConfluirImported", UriKind.Absolute);
    private static readonly Uri BundledFolder = new("avares://Confluir/Assets/Fonts", UriKind.Absolute);

    // Arquivos embutidos de cada família (prefixo do nome do arquivo em Assets/Fonts).
    private static readonly Dictionary<string, string> BundledFiles = new()
    {
        [BundledInterface] = "Inter-",
        [BundledReading] = "SourceSerif4-",
        [BundledMono] = "JetBrainsMono-",
    };

    private const long MaxFontFileBytes = 20L * 1024 * 1024;
    private const int MaxFilesPerImport = 40;

    private readonly Dictionary<string, string> _cssCache = [];
    private List<string> _importedFamilies = [];
    private bool _initialized;

    /// <summary>Fontes ou escala mudaram (o WebView precisa remontar o documento).</summary>
    public event EventHandler? Changed;

    public FontSettings Current => settings.Current.Fonts;

    public IReadOnlyList<string> ImportedFamilies => _importedFamilies;

    /// <summary>Registra as coleções e aplica as fontes salvas. Chamado ao iniciar o app.</summary>
    public void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;
        try
        {
            FontManager.Current.AddFontCollection(new EmbeddedFontCollection(BundledKey, BundledFolder));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao registrar as fontes embutidas");
        }
        LoadImported();
        Apply();
    }

    // Opções ----------------------------------------------------------------------------------------

    public IReadOnlyList<FontChoice> Choices(FontRole role)
    {
        // A padrão da leitura é a fonte do sistema ("$Default" para o Avalonia): mostra o nome real.
        var defaultName = role == FontRole.Reading ? SystemDefaultName() : DefaultFamily(role).Name;
        var list = new List<FontChoice> { new(null, defaultName, FontOrigin.Default, DefaultFamily(role)) };
        list.AddRange(BundledFiles.Keys.Select(name => new FontChoice($"bundled:{name}", name, FontOrigin.Bundled, Bundled(name))));
        list.AddRange(_importedFamilies.Select(name => new FontChoice($"imported:{name}", name, FontOrigin.Imported, Imported(name))));
        var system = SystemFamilyNames();
        list.AddRange(system.Select(name => new FontChoice($"system:{name}", name, FontOrigin.System, new FontFamily(name))));
        return list;
    }

    private static string SystemDefaultName()
    {
        try
        {
            return FontManager.Current.DefaultFontFamily.Name;
        }
        catch (Exception)
        {
            return "System";
        }
    }

    private static List<string> SystemFamilyNames()
    {
        try
        {
            return FontManager.Current.SystemFonts.Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n) && !n.StartsWith('@'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    public string? KeyFor(FontRole role) => role switch
    {
        FontRole.Interface => Current.Interface,
        FontRole.Reading => Current.Reading,
        _ => Current.Mono,
    };

    // Resolução -------------------------------------------------------------------------------------

    /// <summary>Família para a chave; chaves que não existem mais (fonte apagada) voltam ao padrão.</summary>
    public FontFamily Resolve(string? key, FontRole role)
    {
        switch (Parse(key))
        {
            case ("bundled", var name) when BundledFiles.ContainsKey(name):
                return Bundled(name);
            case ("imported", var name) when _importedFamilies.Contains(name, StringComparer.OrdinalIgnoreCase):
                return Imported(name);
            case ("system", var name) when SystemFamilyNames().Contains(name, StringComparer.OrdinalIgnoreCase):
                return new FontFamily(name);
            default:
                return DefaultFamily(role);
        }
    }

    private static FontFamily DefaultFamily(FontRole role) => role switch
    {
        FontRole.Interface => Bundled(BundledInterface),
        FontRole.Reading => FontFamily.Default,
        _ => Bundled(BundledMono),
    };

    private static FontFamily Bundled(string name) => new($"{BundledKey}#{name}");
    private static FontFamily Imported(string name) => new($"{ImportedKey}#{name}");

    private static (string Origin, string Name) Parse(string? key)
    {
        var i = key?.IndexOf(':') ?? -1;
        if (key is null || i < 1)
            return ("", "");
        return (key[..i], key[(i + 1)..]);
    }

    /// <summary>Aplica fontes e escala aos recursos globais (troca instantânea em toda a interface).</summary>
    public void Apply()
    {
        if (Application.Current is { } app)
        {
            app.Resources["ContentControlThemeFontFamily"] = Resolve(Current.Interface, FontRole.Interface);
            app.Resources["ReadingFontFamily"] = Resolve(Current.Reading, FontRole.Reading);
            app.Resources["MonoFontFamily"] = Resolve(Current.Mono, FontRole.Mono);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save(string? interfaceKey, string? readingKey, string? monoKey, double scale, double lineHeight)
    {
        Current.Interface = interfaceKey;
        Current.Reading = readingKey;
        Current.Mono = monoKey;
        Current.Scale = Math.Clamp(scale, 0.8, 1.5);
        Current.ReadingLineHeight = Math.Clamp(lineHeight, 1.2, 2.2);
        settings.Save();
        Apply();
    }

    // CSS para o WebView ----------------------------------------------------------------------------

    /// <summary>
    /// Regras @font-face (arquivos embutidos como data:, já que o documento não acessa a rede) e o valor de
    /// font-family para a fonte escolhida.
    /// </summary>
    public (string FontFaces, string Family) Css(FontRole role)
    {
        var key = KeyFor(role);
        var fallback = role == FontRole.Mono ? MonoFallbackCss : SystemSansCss;
        var (origin, name) = Parse(key);
        if (key is null)
        {
            if (role == FontRole.Reading)
                return ("", SystemSansCss);
            (origin, name) = ("bundled", role == FontRole.Interface ? BundledInterface : BundledMono);
        }
        var family = $"\"{name.Replace("\"", "")}\", {fallback}";
        return origin switch
        {
            "bundled" or "imported" => (FontFaces(origin, name), family),
            "system" => ("", family),
            _ => ("", fallback),
        };
    }

    private string FontFaces(string origin, string name)
    {
        var cacheKey = $"{origin}:{name}";
        if (_cssCache.TryGetValue(cacheKey, out var css))
            return css;
        try
        {
            var files = origin == "bundled" ? BundledFontFiles(name) : ImportedFontFiles(name);
            // Só regular e negrito (itálico quando existir): o documento fica leve e cobre o que os emails usam.
            css = string.Concat(files
                .Select(f => (f.Name, Face: FaceFromFileName(f.Name), f.Read))
                .GroupBy(f => f.Face)
                .Where(g => g.Key.Weight is 400 or 700)
                .Select(g => g.First())
                .Select(f => $"@font-face {{ font-family: \"{name}\"; font-weight: {f.Face.Weight}; font-style: {(f.Face.Italic ? "italic" : "normal")}; " +
                             $"src: url(data:font/ttf;base64,{Convert.ToBase64String(f.Read())}); }}\n"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao preparar a fonte {Family} para o leitor", name);
            css = "";
        }
        _cssCache[cacheKey] = css;
        return css;
    }

    private static List<(string Name, Func<byte[]> Read)> BundledFontFiles(string family)
    {
        if (!BundledFiles.TryGetValue(family, out var prefix))
            return [];
        return AssetLoader.GetAssets(BundledFolder, null)
            .Where(u => Path.GetFileName(u.AbsolutePath).StartsWith(prefix, StringComparison.Ordinal) &&
                        u.AbsolutePath.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase))
            .Select(u => (Path.GetFileName(u.AbsolutePath), (Func<byte[]>)(() =>
            {
                using var s = AssetLoader.Open(u);
                using var m = new MemoryStream();
                s.CopyTo(m);
                return m.ToArray();
            })))
            .ToList();
    }

    private List<(string Name, Func<byte[]> Read)> ImportedFontFiles(string family)
    {
        var dir = Path.Combine(paths.FontsDirectory, SafeName(family));
        return Directory.Exists(dir)
            ? FontFilesIn(dir).Select(f => (Path.GetFileName(f), (Func<byte[]>)(() => File.ReadAllBytes(f)))).ToList()
            : [];
    }

    /// <summary>Peso e estilo pelo nome do arquivo (ex.: "Nunito-Bold.ttf", "Inter-700.ttf", "X-400Italic.ttf").</summary>
    public static (int Weight, bool Italic) FaceFromFileName(string fileName)
    {
        var n = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        var italic = n.Contains("italic") || n.EndsWith("-it", StringComparison.Ordinal);
        var digits = WeightNumber().Match(n);
        var weight = digits.Success ? int.Parse(digits.Groups[1].Value)
            : n.Contains("extrabold") || n.Contains("black") || n.Contains("heavy") ? 800
            : n.Contains("semibold") || n.Contains("demibold") ? 600
            : n.Contains("bold") ? 700
            : n.Contains("medium") ? 500
            : n.Contains("light") || n.Contains("thin") ? 300
            : 400;
        return (weight, italic);
    }

    [GeneratedRegex(@"-(\d{3})(italic)?$")]
    private static partial Regex WeightNumber();

    // Importação -------------------------------------------------------------------------------------

    /// <summary>Importa arquivos .ttf/.otf ou .zip (extrai as fontes de dentro). Retorna as famílias adicionadas.</summary>
    public async Task<FontImportResult> ImportFilesAsync(IEnumerable<string> files, CancellationToken ct = default)
    {
        var fonts = new List<(string Name, byte[] Data)>();
        foreach (var file in files)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".ttf" or ".otf")
            {
                if (new FileInfo(file).Length > MaxFontFileBytes)
                    throw new FontImportException(Resources.Strings.Fonts_FileTooLarge);
                fonts.Add((Path.GetFileName(file), await File.ReadAllBytesAsync(file, ct)));
            }
            else if (ext == ".zip")
            {
                fonts.AddRange(await Task.Run(() => FromZip(file), ct));
            }
        }
        if (fonts.Count == 0)
            throw new FontImportException(Resources.Strings.Fonts_NoFontFiles);
        return new FontImportResult(await AddFontsAsync(fonts, license: null, ct), null);
    }

    private static List<(string, byte[])> FromZip(string file)
    {
        using var zip = ZipFile.OpenRead(file);
        var entries = zip.Entries
            .Where(e => e.FullName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                        e.FullName.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            .Where(e => !e.FullName.Contains("__MACOSX", StringComparison.Ordinal))
            .Take(MaxFilesPerImport)
            .ToList();
        var result = new List<(string, byte[])>();
        foreach (var entry in entries)
        {
            // Tamanho declarado e lido com limite: um zip malicioso não enche a memória.
            if (entry.Length > MaxFontFileBytes)
                continue;
            using var s = entry.Open();
            using var m = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = s.Read(buffer)) > 0)
            {
                if (m.Length + read > MaxFontFileBytes)
                    throw new FontImportException(Resources.Strings.Fonts_FileTooLarge);
                m.Write(buffer, 0, read);
            }
            result.Add((Path.GetFileName(entry.FullName), m.ToArray()));
        }
        return result;
    }

    /// <summary>
    /// Baixa uma família do Google Fonts pelo nome (API de CSS, que entrega TTF para clientes que não pedem WOFF2)
    /// e a licença do repositório público google/fonts.
    /// </summary>
    public async Task<FontImportResult> ImportGoogleFontAsync(string familyName, CancellationToken ct = default)
    {
        var name = Regex.Replace(familyName.Trim(), @"\s+", " ");
        if (!GoogleFamilyName().IsMatch(name))
            throw new FontImportException(Resources.Strings.Fonts_GoogleInvalidName);
        var query = Uri.EscapeDataString(name).Replace("%20", "+");

        string css;
        try
        {
            css = await GetCssAsync($"family={query}:ital,wght@0,400;0,700;1,400;1,700", ct)
                  ?? await GetCssAsync($"family={query}:wght@400;700", ct)
                  ?? await GetCssAsync($"family={query}", ct)
                  ?? throw new FontImportException(string.Format(Resources.Strings.Fonts_GoogleNotFound, name));
        }
        catch (HttpRequestException ex)
        {
            throw new FontImportException(Resources.Strings.Fonts_GoogleOffline, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new FontImportException(Resources.Strings.Fonts_GoogleOffline, ex);
        }

        var faces = FontFaceBlock().Matches(css)
            .Select(m => m.Groups[1].Value)
            .Select(b => (
                Url: FaceUrl().Match(b).Groups[1].Value,
                Weight: FaceWeight().Match(b) is { Success: true } w ? w.Groups[1].Value : "400",
                Italic: b.Contains("font-style: italic", StringComparison.Ordinal)))
            .Where(f => f.Url.StartsWith("https://fonts.gstatic.com/", StringComparison.Ordinal))
            .DistinctBy(f => (f.Weight, f.Italic))
            .ToList();
        if (faces.Count == 0)
            throw new FontImportException(string.Format(Resources.Strings.Fonts_GoogleNotFound, name));

        var fonts = new List<(string, byte[])>();
        var fileBase = Regex.Replace(name, "[^A-Za-z0-9]", "");
        try
        {
            foreach (var face in faces)
            {
                var data = await http.GetByteArrayAsync(face.Url, ct);
                if (data.Length > MaxFontFileBytes)
                    throw new FontImportException(Resources.Strings.Fonts_FileTooLarge);
                fonts.Add(($"{fileBase}-{face.Weight}{(face.Italic ? "Italic" : "")}.ttf", data));
            }
        }
        catch (HttpRequestException ex)
        {
            throw new FontImportException(Resources.Strings.Fonts_GoogleOffline, ex);
        }

        var license = await LicenseAsync(fileBase.ToLowerInvariant(), ct);
        return new FontImportResult(await AddFontsAsync(fonts, license, ct), license?.Name);
    }

    private async Task<string?> GetCssAsync(string query, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://fonts.googleapis.com/css2?{query}");
        // Sem um navegador conhecido no User-Agent a API responde com links TTF (o Avalonia não lê WOFF2).
        request.Headers.UserAgent.ParseAdd("Confluir/1.0");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<(string Name, string Text)?> LicenseAsync(string slug, CancellationToken ct)
    {
        (string Folder, string File, string Name)[] candidates =
        [
            ("ofl", "OFL.txt", "SIL Open Font License 1.1"),
            ("apache", "LICENSE.txt", "Apache License 2.0"),
            ("ufl", "UFL.txt", "Ubuntu Font Licence 1.0"),
        ];
        foreach (var (folder, file, licenseName) in candidates)
        {
            try
            {
                using var response = await http.GetAsync($"https://raw.githubusercontent.com/google/fonts/main/{folder}/{slug}/{file}", ct);
                if (response.IsSuccessStatusCode)
                    return (licenseName, await response.Content.ReadAsStringAsync(ct));
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>Valida as fontes, agrupa por família e grava em &lt;dados&gt;/fonts/&lt;família&gt;; recarrega a coleção.</summary>
    private async Task<IReadOnlyList<string>> AddFontsAsync(List<(string Name, byte[] Data)> fonts,
        (string Name, string Text)? license, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "confluir-fonts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var byFamily = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (fileName, data) in fonts)
            {
                var path = Path.Combine(temp, SafeName(fileName));
                await File.WriteAllBytesAsync(path, data, ct);
                // Lê a família de dentro do arquivo; arquivos que não são fontes válidas ficam de fora.
                var probe = new FileFontCollection(new Uri($"fonts:probe{Guid.NewGuid():N}", UriKind.Absolute));
                if (!probe.Add(new Uri(path)))
                    continue;
                foreach (var family in probe.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!byFamily.TryGetValue(family, out var list))
                        byFamily[family] = list = [];
                    list.Add(path);
                }
            }
            if (byFamily.Count == 0)
                throw new FontImportException(Resources.Strings.Fonts_NoFontFiles);

            foreach (var (family, files) in byFamily)
            {
                var dir = Path.Combine(paths.FontsDirectory, SafeName(family));
                Directory.CreateDirectory(dir);
                foreach (var file in files)
                    File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
                if (license is { } l)
                    await File.WriteAllTextAsync(Path.Combine(dir, "LICENSE.txt"), $"{l.Name}\n\n{l.Text}", ct);
                _cssCache.Remove($"imported:{family}");
            }
            LoadImported();
            logger.LogInformation("Fontes importadas: {Families}", string.Join(", ", byFamily.Keys));
            return byFamily.Keys.ToList();
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Carrega (ou recarrega) a coleção das fontes importadas a partir da pasta de fontes.</summary>
    private void LoadImported()
    {
        var families = new List<string>();
        var collection = new FileFontCollection(ImportedKey);
        if (Directory.Exists(paths.FontsDirectory))
        {
            foreach (var dir in Directory.GetDirectories(paths.FontsDirectory))
            {
                try
                {
                    var added = false;
                    foreach (var file in FontFilesIn(dir))
                        added |= collection.Add(new Uri(file));
                    if (added)
                        families.Add(Path.GetFileName(dir));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Fonte importada ignorada: {Folder}", Path.GetFileName(dir));
                }
            }
        }
        // A família registrada pode ter nome diferente da pasta (pastas usam um nome seguro).
        var registered = collection.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _importedFamilies = families
            .Select(folder => registered.FirstOrDefault(r => SafeName(r) == folder) ?? folder)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        try
        {
            FontManager.Current.AddFontCollection(collection);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao registrar as fontes importadas");
        }
    }

    private static IEnumerable<string> FontFilesIn(string dir) =>
        Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                                                 f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase));

    /// <summary>Licença gravada junto da fonte importada (primeira linha), se houver.</summary>
    public string? ImportedLicense(string family)
    {
        var file = Path.Combine(paths.FontsDirectory, SafeName(family), "LICENSE.txt");
        return File.Exists(file) ? File.ReadLines(file).FirstOrDefault() : null;
    }

    private static string SafeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ]{1,59}$")]
    private static partial Regex GoogleFamilyName();

    [GeneratedRegex(@"@font-face\s*\{([^}]*)\}")]
    private static partial Regex FontFaceBlock();

    [GeneratedRegex(@"url\(([^)]+)\)")]
    private static partial Regex FaceUrl();

    [GeneratedRegex(@"font-weight:\s*(\d+)")]
    private static partial Regex FaceWeight();
}
