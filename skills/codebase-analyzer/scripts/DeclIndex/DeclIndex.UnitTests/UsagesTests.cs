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

	private List<Usage> UsosComTexto(string símbolo, params string[] inclusões)
	{
		Refresh.Run(raiz, store);
		return Usages.Find(store, raiz, símbolo, null, inclusões, withText: true);
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

	[TestMethod]
	public void Com_texto_cada_uso_traz_a_própria_linha_sem_espaços_nas_pontas()
	{
		File.WriteAllText(Path.Combine(raiz, "App", "View.xaml"), "<Grid>\n\t\t<Button Content=\"Confirmação\" AutomationProperties.AutomationId=\"Sale.Button.Confirm\" />  \r\n\n    <TextBox AutomationProperties.AutomationId=\"Sale.Input.Value\"/>\n</Grid>");

		UsosComTexto("AutomationId", "*.xaml").Should().Equal(
			new Usage("App/View.xaml", 2, "<Button Content=\"Confirmação\" AutomationProperties.AutomationId=\"Sale.Button.Confirm\" />"),
			new Usage("App/View.xaml", 4, "<TextBox AutomationProperties.AutomationId=\"Sale.Input.Value\"/>"));
	}

	[TestMethod]
	public void Linha_acima_de_200_caracteres_é_cortada_com_reticências_sem_partir_emoji()
	{
		var exata = "Total " + new string('x', 194);
		var longa = "Total " + new string('y', 195);
		var comEmoji = "Total " + new string('a', 193) + "😀 fim";
		File.WriteAllText(Path.Combine(raiz, "Lib", "Longa.txt"), $"\t{exata}\n{longa}\n{comEmoji}");

		UsosComTexto("Total", "Longa.txt").Select(uso => uso.Text).Should().Equal(exata, longa[..200] + "…", comEmoji[..199] + "…");
	}

	[TestMethod]
	public void Com_ou_sem_texto_os_caminhos_e_as_linhas_são_os_mesmos_e_sem_pedir_não_vem_texto()
	{
		var semTexto = Usos("Total");
		var comTexto = UsosComTexto("Total");

		semTexto.Should().NotBeEmpty().And.OnlyContain(uso => uso.Text == null);
		comTexto.Should().OnlyContain(uso => uso.Text != null);
		comTexto.Select(uso => uso with { Text = null }).Should().Equal(semTexto);
	}
}
