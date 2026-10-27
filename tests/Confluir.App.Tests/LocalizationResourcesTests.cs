using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Confluir.App.Tests;

/// <summary>Garante que nenhuma tradução fique faltando ou quebrada (pt-BR, en, es).</summary>
public partial class LocalizationResourcesTests
{
    private static readonly string AppDir = Path.Combine(RepoRoot(), "src", "Confluir.App");
    private static readonly string ResourcesDir = Path.Combine(AppDir, "Resources");

    public static TheoryData<string> Translations => new() { "en", "es" };

    [Theory]
    [MemberData(nameof(Translations))]
    public void Traducao_tem_exatamente_as_mesmas_chaves(string language)
    {
        var neutral = Load("Strings.resx");
        var translated = Load($"Strings.{language}.resx");

        Assert.Empty(neutral.Keys.Except(translated.Keys));    // faltando na tradução
        Assert.Empty(translated.Keys.Except(neutral.Keys));    // sobrando na tradução
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void Traducao_preserva_os_marcadores(string language)
    {
        var neutral = Load("Strings.resx");
        var translated = Load($"Strings.{language}.resx");

        foreach (var (key, value) in neutral)
        {
            if (!translated.TryGetValue(key, out var other))
                continue;
            Assert.True(Placeholders(value).SetEquals(Placeholders(other)),
                $"{language}: marcadores diferentes em '{key}'");
        }
    }

    [Fact]
    public void Nenhuma_traducao_vazia()
    {
        foreach (var file in new[] { "Strings.resx", "Strings.en.resx", "Strings.es.resx" })
            Assert.All(Load(file), kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), $"{file}: '{kv.Key}' vazia"));
    }

    [Fact]
    public void Xaml_nao_usa_strings_estaticas_e_so_chaves_existentes()
    {
        var keys = Load("Strings.resx").Keys.ToHashSet();
        foreach (var file in Directory.EnumerateFiles(AppDir, "*.axaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                 !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var xaml = File.ReadAllText(file);
            // x:Static não atualiza ao trocar de idioma.
            Assert.DoesNotContain("res:Strings.", xaml);
            foreach (Match m in TrUsage().Matches(xaml))
                Assert.Contains(m.Groups[1].Value, keys);
        }
    }

    private static Dictionary<string, string> Load(string file) =>
        XDocument.Load(Path.Combine(ResourcesDir, file)).Root!
            .Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    private static HashSet<string> Placeholders(string value) =>
        Placeholder().Matches(value).Select(m => m.Groups[1].Value).ToHashSet();

    // Parte do caminho deste arquivo-fonte (gravado na compilação), e não da pasta de saída do build,
    // que pode estar fora do repositório (ex.: --artifacts-path).
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Confluir.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Confluir.sln não encontrado acima dos testes.");
    }

    [GeneratedRegex(@"\{(\d+)(?:[:,][^}]*)?\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\{l:Tr\s+([A-Za-z0-9_]+)\}")]
    private static partial Regex TrUsage();
}
