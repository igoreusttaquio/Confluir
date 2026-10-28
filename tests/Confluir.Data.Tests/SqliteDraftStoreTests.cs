using Confluir.Core.Compose;
using Confluir.Core.Models;
using Microsoft.Data.Sqlite;

namespace Confluir.Data.Tests;

public sealed class SqliteDraftStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "confluir-tests", Guid.NewGuid().ToString("N"));
    private readonly Database _database;
    private readonly SqliteDraftStore _store;
    private readonly Guid _account = Guid.NewGuid();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public SqliteDraftStoreTests()
    {
        _database = new Database(Path.Combine(_dir, "confluir.db"));
        _store = new SqliteDraftStore(_database);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private Draft Draft(string subject, DateTimeOffset? updated = null) => new(
        Guid.NewGuid(), _account, ComposeMode.Reply, "Ana <ana@exemplo.com>, bia@", "", "", subject,
        "<div><b>Olá</b></div>", "Olá", IsPlainText: false,
        [new OutgoingAttachment("a.pdf", 10, FilePath: @"C:\a.pdf"), new OutgoingAttachment("b.png", 5, SourceMessageId: Guid.NewGuid(), SourceIndex: 1)],
        "<id@x>", ["<r1@x>", "<id@x>"], updated ?? DateTimeOffset.UnixEpoch, new ServerDraft("Drafts", 42));

    [Fact]
    public async Task Guarda_e_le_o_rascunho_completo()
    {
        var draft = Draft("Re: Proposta");
        await _store.SaveAsync(draft, _ct);

        var read = await _store.GetAsync(draft.Id, _ct);

        Assert.NotNull(read);
        Assert.Equal(draft with { Attachments = [], References = [] }, read with { Attachments = [], References = [] });
        Assert.Equal(draft.Attachments, read.Attachments);
        Assert.Equal(draft.References, read.References);
    }

    [Fact]
    public async Task Salvar_de_novo_substitui_e_lista_do_mais_recente()
    {
        var older = Draft("Antigo", DateTimeOffset.UnixEpoch.AddDays(1));
        var newer = Draft("Novo", DateTimeOffset.UnixEpoch.AddDays(2));
        await _store.SaveAsync(older, _ct);
        await _store.SaveAsync(newer, _ct);
        await _store.SaveAsync(older with { Subject = "Antigo editado", NeedsUpload = false }, _ct);

        var all = await _store.ListAsync(_ct);

        Assert.Equal(["Novo", "Antigo editado"], all.Select(d => d.Subject));
        Assert.False(all[1].NeedsUpload);
    }

    [Fact]
    public async Task Excluir_e_remover_a_conta_apagam_rascunhos()
    {
        var a = Draft("A");
        var b = Draft("B");
        await _store.SaveAsync(a, _ct);
        await _store.SaveAsync(b, _ct);

        await _store.DeleteAsync(a.Id, _ct);
        Assert.Null(await _store.GetAsync(a.Id, _ct));

        await new SqliteMailCache(_database).DeleteAccountAsync(_account, _ct);
        Assert.Empty(await _store.ListAsync(_ct));
    }
}
