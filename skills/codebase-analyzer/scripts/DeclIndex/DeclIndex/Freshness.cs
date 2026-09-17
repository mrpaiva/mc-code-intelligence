namespace DeclIndex;

/// <summary>
/// Caminho rápido do refresh: reusa o TSV quando nada pode ter mudado o que ele depende, sem spawnar git.
/// O TSV é função dos SHAs do index e dos arquivos sujos da worktree; commit não muda nenhum dos dois, mas
/// checkout, pull, stash e reset reescrevem o index — por isso o carimbo guarda mtime e tamanho dele. Edição
/// pelo agente chega pelo marcador <c>.dirty</c> (hook PostToolUse de Edit/Write); edição por fora (IDE, sed)
/// só é vista quando a janela vence. Qualquer dúvida cai no refresh completo.
/// </summary>
public static class Freshness
{
	public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

	public static string StampPathFor(string store, string root) => Path.Combine(store, "worktrees", WorktreeIndex.KeyFor(root) + ".stamp");

	/// <summary>True quando o TSV pode ser reusado: carimbo dentro da janela, index do git igual e nenhum marcador de edição.</summary>
	public static bool IsFresh(string store, string root, DateTime nowUtc, out int files)
	{
		files = 0;
		var stampPath = StampPathFor(store, root);
		if (!File.Exists(stampPath) || !File.Exists(WorktreeIndex.TsvPathFor(store, root))) return false;
		if (DirtyMarkers(store, root).Any()) return false;

		var lines = File.ReadAllLines(stampPath);
		if (lines.Length < 3 || !long.TryParse(lines[0], out var stampedTicks) || !int.TryParse(lines[2], out files)) return false;

		var age = nowUtc - new DateTime(stampedTicks, DateTimeKind.Utc);
		if (age < TimeSpan.Zero || age > Window) return false;

		return lines[1] == GitIndexSignature(root);
	}

	/// <summary>Grava o carimbo de um refresh completo e consome os marcadores anteriores a ele (um criado durante o refresh fica para o próximo).</summary>
	public static void Stamp(string store, string root, DateTime startedUtc, int files)
	{
		AtomicFile.WriteLines(StampPathFor(store, root), [startedUtc.Ticks.ToString(), GitIndexSignature(root), files.ToString()]);

		foreach (var marker in DirtyMarkers(store, root).Where(marker => File.GetLastWriteTimeUtc(marker) <= startedUtc))
		{
			try { File.Delete(marker); }
			catch (IOException) { }
		}
	}

	/// <summary>O hook grava a chave a partir do caminho que o Claude Code lhe deu; a caixa pode diferir da nossa.</summary>
	private static IEnumerable<string> DirtyMarkers(string store, string root)
	{
		var directory = Path.Combine(store, "worktrees");
		if (!Directory.Exists(directory)) return [];

		var expected = WorktreeIndex.KeyFor(root) + ".dirty";
		return Directory.EnumerateFiles(directory, "*.dirty").Where(path => string.Equals(Path.GetFileName(path), expected, StringComparison.OrdinalIgnoreCase)).ToList();
	}

	private static string GitIndexSignature(string root)
	{
		var indexPath = GitIndexPath(root);
		if (indexPath == null) return "";

		var info = new FileInfo(indexPath);
		return info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "";
	}

	/// <summary>Numa worktree secundária, <c>.git</c> é um arquivo apontando o gitdir onde o index mora.</summary>
	private static string? GitIndexPath(string root)
	{
		var dotGit = Path.Combine(root, ".git");
		if (Directory.Exists(dotGit)) return Path.Combine(dotGit, "index");
		if (!File.Exists(dotGit)) return null;

		var pointer = File.ReadLines(dotGit).FirstOrDefault(line => line.StartsWith("gitdir: ", StringComparison.Ordinal));
		if (pointer == null) return null;

		var gitDir = pointer["gitdir: ".Length..].Trim();
		if (!Path.IsPathRooted(gitDir)) gitDir = Path.GetFullPath(Path.Combine(root, gitDir));

		return Path.Combine(gitDir, "index");
	}
}
