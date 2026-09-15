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

		var relatório = Refresh.Run(raiz, store);

		relatório.Invalidated.Should().Be(1);
		relatório.Parsed.Should().Be(3);
		File.ReadAllLines(relatório.TsvPath).Should().Contain(l => l.StartsWith("class\tD\t"));
	}
}
