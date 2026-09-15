using System.Collections.Concurrent;

namespace DeclIndex;

/// <summary>TSV materializado de uma worktree e o manifesto (sha, caminho) que o gerou.</summary>
public static class WorktreeIndex
{
	public const string Header = Declaration.Header + "\tfile\tproject";

	private const int FileColumn = 8;

	public static string KeyFor(string root)
		=> Path.GetFullPath(root).TrimEnd('\\', '/').Replace(':', '_').Replace('\\', '_').Replace('/', '_');

	public static string TsvPathFor(string store, string root) => Path.Combine(store, "worktrees", KeyFor(root) + ".tsv");

	public static string ManifestPathFor(string store, string root) => Path.Combine(store, "worktrees", KeyFor(root) + ".manifest");

	public static bool ManifestMatches(string manifestPath, IReadOnlyList<SourceEntry> entries)
		=> File.Exists(manifestPath) && File.ReadAllLines(manifestPath).SequenceEqual(entries.Select(ManifestLine));

	/// <summary>Escolhe a semente: o próprio TSV da worktree ou, se não houver, o TSV mais recente de outra worktree do armazém.</summary>
	public static string? SeedFor(string store, string tsvPath)
	{
		if (File.Exists(tsvPath)) return tsvPath;

		var worktrees = Path.Combine(store, "worktrees");
		if (!Directory.Exists(worktrees)) return null;

		return Directory.EnumerateFiles(worktrees, "*.tsv").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
	}

	/// <summary>
	/// Materializa reaproveitando a semente: linhas de arquivos cujo (caminho, sha) não mudou são copiadas;
	/// só os demais leem blob. Devolve quantos blobs foram lidos.
	/// </summary>
	public static int Materialize(string tsvPath, string manifestPath, IReadOnlyList<SourceEntry> entries, BlobStore store, ProjectResolver projects, string? seedTsvPath)
	{
		var reusable = LoadReusable(seedTsvPath, entries);
		var changed = entries.Where(entry => !reusable.ContainsKey(entry.RelativePath)).ToList();
		var fresh = new ConcurrentDictionary<string, List<string>>();

		Parallel.ForEach(changed, entry =>
		{
			var project = projects.ProjectOf(entry.RelativePath);
			fresh[entry.RelativePath] = store.Read(entry.Sha).Select(declaration => $"{declaration.ToTsv()}\t{entry.RelativePath}\t{project}").ToList();
		});

		var lines = new List<string>(capacity: entries.Count * 16) { Header };

		foreach (var entry in entries)
		{
			lines.AddRange(reusable.TryGetValue(entry.RelativePath, out var reused) ? reused : fresh[entry.RelativePath]);
		}

		AtomicFile.WriteLines(tsvPath, lines);
		AtomicFile.WriteLines(manifestPath, entries.Select(ManifestLine));

		return changed.Count;
	}

	/// <summary>Linhas da semente agrupadas por arquivo, só dos arquivos cujo sha na semente é o mesmo de agora.</summary>
	private static Dictionary<string, List<string>> LoadReusable(string? seedTsvPath, IReadOnlyList<SourceEntry> entries)
	{
		var reusable = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		if (seedTsvPath == null) return reusable;

		var seedManifestPath = Path.ChangeExtension(seedTsvPath, ".manifest");
		if (!File.Exists(seedManifestPath)) return reusable;

		var seedShas = File.ReadLines(seedManifestPath).Select(line => line.Split('\t')).ToDictionary(columns => columns[1], columns => columns[0], StringComparer.Ordinal);
		var wanted = entries.Where(entry => seedShas.TryGetValue(entry.RelativePath, out var sha) && sha == entry.Sha).Select(entry => entry.RelativePath).ToHashSet(StringComparer.Ordinal);

		foreach (var path in wanted) reusable[path] = [];

		foreach (var line in File.ReadLines(seedTsvPath).Skip(1))
		{
			var file = ColumnAt(line, FileColumn);
			if (reusable.TryGetValue(file, out var group)) group.Add(line);
		}

		return reusable;
	}

	private static string ColumnAt(string line, int index)
	{
		var start = 0;

		for (var column = 0; column < index; column++)
		{
			start = line.IndexOf('\t', start) + 1;
			if (start == 0) return "";
		}

		var end = line.IndexOf('\t', start);

		return end < 0 ? line[start..] : line[start..end];
	}

	private static string ManifestLine(SourceEntry entry) => $"{entry.Sha}\t{entry.RelativePath}";
}
