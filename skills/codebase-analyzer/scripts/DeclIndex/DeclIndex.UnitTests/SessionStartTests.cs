using System.Text.Json;
using DeclIndex.Hook;

namespace DeclIndex.UnitTests;

[TestClass]
public class SessionStartTests
{
	private string plugin = "";
	private string code = "";
	private string scripts = "";
	private string exe = "";

	[TestInitialize]
	public void Preparar()
	{
		plugin = Directory.CreateTempSubdirectory("sessionstart-plugin-").FullName;
		code = Directory.CreateTempSubdirectory("sessionstart-code-").FullName;
		scripts = Path.Combine(plugin, "skills", "codebase-analyzer", "scripts");
		exe = Path.Combine(scripts, "DeclIndex", "DeclIndex", "bin", "Release", "net10.0", "DeclIndex.exe");
		Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
		File.WriteAllText(exe, "");
		Directory.CreateDirectory(Path.Combine(plugin, "hooks"));
		File.WriteAllText(Path.Combine(plugin, "hooks", "routing.md"), "# Roteamento\n\nScripts em `{{SCRIPTS}}`; detalhes em `{{REFERENCE}}`.\n");
		Directory.CreateDirectory(Path.Combine(code, "Applications"));
		Directory.CreateDirectory(Path.Combine(code, "Components"));
	}

	[TestCleanup]
	public void Limpar()
	{
		Directory.Delete(plugin, true);
		Directory.Delete(code, true);
	}

	private static HookRequest Pedido(string cwd)
		=> HookRequest.Parse(JsonSerializer.Serialize(new Dictionary<string, object?> { ["hook_event_name"] = "SessionStart", ["cwd"] = cwd }))!;

	private HookEnvironment Ambiente(params string[] raízes) => new(scripts, raízes);

	private static string Contexto(string json)
		=> JsonDocument.Parse(json).RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;

	[TestMethod]
	public void Dentro_do_checkout_devolve_o_roteamento_com_os_caminhos_resolvidos()
	{
		var json = SessionStart.Run(Pedido(Path.Combine(code, "Applications")), Ambiente());

		JsonDocument.Parse(json).RootElement.GetProperty("hookSpecificOutput").GetProperty("hookEventName").GetString().Should().Be("SessionStart");
		Contexto(json).Should().Contain($"Scripts em `{scripts}`").And.Contain(Path.Combine(plugin, "skills", "codebase-analyzer", "references", "code-intelligence.md")).And.NotContain("{{");
	}

	[TestMethod]
	public void Fora_do_checkout_e_das_raízes_extras_não_devolve_nada()
	{
		var fora = Directory.CreateTempSubdirectory("sessionstart-fora-").FullName;

		try
		{
			SessionStart.Run(Pedido(fora), Ambiente()).Should().BeEmpty();
			SessionStart.Run(Pedido(fora), Ambiente(fora)).Should().NotBeEmpty("MC_HOOK_ROOTS traz a pasta para o escopo");
		}
		finally
		{
			Directory.Delete(fora, true);
		}
	}

	[TestMethod]
	public void Sem_o_executável_compila_e_a_falha_vira_nota_no_contexto()
	{
		File.Delete(exe);
		var compilado = new List<string>();

		var json = SessionStart.Run(Pedido(code), Ambiente(), solution => { compilado.Add(solution); return "erro CS0001"; });

		compilado.Should().Equal(Path.Combine(scripts, "DeclIndex", "DeclIndex.slnx"));
		Contexto(json).Should().Contain("DeclIndex não compilou").And.Contain("erro CS0001");
	}

	[TestMethod]
	public void Com_o_executável_não_compila_e_o_contexto_sai_sem_nota()
	{
		var compilado = false;

		var json = SessionStart.Run(Pedido(code), Ambiente(), _ => { compilado = true; return null; });

		compilado.Should().BeFalse();
		Contexto(json).Should().NotContain("não compilou");
	}
}
