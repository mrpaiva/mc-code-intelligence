using System.Text.Json;
using DeclIndex.Hook;

namespace DeclIndex.UnitTests;

[TestClass]
public class EditMarkerTests
{
	private string raiz = "";
	private string store = "";

	[TestInitialize]
	public void Preparar()
	{
		raiz = Directory.CreateTempSubdirectory("editmarker-code-").FullName;
		store = Directory.CreateTempSubdirectory("editmarker-store-").FullName;
		Directory.CreateDirectory(Path.Combine(raiz, "Applications", "X", "Sources"));
		Directory.CreateDirectory(Path.Combine(raiz, "Components"));
	}

	[TestCleanup]
	public void Limpar()
	{
		Directory.Delete(raiz, true);
		Directory.Delete(store, true);
	}

	private static HookRequest Pedido(string ferramenta, Dictionary<string, object?> entrada)
		=> HookRequest.Parse(JsonSerializer.Serialize(new Dictionary<string, object?> { ["hook_event_name"] = "PostToolUse", ["tool_name"] = ferramenta, ["tool_input"] = entrada }))!;

	[TestMethod]
	public void Edição_sob_o_checkout_grava_o_marcador_com_a_chave_da_worktree()
	{
		var arquivo = Path.Combine(raiz, "Applications", "X", "Sources", "Foo.cs");

		var marcador = EditMarker.Mark(Pedido("Edit", new() { ["file_path"] = arquivo, ["old_string"] = "a", ["new_string"] = "b" }), store);

		marcador.Should().Be(Path.Combine(store, "worktrees", WorktreeIndex.KeyFor(raiz) + ".dirty"), "o DeclIndex procura o marcador pela mesma chave do TSV");
		File.Exists(marcador).Should().BeTrue();
	}

	[TestMethod]
	public void Notebook_usa_notebook_path()
	{
		var caderno = Path.Combine(raiz, "Components", "n.ipynb");

		EditMarker.Mark(Pedido("NotebookEdit", new() { ["notebook_path"] = caderno }), store).Should().NotBeNull();
	}

	[TestMethod]
	public void Arquivo_fora_de_um_checkout_não_marca_nada()
	{
		var fora = Path.Combine(Path.GetTempPath(), "editmarker-fora-" + Guid.NewGuid().ToString("N") + ".md");

		EditMarker.Mark(Pedido("Write", new() { ["file_path"] = fora, ["content"] = "x" }), store).Should().BeNull();
		Directory.Exists(Path.Combine(store, "worktrees")).Should().BeFalse();
	}

	[TestMethod]
	public void Pedido_sem_caminho_não_marca_nada()
	{
		EditMarker.Mark(Pedido("Edit", new()), store).Should().BeNull();
	}

	[TestMethod]
	public void Evento_chega_no_pedido()
	{
		Pedido("Edit", new()).HookEventName.Should().Be("PostToolUse");
		HookRequest.Parse("{\"tool_name\":\"Grep\"}")!.HookEventName.Should().Be("");
	}
}
