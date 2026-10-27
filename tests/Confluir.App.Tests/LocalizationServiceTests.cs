using Confluir.App.Localization;
using Confluir.App.Resources;

namespace Confluir.App.Tests;

// O serviço altera a cultura global do processo: estes testes não rodam em paralelo com outros.
[Collection(nameof(GlobalCultureCollection))]
public sealed class LocalizationServiceTests : IDisposable
{
    private readonly LocalizationService _loc = LocalizationService.Instance;

    public void Dispose() => _loc.Apply("pt-BR");

    [Theory]
    [InlineData("pt-BR", 1, "1 mensagem")]
    [InlineData("pt-BR", 1500, "1.500 mensagens")]
    [InlineData("en", 1, "1 message")]
    [InlineData("en", 1500, "1,500 messages")]
    [InlineData("es", 1, "1 mensaje")]
    [InlineData("es", 1500, "1.500 mensajes")]
    public void Plural_e_numeros_seguem_o_idioma(string language, int count, string expected)
    {
        _loc.Apply(language);
        Assert.Equal(expected, _loc.Plural("MessageList_Count", count));
    }

    [Fact]
    public void Troca_atualiza_indexador_classe_gerada_e_avisa()
    {
        _loc.Apply("pt-BR");
        var notified = 0;
        var versionBefore = _loc.Version;
        _loc.LanguageChanged += Handler;
        try
        {
            _loc.Apply("es");

            Assert.Equal("es", _loc.Language);
            Assert.Equal("Bandeja de entrada", _loc["Folder_Inbox"]);
            Assert.Equal("Bandeja de entrada", Strings.Folder_Inbox);
            Assert.Equal(1, notified);
            Assert.True(_loc.Version > versionBefore);

            _loc.Apply("es"); // sem mudança, sem aviso
            Assert.Equal(1, notified);
        }
        finally
        {
            _loc.LanguageChanged -= Handler;
        }

        void Handler(object? s, EventArgs e) => notified++;
    }

    [Fact]
    public void Chave_inexistente_aparece_marcada()
    {
        Assert.Equal("#NaoExiste", _loc["NaoExiste"]);
    }
}

[CollectionDefinition(nameof(GlobalCultureCollection), DisableParallelization = true)]
public sealed class GlobalCultureCollection;
