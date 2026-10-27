namespace Confluir.Core.Tests;

public class StableIdTests
{
    [Fact]
    public void Mesmas_partes_geram_o_mesmo_id()
    {
        var account = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
        Assert.Equal(StableId.From(account, "INBOX", 1u, 42u), StableId.From(account, "INBOX", 1u, 42u));
    }

    [Fact]
    public void Partes_diferentes_geram_ids_diferentes()
    {
        var account = Guid.NewGuid();
        Assert.NotEqual(StableId.From(account, "INBOX", 1u, 42u), StableId.From(account, "INBOX", 1u, 43u));
        Assert.NotEqual(StableId.From(account, "INBOX", 1u, 42u), StableId.From(account, "INBOX", 2u, 42u));
        // O separador impede colisões por concatenação ("ab"+"c" vs "a"+"bc").
        Assert.NotEqual(StableId.From("ab", "c"), StableId.From("a", "bc"));
    }
}
