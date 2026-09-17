using System.Diagnostics;

namespace DeclIndex.UnitTests;

[TestClass]
public class RefreshTests
{
	private string raiz = "";
	private string store = "";

	[TestInitialize]
	public void Preparar()
	{
		raiz = Directory.CreateTempSubdirectory("declindex-repo-").FullName;
		store = Directory.CreateTempSubdirectory("declindex-store-").FullName;
		Git("init", "-q");
		Git("config", "user.email", "t@t");
		Git("config", "user.name", "t");
		Directory.CreateDirectory(Path.Combine(raiz, "App"));
		File.WriteAllText(Path.Combine(raiz, "App", "App.csproj"), "<Project />");
		File.WriteAllText(Path.Combine(raiz, "App", "A.cs"), "namespace N { public class A { public void Save() { } } }");
		File.WriteAllText(Path.Combine(raiz, "App", "B.cs"), "#if PAF\nnamespace N { class B { } }\n#endif");
		Git("add", ".");
		Git("commit", "-q", "-m", "inicial");
	}

	[TestCleanup]
	public void Limpar()
	{
		ApagarÁrvore(raiz);
		ApagarÁrvore(store);
	}

	/// <summary>Os objetos do .git nascem somente-leitura; Directory.Delete recusa sem limpar o atributo.</summary>
	private static void ApagarÁrvore(string caminho)
	{
		foreach (var arquivo in Directory.EnumerateFiles(caminho, "*", SearchOption.AllDirectories))
		{
			File.SetAttributes(arquivo, FileAttributes.Normal);
		}

		Directory.Delete(caminho, true);
	}

