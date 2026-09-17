namespace DeclIndex;

public sealed record Usage(string Path, int Line);

/// <summary>
/// Consulta de uso: acertos do corpus traduzidos para os caminhos da worktree pelo manifesto, passando pelo
/// <see cref="PathFilter"/>. O mesmo blob em dois caminhos sai nos dois.
/// </summary>
public static class Usages
{
	public static List<Usage> Find(string storeRoot, string root, string symbol, string? scope, IReadOnlyList<string> includes)
	{
		var corpus = new Corpus(storeRoot);
		var hits = corpus.Search(symbol);
		var paths = WorktreeIndex.ReadManifest(WorktreeIndex.ManifestPathFor(storeRoot, Path.GetFullPath(root)), hits.Select(hit => corpus.ShaOf(hit.Id)).ToHashSet(StringComparer.Ordinal));
		var filter = new PathFilter(scope, includes);
		var usages = new HashSet<Usage>();

		foreach (var hit in hits)
		{
			if (!paths.TryGetValue(corpus.ShaOf(hit.Id), out var candidates)) continue;

			foreach (var path in candidates)
			{
				if (filter.Accept(path)) usages.Add(new Usage(path, hit.Line));
			}
		}

		return usages.OrderBy(usage => usage.Path, StringComparer.Ordinal).ThenBy(usage => usage.Line).ToList();
	}
}
