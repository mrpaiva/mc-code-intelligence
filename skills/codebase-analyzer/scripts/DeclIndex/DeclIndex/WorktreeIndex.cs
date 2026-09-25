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

	/// <summary>Só os .cs de cada lado: mudança em .resx ou .config troca o manifesto, mas não o que o TSV contém.</summary>
	public static bool ManifestMatchesCSharp(string manifestPath, IReadOnlyList<SourceEntry> entries)
		=> File.Exists(manifestPath) && File.ReadAllLines(manifestPath).Where(line => line.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).SequenceEqual(entries.Where(entry => entry.IsCSharp).Select(ManifestLine));

	public static void WriteManifest(string manifestPath, IReadOnlyList<SourceEntry> entries)
		=> AtomicFile.WriteLines(manifestPath, entries.Select(ManifestLine));

	/// <summary>Caminhos de cada sha do manifesto da worktree (só os pedidos, se houver lista) — o mesmo conteúdo pode estar em mais de um arquivo.</summary>
	public static Dictionary<string, List<string>> ReadManifest(string manifestPath, IReadOnlySet<string>? shas = null)
	{
		var paths = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		if (shas is { Count: 0 }) return paths;

		foreach (var line in File.ReadLines(manifestPath))
		{
			var tab = line.IndexOf('\t');
			if (tab < 0) continue;

			var sha = line[..tab];
			if (shas != null && !shas.Contains(sha)) continue;

			if (!paths.TryGetValue(sha, out var list)) paths[sha] = list = [];

			list.Add(line[(tab + 1)..]);
		}

		return paths;
	}

	/// <summary>Escolhe a semente: o próprio TSV da worktree ou, se não houver, o TSV mais recente de outra worktree do armazém.</summary>
	public static string? SeedFor(string store, string tsvPath)
	{
		if (File.Exists(tsvPath)) return tsvPath;

		var worktrees = Path.Combine(store, "worktrees");
		if (!Directory.Exists(worktrees)) return null;

		return Directory.EnumerateFiles(worktrees, "*.tsv").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
	}

	/// <summary>
	/// Materializa os .cs reaproveitando a semente: linhas de arquivos cujo (caminho, sha) não mudou são copiadas;
	/// só os demais leem blob. O manifesto é do chamador, gravado depois — a semente é lida com o manifesto anterior.
	/// Devolve quantos blobs foram lidos.
	/// </summary>
	public static int Materialize(string tsvPath, IReadOnlyList<SourceEntry> entries, BlobStore store, ProjectResolver projects, string? seedTsvPath)
	{
		entries = entries.Where(entry => entry.IsCSharp).ToList();
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
			lines.AddRange(reusable.TryGetValue(entry.RelativePath, out var reused) ? WithProject(reused, projects.ProjectOf(entry.RelativePath)) : fresh[entry.RelativePath]);
		}

		AtomicFile.WriteLines(tsvPath, lines);

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

	/// <summary>
	/// A semente pode ser outra worktree, em outro branch, ou a própria antes de um csproj mudar: o sha igual garante as
	/// declarações, não o projeto. O projeto (última coluna) é sempre o desta árvore.
	/// </summary>
	private static IEnumerable<string> WithProject(List<string> lines, string project)
	{
		foreach (var line in lines)
		{
			var start = line.LastIndexOf('\t') + 1;

			yield return line.AsSpan(start).SequenceEqual(project) ? line : string.Concat(line.AsSpan(0, start), project);
		}
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
