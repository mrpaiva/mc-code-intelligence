namespace DeclIndex.Hook;

/// <summary>
/// O que o hook precisa saber do ambiente: onde os scripts moram (para as mensagens) e sob quais raízes ele
/// age além dos checkouts do Code (MC_HOOK_ROOTS). Injetado para os testes não dependerem de variável.
/// </summary>
public sealed record HookEnvironment(string ScriptsDirectory, IReadOnlyList<string> HookRoots)
{
	public static HookEnvironment FromProcess()
	{
		var roots = (Environment.GetEnvironmentVariable("MC_HOOK_ROOTS") ?? "")
			.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		return new HookEnvironment(ResolveScriptsDirectory(), roots);
	}

	/// <summary>
	/// CLAUDE_PLUGIN_ROOT vem do Claude Code quando o hook roda pelo plugin. Sem ela (execução direta), sobe a
	/// partir do executável até a pasta que tem o find_declarations.ps1: bin\Release\net10.0 → DeclIndex → DeclIndex → scripts.
	/// </summary>
	private static string ResolveScriptsDirectory()
	{
		var pluginRoot = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_ROOT");
		if (!string.IsNullOrEmpty(pluginRoot))
		{
			return Path.Combine(Path.GetFullPath(pluginRoot), "skills", "codebase-analyzer", "scripts");
		}

		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		for (var level = 0; level < 8 && directory != null; level++, directory = directory.Parent)
		{
			if (File.Exists(Path.Combine(directory.FullName, "find_declarations.ps1"))) return directory.FullName;
		}

		return Path.Combine("<plugin>", "skills", "codebase-analyzer", "scripts");
	}
}