	private void Git(params string[] args)
	{
		var process = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = raiz, RedirectStandardOutput = true, RedirectStandardError = true })!;
		process.WaitForExit();
		process.ExitCode.Should().Be(0, process.StandardError.ReadToEnd());
	}

	private string[] LerÍndice() => File.ReadAllLines(Refresh.Run(raiz, store).TsvPath);

	/// <summary>O que o hook PostToolUse de Edit/Write grava quando o agente edita um arquivo da worktree.</summary>
	private void MarcarEdição()
	{
		var marcador = Path.Combine(store, "worktrees", WorktreeIndex.KeyFor(raiz) + ".dirty");
		Directory.CreateDirectory(Path.GetDirectoryName(marcador)!);
		File.WriteAllText(marcador, "");
	}

	[TestMethod]
	public void Primeira_atualização_indexa_tudo_com_arquivo_projeto_e_símbolos_do_repo()
	{
		var relatório = Refresh.Run(raiz, store);
		var linhas = File.ReadAllLines(relatório.TsvPath);

		relatório.Files.Should().Be(2);
		relatório.Parsed.Should().Be(2);
		linhas.Should().Contain(l => l.StartsWith("method\tSave\tN.A\tpublic\t") && l.EndsWith("\tApp/A.cs\tApp"));
		linhas.Should().Contain(l => l.StartsWith("class\tB\tN\t"), "o #if PAF foi parseado com a união dos símbolos");
	}

	[TestMethod]
	public void Atualização_sem_mudança_não_reescreve_o_TSV()
	{
		var tsv = Refresh.Run(raiz, store).TsvPath;
		var antes = File.GetLastWriteTimeUtc(tsv);

		var relatório = Refresh.Run(raiz, store);

		relatório.Parsed.Should().Be(0);
		File.GetLastWriteTimeUtc(tsv).Should().Be(antes);
	}

	[TestMethod]
	public void Arquivo_modificado_sem_commit_é_reparseado_e_só_ele()
	{
		Refresh.Run(raiz, store);
		File.WriteAllText(Path.Combine(raiz, "App", "A.cs"), "namespace N { public class A { public void Load() { } } }");
		MarcarEdição();

		var relatório = Refresh.Run(raiz, store);

		relatório.Parsed.Should().Be(1);
		File.ReadAllLines(relatório.TsvPath).Should().Contain(l => l.StartsWith("method\tLoad\t")).And.NotContain(l => l.StartsWith("method\tSave\t"));
	}

	[TestMethod]
	public void Arquivo_novo_não_rastreado_entra_e_arquivo_apagado_sai()
	{
		Refresh.Run(raiz, store);
		File.WriteAllText(Path.Combine(raiz, "App", "C.cs"), "class C { }");
		File.Delete(Path.Combine(raiz, "App", "B.cs"));
		MarcarEdição();

		var linhas = LerÍndice();

		linhas.Should().Contain(l => l.StartsWith("class\tC\t")).And.NotContain(l => l.StartsWith("class\tB\t"));
	}

	[TestMethod]
	public void Segunda_worktree_com_o_mesmo_conteúdo_não_parseia_e_produz_o_mesmo_índice()
	{
		var primeira = Refresh.Run(raiz, store);
		var segunda = Path.Combine(Path.GetTempPath(), "declindex-clone-" + Guid.NewGuid().ToString("N"));
		Git("clone", "-q", raiz, segunda);

		try
		{
			var relatório = Refresh.Run(segunda, store);

			relatório.Parsed.Should().Be(0);
			relatório.BlobsRead.Should().Be(0, "a semente é o TSV da primeira worktree");
			File.ReadAllLines(relatório.TsvPath).Should().Equal(File.ReadAllLines(primeira.TsvPath));
		}
		finally
		{
			ApagarÁrvore(segunda);
		}
	}

	[TestMethod]
	public void Materialização_incremental_troca_só_as_linhas_do_arquivo_que_mudou_e_mantém_a_ordem()
	{
		Refresh.Run(raiz, store);
		File.WriteAllText(Path.Combine(raiz, "App", "A.cs"), "namespace N { public class A { public void Load() { } public void Save() { } } }");
		MarcarEdição();

		var relatório = Refresh.Run(raiz, store);
		var linhas = File.ReadAllLines(relatório.TsvPath);

		relatório.BlobsRead.Should().Be(1, "só o A.cs mudou; o resto veio do TSV anterior");
		linhas.Skip(1).Select(l => l.Split('\t')[8]).Should().BeInAscendingOrder(StringComparer.Ordinal);
		linhas.Where(l => l.EndsWith("\tApp/A.cs\tApp")).Should().HaveCount(3);
		linhas.Should().Contain(l => l.StartsWith("class\tB\t"));
	}

	[TestMethod]
	public void Símbolo_novo_em_arquivo_novo_invalida_e_reparseia_tudo()
	{
		Refresh.Run(raiz, store);
		File.WriteAllText(Path.Combine(raiz, "App", "D.cs"), "#if SAFRA\nclass D { }\n#endif");
		MarcarEdição();

		var relatório = Refresh.Run(raiz, store);

		relatório.Invalidated.Should().Be(1);
		relatório.Parsed.Should().Be(3);
		File.ReadAllLines(relatório.TsvPath).Should().Contain(l => l.StartsWith("class\tD\t"));
	}

	[TestMethod]
	public void Texto_que_não_é_cs_entra_no_manifesto_e_no_corpus_mas_não_no_TSV()
	{
		var relatório = Refresh.Run(raiz, store);

		relatório.Files.Should().Be(2, "só os .cs contam para o índice de declarações");
		relatório.Texts.Should().Be(3);
		relatório.CorpusAppended.Should().Be(3);
		File.ReadAllLines(WorktreeIndex.ManifestPathFor(store, raiz)).Should().Contain(l => l.EndsWith("\tApp/App.csproj"));
		File.ReadAllLines(relatório.TsvPath).Should().NotContain(l => l.Contains("App.csproj"));
		new Corpus(store).Search("Project").Should().ContainSingle();
	}

	[TestMethod]
	public void Edição_em_arquivo_que_não_é_cs_atualiza_manifesto_e_corpus_sem_rematerializar_o_TSV()
	{
		var tsv = Refresh.Run(raiz, store).TsvPath;
		var antes = File.GetLastWriteTimeUtc(tsv);
		File.WriteAllText(Path.Combine(raiz, "App", "App.csproj"), "<Project><Novo /></Project>");
		MarcarEdição();

		var relatório = Refresh.Run(raiz, store);

		relatório.Reused.Should().BeFalse();
		relatório.BlobsRead.Should().Be(0);
		relatório.CorpusAppended.Should().Be(1);
		File.GetLastWriteTimeUtc(tsv).Should().Be(antes);
		new Corpus(store).Search("Novo").Should().ContainSingle();
	}

	[TestMethod]
	public void Binário_é_registrado_no_corpus_sem_texto_e_não_é_relido()
	{
		File.WriteAllBytes(Path.Combine(raiz, "App", "logo.png"), [(byte)'S', (byte)'E', (byte)'G', 0, (byte)'R', (byte)'E', (byte)'D', (byte)'O']);
		Refresh.Run(raiz, store).CorpusAppended.Should().Be(4);
		MarcarEdição();

		var relatório = Refresh.Run(raiz, store);

		relatório.CorpusAppended.Should().Be(0);
		new Corpus(store).Search("SEG").Should().BeEmpty();
	}

	[TestMethod]
	public void Segunda_atualização_dentro_da_janela_reusa_o_TSV_sem_consultar_o_git()
	{
		var primeira = Refresh.Run(raiz, store);

		var relatório = Refresh.Run(raiz, store);

		relatório.Reused.Should().BeTrue();
		relatório.Files.Should().Be(primeira.Files);
		relatório.Phases.GitMs.Should().Be(0);
		relatório.TsvPath.Should().Be(primeira.TsvPath);
	}

	[TestMethod]
	public void Edição_sem_marcador_dentro_da_janela_é_reusada_e_só_entra_quando_o_carimbo_vence()
	{
		Refresh.Run(raiz, store);
		File.WriteAllText(Path.Combine(raiz, "App", "A.cs"), "namespace N { public class A { public void Load() { } } }");

		Refresh.Run(raiz, store).Reused.Should().BeTrue("edição por fora do agente só é vista quando a janela vence");

		Freshness.Stamp(store, raiz, DateTime.UtcNow - Freshness.Window - TimeSpan.FromSeconds(1), 2);
		var relatório = Refresh.Run(raiz, store);

		relatório.Reused.Should().BeFalse();
		File.ReadAllLines(relatório.TsvPath).Should().Contain(l => l.StartsWith("method\tLoad\t"));
	}

	[TestMethod]
	public void Marcador_de_edição_força_a_atualização_completa_e_é_consumido()
	{
		Refresh.Run(raiz, store);
		MarcarEdição();

		Refresh.Run(raiz, store).Reused.Should().BeFalse();
		Refresh.Run(raiz, store).Reused.Should().BeTrue("o marcador foi consumido pelo refresh que o viu");
	}

	[TestMethod]
	public void Marcador_gravado_durante_o_refresh_fica_para_o_próximo()
	{
		Refresh.Run(raiz, store);
		Freshness.Stamp(store, raiz, DateTime.UtcNow - TimeSpan.FromSeconds(5), 2);
		MarcarEdição();

		Refresh.Run(raiz, store).Reused.Should().BeFalse("o marcador é mais novo que o carimbo, então não foi consumido por ele");
	}

	[TestMethod]
	public void Index_do_git_reescrito_força_a_atualização_completa()
	{
		Refresh.Run(raiz, store);
		File.WriteAllText(Path.Combine(raiz, "App", "A.cs"), "namespace N { public class A { public void Load() { } } }");
		Git("add", ".");

		var relatório = Refresh.Run(raiz, store);

		relatório.Reused.Should().BeFalse("git add reescreve o index");
		File.ReadAllLines(relatório.TsvPath).Should().Contain(l => l.StartsWith("method\tLoad\t"));
	}

	[TestMethod]
	public void Worktree_secundária_resolve_o_index_pelo_arquivo_dot_git()
	{
		var secundária = Path.Combine(Path.GetTempPath(), "declindex-wt-" + Guid.NewGuid().ToString("N"));
		Git("worktree", "add", "-q", secundária);

		try
		{
			Refresh.Run(secundária, store);
			Refresh.Run(secundária, store).Reused.Should().BeTrue();

			File.WriteAllText(Path.Combine(secundária, "App", "A.cs"), "namespace N { public class A { public void Load() { } } }");
			Git("-C", secundária, "add", ".");

			Refresh.Run(secundária, store).Reused.Should().BeFalse("o index da worktree secundária mora em .git/worktrees/<nome>/index");
		}
		finally
		{
			Git("worktree", "remove", "--force", secundária);
		}
	}
}
