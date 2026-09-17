using System.Diagnostics;

namespace DeclIndex.UnitTests;

[TestClass]
public class UsagesTests
{
	private string raiz = "";
	private string store = "";

	[TestInitialize]
	public void Preparar()
	{
		raiz = Directory.CreateTempSubdirectory("declindex-usos-").FullName;
		store = Directory.CreateTempSubdirectory("declindex-store-").FullName;
		Git("init", "-q");
		Git("config", "user.email", "t@t");
		Git("config", "user.name", "t");
		Directory.CreateDirectory(Path.Combine(raiz, "App", "bin"));
		Directory.CreateDirectory(Path.Combine(raiz, "Lib"));
		File.WriteAllText(Path.Combine(raiz, "App", "A.cs"), "namespace N { public class A { public void Save() { Total(); } } }");
		File.WriteAllText(Path.Combine(raiz, "App", "App.config"), "<add key=\"Total\" />\n<add key=\"Totals\" />\n<add key=\"Total\" />");
		File.WriteAllText(Path.Combine(raiz, "App", "bin", "saida.txt"), "Total");
		File.WriteAllText(Path.Combine(raiz, "Lib", "B.cs"), "class B { int Total; }");
		File.WriteAllText(Path.Combine(raiz, "Lib", "C.cs"), "class B { int Total; }");
		Git("add", ".");
		Git("commit", "-q", "-m", "inicial");
	}

	[TestCleanup]
	public void Limpar()
	{
		foreach (var arquivo in Directory.EnumerateFiles(raiz, "*", SearchOption.AllDirectories)) File.SetAttributes(arquivo, FileAttributes.Normal);

		Directory.Delete(raiz, true);
		Directory.Delete(store, true);
	}

	private void Git(params string[] args)
	{
		var process = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = raiz, RedirectStandardOutput = true, RedirectStandardError = true })!;
		process.WaitForExit();
		process.ExitCode.Should().Be(0, process.StandardError.ReadToEnd());
	}

	private List<Usage> Usos(string símbolo, string? escopo = null, params string[] inclusões)
	{
		Refresh.Run(raiz, store);
		return Usages.Find(store, raiz, símbolo, escopo, inclusões);
	}

	[TestMethod]
	public void Uso_em_cs_e_em_config_sai_por_caminho_e_linha_uma_vez_por_linha_e_fora_de_bin()
		=> Usos("Total").Should().Equal(new Usage("App/A.cs", 1), new Usage("App/App.config", 1), new Usage("App/App.config", 3), new Usage("Lib/B.cs", 1), new Usage("Lib/C.cs", 1));

	[TestMethod]
	public void Mesmo_conteúdo_em_dois_caminhos_aparece_nos_dois()
		=> Usos("B").Select(uso => uso.Path).Should().Equal("Lib/B.cs", "Lib/C.cs");

	[TestMethod]
	public void Escopo_e_inclusão_restringem_os_caminhos()
	{
		Usos("Total", "Lib").Select(uso => uso.Path).Should().Equal("Lib/B.cs", "Lib/C.cs");
		Usos("Total", @".\App\").Select(uso => uso.Path).Should().Equal("App/A.cs", "App/App.config", "App/App.config");
		Usos("Total", null, "*.cs").Select(uso => uso.Path).Should().Equal("App/A.cs", "Lib/B.cs", "Lib/C.cs");
		Usos("Total", null, "*.config", "Lib/*.cs").Select(uso => uso.Path).Should().Equal("App/App.config", "App/App.config", "Lib/B.cs", "Lib/C.cs");
	}

	[TestMethod]
	public void Arquivo_modificado_sem_commit_é_consultado_no_conteúdo_novo()
	{
		Usos("Load").Should().BeEmpty();
		File.WriteAllText(Path.Combine(raiz, "App", "A.cs"), "namespace N { public class A {\npublic void Load() { } } }");
		var marcador = Path.Combine(store, "worktrees", WorktreeIndex.KeyFor(raiz) + ".dirty");
		File.WriteAllText(marcador, "");

		Usos("Load").Should().Equal(new Usage("App/A.cs", 2));
		Usos("Save").Should().BeEmpty("o blob antigo continua no corpus, mas não está mais no manifesto");
	}

	[TestMethod]
	public void Símbolo_ausente_não_acha_nada()
		=> Usos("Inexistente").Should().BeEmpty();
}
