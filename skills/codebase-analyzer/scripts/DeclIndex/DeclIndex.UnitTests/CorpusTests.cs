namespace DeclIndex.UnitTests;

[TestClass]
public class CorpusTests
{
	private string store = "";

	[TestInitialize]
	public void Preparar() => store = Directory.CreateTempSubdirectory("declindex-corpus-").FullName;

	[TestCleanup]
	public void Limpar() => Directory.Delete(store, true);

	private string Texto() => File.ReadAllText(Path.Combine(store, "corpus.txt"));

	private string[] Ids() => File.ReadAllLines(Path.Combine(store, "corpus.ids"));

	[TestMethod]
	public void Blob_acrescentado_ganha_id_sequencial_e_linhas_vazias_ficam_de_fora()
	{
		var corpus = new Corpus(store);

		corpus.Append([("aa11", "class A { }\n"), ("bb22", "x\r\n\r\ny")]).Should().Be(2);

		corpus.Contains("aa11").Should().BeTrue();
		corpus.ShaOf(2).Should().Be("bb22");
		Texto().Should().Be("1:1:class A { }\n2:1:x\n2:3:y\n", "a numeração é a do arquivo, mesmo pulando a linha vazia");
		Ids().Should().Equal("aa11\t16\t1", "bb22\t28\t3");
	}

	[TestMethod]
	public void Blob_já_presente_não_entra_de_novo_nem_em_outra_instância()
	{
		new Corpus(store).Append([("aa11", "a\n")]);

		new Corpus(store).Append([("aa11", "a\n"), ("bb22", "b\n")]).Should().Be(1);

		Ids().Should().HaveCount(2);
	}

	[TestMethod]
	public void Blob_sem_texto_é_registrado_sem_linhas_para_não_ser_relido()
	{
		var corpus = new Corpus(store);

		corpus.Append([("aa11", null)]).Should().Be(1);

		corpus.Contains("aa11").Should().BeTrue();
		Ids().Should().Equal("aa11\t0\t0");
	}

	[TestMethod]
	public void Busca_devolve_id_e_linha_só_de_palavra_inteira_uma_vez_por_linha()
	{
		var corpus = new Corpus(store);
		corpus.Append([("aa11", "int Total;\nvar GetTotal = Total + TotalX + Total;\nnada\n"), ("bb22", "Total\n")]);

		corpus.Search("Total").Should().Equal(new CorpusHit(1, 1), new CorpusHit(1, 2), new CorpusHit(2, 1));
		corpus.Search("GetTotal").Should().Equal(new CorpusHit(1, 2));
		corpus.Search("otal").Should().BeEmpty();
	}

	[TestMethod]
	public void Símbolo_numérico_não_casa_o_prefixo_id_linha()
	{
		var corpus = new Corpus(store);
		corpus.Append([("aa11", "x 1 y\n"), ("bb22", "nada\n")]);

		corpus.Search("1").Should().Equal(new[] { new CorpusHit(1, 1) }, "só o 1 do texto, não o do prefixo");
		corpus.Search("2").Should().BeEmpty();
	}

	[TestMethod]
	public void Acento_faz_parte_da_palavra()
	{
		var corpus = new Corpus(store);
		corpus.Append([("aa11", "var Município = 1;\n")]);

		corpus.Search("Município").Should().Equal(new CorpusHit(1, 1));
		corpus.Search("Munic").Should().BeEmpty();
	}

	[TestMethod]
	public void Cauda_gravada_pela_metade_é_ignorada_na_busca_e_truncada_no_próximo_acréscimo()
	{
		new Corpus(store).Append([("aa11", "a\n")]);
		File.AppendAllText(Path.Combine(store, "corpus.txt"), "2:1:fantasma");

		new Corpus(store).Search("fantasma").Should().BeEmpty();

		var corpus = new Corpus(store);
		corpus.Append([("bb22", "b\n")]);

		Texto().Should().Be("1:1:a\n2:1:b\n");
		corpus.Search("b").Should().Equal(new CorpusHit(2, 1));
	}

	[TestMethod]
	public void Poda_mantém_só_os_referenciados_e_renumera()
	{
		var corpus = new Corpus(store);
		corpus.Append([("aa11", "a\n"), ("bb22", "b\n"), ("cc33", "c\n")]);

		corpus.Prune(new HashSet<string> { "bb22", "cc33" });

		corpus.Count.Should().Be(2);
		corpus.ShaOf(1).Should().Be("bb22");
		Texto().Should().Be("1:1:b\n2:1:c\n");
		corpus.Search("c").Should().Equal(new CorpusHit(2, 1));
		new Corpus(store).Contains("aa11").Should().BeFalse();
	}

	[TestMethod]
	public void Corpus_vazio_não_acha_nada()
		=> new Corpus(store).Search("x").Should().BeEmpty();
}
