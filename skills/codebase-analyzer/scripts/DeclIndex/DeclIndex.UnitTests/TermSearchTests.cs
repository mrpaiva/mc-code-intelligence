using System.Diagnostics;

namespace DeclIndex.UnitTests;

[TestClass]
public class TermSearchTests
{
	private string raiz = "";
	private string store = "";

	[TestInitialize]
	public void Preparar()
	{
		raiz = Directory.CreateTempSubdirectory("declindex-busca-").FullName;
		store = Directory.CreateTempSubdirectory("declindex-store-").FullName;
		Git("init", "-q");
		Git("config", "user.email", "t@t");
		Git("config", "user.name", "t");
		Directory.CreateDirectory(Path.Combine(raiz, "App"));
		File.WriteAllText(Path.Combine(raiz, "App", "VoucherCancel.cs"), "class VoucherCancel\n{\n\tvoid Cancel(Voucher voucher) { Cancel(voucher); }\n\tvoid Voucher() { }\n}\n");
		File.WriteAllText(Path.Combine(raiz, "App", "VoucherList.cs"), "class VoucherList\n{\n\tvoid List(Voucher voucher) { }\n}\n");
		File.WriteAllText(Path.Combine(raiz, "App", "Cancel.txt"), "Cancel\n");
		File.WriteAllText(Path.Combine(raiz, "App", "Outro.cs"), string.Concat(Enumerable.Repeat("class Outro { }\n", 50)));
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

	private List<RankedFile> Busca(string[] termos, int top = 20, string? escopo = null, params string[] inclusões)
	{
		Refresh.Run(raiz, store);
		return TermSearch.Rank(store, raiz, termos, top, escopo, inclusões);
	}

	[TestMethod]
	public void Arquivo_com_os_dois_termos_e_mais_ocorrências_vem_primeiro_e_quem_não_tem_nenhum_fica_de_fora()
	{
		var resultado = Busca(["Voucher", "Cancel"]);

		resultado.Select(arquivo => arquivo.Path).Should().Equal("App/VoucherCancel.cs", "App/Cancel.txt", "App/VoucherList.cs");
		resultado[0].Terms.Should().Equal("Voucher", "Cancel");
		resultado[0].Lines.Should().Equal([3, 4], "VoucherCancel na linha 1 não é a palavra Voucher");
		resultado[1].Terms.Should().Equal("Cancel");
		resultado[0].Score.Should().BeGreaterThan(resultado[1].Score);
	}

	[TestMethod]
	public void Com_o_mesmo_df_o_arquivo_mais_curto_pontua_mais()
	{
		var resultado = Busca(["Voucher", "Cancel"]);
		var cancel = resultado.Single(arquivo => arquivo.Path == "App/Cancel.txt");
		var list = resultado.Single(arquivo => arquivo.Path == "App/VoucherList.cs");

		cancel.Score.Should().BeGreaterThan(list.Score, "Cancel e Voucher aparecem em 2 arquivos cada; Cancel.txt tem 1 linha e VoucherList.cs 4");
	}

	[TestMethod]
	public void Top_inclusão_e_escopo_limitam_a_resposta()
	{
		Busca(["Voucher"], top: 1).Should().ContainSingle().Which.Path.Should().Be("App/VoucherCancel.cs");
		Busca(["Cancel"], 20, null, "*.txt").Select(arquivo => arquivo.Path).Should().Equal("App/Cancel.txt");
		Busca(["Cancel"], 20, "Lib").Should().BeEmpty();
	}

	[TestMethod]
	public void Termo_sem_ocorrência_não_acha_nada()
		=> Busca(["Inexistente"]).Should().BeEmpty();
}
