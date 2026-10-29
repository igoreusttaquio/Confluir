using System.Xml.Linq;
using Confluir.App.ViewModels;

namespace Confluir.App.Tests;

public sealed class CreditsTests
{
    [Fact]
    public void Todo_pacote_do_app_tem_credito_na_tela_Sobre()
    {
        var root = FindRoot();
        var packages = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(p => XDocument.Load(p).Descendants("PackageReference"))
            .Select(r => (string)r.Attribute("Include")!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var credited = AboutViewModel.Credits.SelectMany(g => g.Items).SelectMany(c => c.Packages)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(packages);
        Assert.Empty(packages.Except(credited));
    }

    [Fact]
    public void Creditos_tem_autor_licenca_e_link()
    {
        Assert.All(AboutViewModel.Credits.SelectMany(g => g.Items), c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Author), c.Name);
            Assert.False(string.IsNullOrWhiteSpace(c.License), c.Name);
            Assert.StartsWith("https://", c.Url);
        });
    }

    /// <summary>A pasta com Confluir.sln, subindo a partir deste arquivo de código (os testes rodam fora do repositório).</summary>
    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(source) ?? "", AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Confluir.sln")))
                    return dir.FullName;
            }
        }
        Assert.Skip("Repositório não encontrado a partir da pasta dos testes.");
        return "";
    }
}
