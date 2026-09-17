namespace DeclIndex.Hook;

/// <summary>
/// PostToolUse de Edit/Write/MultiEdit/NotebookEdit: marca a worktree do arquivo editado como suja, para o
/// refresh do DeclIndex não reusar o TSV na próxima consulta (ver Freshness no DeclIndex). Fora de um checkout
/// do Code, nada acontece.
/// </summary>
public static class EditMarker
{
	private static readonly string[] PathProperties = ["file_path", "notebook_path"];

	/// <summary>Armazém padrão do índice — a mesma resolução de Program.cs do DeclIndex.</summary>
	public static string DefaultStore()
		=> Environment.GetEnvironmentVariable("MC_CODEINDEX")
			?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mc-code-intelligence", "index");

	/// <summary>Caminho do marcador gravado, ou null quando o arquivo não está sob um checkout do Code.</summary>
	public static string? Mark(HookRequest request, string store)
	{
		var path = PathProperties.Select(request.GetString).FirstOrDefault(value => !string.IsNullOrEmpty(value));
		if (path == null) return null;

		var directory = Path.GetDirectoryName(Path.GetFullPath(path));
		var root = directory == null ? null : CodeIntelligenceHook.FindCodeAncestor(directory);
		if (root == null) return null;

		var marker = Path.Combine(store, "worktrees", KeyFor(root) + ".dirty");
		Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
		File.WriteAllText(marker, "");
		return marker;
	}

	// Mesma chave que WorktreeIndex.KeyFor e find_declarations.ps1: caminho completo com ':', '\' e '/' trocados por '_'.
	private static string KeyFor(string root)
		=> Path.GetFullPath(root).TrimEnd('\\', '/').Replace(':', '_').Replace('\\', '_').Replace('/', '_');
}
