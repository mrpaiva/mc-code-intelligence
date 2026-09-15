namespace DeclIndex.UnitTests;

[TestClass]
public class BlobStoreTests
{
	private string raiz = "";

	[TestInitialize]
	public void Preparar() => raiz = Directory.CreateTempSubdirectory("declindex-").FullName;

	[TestCleanup]
	public void Limpar() => Directory.Delete(raiz, true);

	[TestMethod]
	public void Blob_gravado_é_lido_de_volta_e_reconhecido_como_presente()
	{
		var store = new BlobStore(raiz);
		var linhas = new[] { new Declaration("class", "C", "A", "public", "", "", "", 1) };

		store.Write("abcdef0123", linhas);

		store.Contains("abcdef0123").Should().BeTrue();
		store.Read("abcdef0123").Should().Equal(linhas);
		File.Exists(Path.Combine(raiz, "blobs", "ab", "abcdef0123.tsv")).Should().BeTrue();
	}

	[TestMethod]
	public void Conjunto_de_símbolos_novo_invalida_os_blobs_e_fica_registrado()
	{
		var store = new BlobStore(raiz);
		store.Write("aa11", [new Declaration("class", "C", "", "", "", "", "", 1)]);
		AtomicFile.WriteLines(Path.Combine(raiz, "worktrees", "w.tsv"), ["velho"]);

		store.EnsureSymbols(["PAF"]).Should().BeTrue();

		store.Contains("aa11").Should().BeFalse();
		Directory.Exists(Path.Combine(raiz, "worktrees")).Should().BeFalse("TSV materializado com símbolos antigos não pode sobreviver");
		new BlobStore(raiz).Symbols.Should().BeEquivalentTo(["PAF"]);
		new BlobStore(raiz).EnsureSymbols(["PAF"]).Should().BeFalse();
	}

	[TestMethod]
	public void Poda_remove_blobs_sem_referência_e_preserva_os_referenciados()
	{
		var store = new BlobStore(raiz);
		store.Write("aa11", []);
		store.Write("bb22", []);

		store.Prune(new HashSet<string> { "bb22" });

		store.Contains("aa11").Should().BeFalse();
		store.Contains("bb22").Should().BeTrue();
	}

	[TestMethod]
	public void Escrita_atômica_não_deixa_arquivo_novo_para_trás()
	{
		var alvo = Path.Combine(raiz, "x.tsv");

		AtomicFile.WriteLines(alvo, ["a", "b"]);

		File.ReadAllLines(alvo).Should().Equal("a", "b");
		File.ReadAllText(alvo).Should().Be("a\nb\n", "o rg não casa $ antes de \\r");
		File.Exists(alvo + ".novo").Should().BeFalse();
	}
}
