namespace DeclIndex;

public sealed record Usage(string Path, int Line, string? Text = null);

/// <summary>
/// Consulta de uso: acertos do corpus traduzidos para os caminhos da worktree pelo manifesto, passando pelo
/// <see cref="PathFilter"/>. O mesmo blob em dois caminhos sai nos dois.
/// </summary>
public static class Usages
{
	/// <summary>Limite do texto da linha em <see cref="Usage.Text"/>; o que passa disso vira "…".</summary>
	public const int MaxTextLength = 200;

	public static List<Usage> Find(string storeRoot, string root, string symbol, string? scope, IReadOnlyList<string> includes, bool withText = false)
	{
		var corpus = new Corpus(storeRoot);
		var hits = corpus.Search(symbol, withText);
		var paths = WorktreeIndex.ReadManifest(WorktreeIndex.ManifestPathFor(storeRoot, Path.GetFullPath(root)), hits.Select(hit => corpus.ShaOf(hit.Id)).ToHashSet(StringComparer.Ordinal));
		var filter = new PathFilter(scope, includes);
		var usages = new HashSet<Usage>();

		foreach (var hit in hits)
		{
			if (!paths.TryGetValue(corpus.ShaOf(hit.Id), out var candidates)) continue;

			foreach (var path in candidates)
			{
				if (filter.Accept(path)) usages.Add(new Usage(path, hit.Line, hit.Text == null ? null : Excerpt(hit.Text)));
			}
		}

		return usages.OrderBy(usage => usage.Path, StringComparer.Ordinal).ThenBy(usage => usage.Line).ToList();
	}

	/// <summary>A linha sem espaços nas pontas e cortada em <see cref="MaxTextLength"/> caracteres, sem partir um par substituto.</summary>
	private static string Excerpt(string line)
	{
		var text = line.Trim();
		if (text.Length <= MaxTextLength) return text;

		var cut = char.IsHighSurrogate(text[MaxTextLength - 1]) ? MaxTextLength - 1 : MaxTextLength;
		return string.Concat(text.AsSpan(0, cut), "…");
	}
}
