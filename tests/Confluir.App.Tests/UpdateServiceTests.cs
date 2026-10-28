using System.Runtime.InteropServices;
using Confluir.App.Services;

namespace Confluir.App.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("1.4", "1.4.0")]
    [InlineData("0.3.1-beta.2", "0.3.1")]
    [InlineData("0.1.0+abc123", "0.1.0")]
    public void Versao_da_tag(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateService.ParseVersion(tag));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData(null)]
    public void Tag_sem_versao(string? tag) => Assert.Null(UpdateService.ParseVersion(tag));

    [Fact]
    public void Instalador_de_cada_sistema()
    {
        Assert.Equal("Confluir-Setup-x64.exe", UpdateService.AssetFor(OSPlatform.Windows, Architecture.X64));
        Assert.Null(UpdateService.AssetFor(OSPlatform.Windows, Architecture.Arm64));
        Assert.Equal("confluir_amd64.deb", UpdateService.AssetFor(OSPlatform.Linux, Architecture.X64));
        Assert.Equal("Confluir-macOS-arm64.dmg", UpdateService.AssetFor(OSPlatform.OSX, Architecture.Arm64));
        Assert.Equal("Confluir-macOS-x64.dmg", UpdateService.AssetFor(OSPlatform.OSX, Architecture.X64));
    }

    private const string Release = """
        {
          "tag_name": "v0.2.0", "draft": false, "prerelease": false,
          "html_url": "https://github.com/igoreusttaquio/Confluir/releases/tag/v0.2.0",
          "assets": [
            { "name": "Confluir-Setup-x64.exe", "browser_download_url": "https://example.com/setup.exe" },
            { "name": "confluir_amd64.deb", "browser_download_url": "https://example.com/c.deb" },
            { "name": "SHA256SUMS.txt", "browser_download_url": "https://example.com/sums" }
          ]
        }
        """;

    [Fact]
    public void Le_a_versao_publicada_e_o_instalador()
    {
        var info = UpdateService.ParseRelease(Release, "confluir_amd64.deb");
        Assert.NotNull(info);
        Assert.Equal(new Version(0, 2, 0), info.Version);
        Assert.Equal("https://example.com/c.deb", info.InstallerUrl);
        Assert.Equal("confluir_amd64.deb", info.InstallerName);
        Assert.Equal("https://example.com/sums", info.ChecksumsUrl);
        Assert.EndsWith("/v0.2.0", info.PageUrl);
    }

    [Fact]
    public void Sem_instalador_deste_sistema_fica_so_a_pagina()
    {
        var info = UpdateService.ParseRelease(Release, "Confluir-macOS-arm64.dmg");
        Assert.NotNull(info);
        Assert.Null(info.InstallerUrl);
    }

    [Fact]
    public void Pre_lancamento_e_ignorado() =>
        Assert.Null(UpdateService.ParseRelease(Release.Replace("\"prerelease\": false", "\"prerelease\": true"), "confluir_amd64.deb"));

    [Fact]
    public void Hash_do_arquivo_na_lista_do_sha256sum()
    {
        const string sums = "aaa111  Confluir-Setup-x64.exe\nbbb222 *confluir_amd64.deb\n";
        Assert.Equal("aaa111", UpdateService.ChecksumFor(sums, "Confluir-Setup-x64.exe"));
        Assert.Equal("bbb222", UpdateService.ChecksumFor(sums, "confluir_amd64.deb"));
        Assert.Null(UpdateService.ChecksumFor(sums, "outro.dmg"));
    }
}
