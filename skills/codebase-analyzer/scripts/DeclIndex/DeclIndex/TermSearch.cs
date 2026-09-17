namespace DeclIndex;

public sealed record RankedFile(string Path, double Score, IReadOnlyList<string> Terms, IReadOnlyList<int> Lines);

/// <summary>
/// Busca por relevância para "onde vive a lógica de X" com mais de uma palavra, onde o find_usages devolveria
/// centenas de arquivos sem ordem. Cada termo é varrido no corpus como palavra inteira e os arquivos da worktree
/// são ranqueados por BM25: tf = linhas do blob com o termo, df = blobs da worktree com o termo, comprimento =
/// linhas do arquivo, N e média sobre os blobs da worktree que passam no filtro. Sem índice invertido: k termos
/// custam k varreduras.
/// </summary>
public static class TermSearch
{
	private const double K1 = 1.2;
	private const double B = 0.75;

	public static List<RankedFile> Rank(string storeRoot, string root, IReadOnlyList<string> terms, int top, string? scope, IReadOnlyList<string> includes)
	{
		var corpus = new Corpus(storeRoot);
		var filter = new PathFilter(scope, includes);
		var pathsBySha = WorktreeIndex.ReadManifest(WorktreeIndex.ManifestPathFor(storeRoot, Path.GetFullPath(root)));
		var documents = new Dictionary<int, List<string>>();

		foreach (var (sha, paths) in pathsBySha)
		{
			if (!corpus.Contains(sha)) continue;

			var id = corpus.IdOf(sha);
			var accepted = paths.Where(filter.Accept).ToList();
			if (accepted.Count > 0 && corpus.LinesOf(id) > 0) documents[id] = accepted;
		}

		if (documents.Count == 0) return [];

		var averageLength = documents.Keys.Average(corpus.LinesOf);
		var scores = new Dictionary<int, double>();
		var matched = new Dictionary<int, List<string>>();
		var lines = new Dictionary<int, SortedSet<int>>();

		foreach (var term in terms.Distinct(StringComparer.Ordinal))
		{
			var hitsByBlob = corpus.Search(term).GroupBy(hit => hit.Id).Where(group => documents.ContainsKey(group.Key)).ToList();
			if (hitsByBlob.Count == 0) continue;

			var idf = Math.Log((documents.Count - hitsByBlob.Count + 0.5) / (hitsByBlob.Count + 0.5) + 1);

			foreach (var group in hitsByBlob)
			{
				var frequency = group.Count();
				var length = corpus.LinesOf(group.Key);
				scores[group.Key] = scores.GetValueOrDefault(group.Key) + idf * frequency * (K1 + 1) / (frequency + K1 * (1 - B + B * length / averageLength));

				if (!matched.TryGetValue(group.Key, out var termsOfBlob)) matched[group.Key] = termsOfBlob = [];
				termsOfBlob.Add(term);

				if (!lines.TryGetValue(group.Key, out var linesOfBlob)) lines[group.Key] = linesOfBlob = [];
				linesOfBlob.UnionWith(group.Select(hit => hit.Line));
			}
		}

		return scores
			.SelectMany(pair => documents[pair.Key].Select(path => new RankedFile(path, pair.Value, matched[pair.Key], lines[pair.Key].ToList())))
			.OrderByDescending(file => file.Score)
			.ThenBy(file => file.Path, StringComparer.Ordinal)
			.Take(top)
			.ToList();
	}
}
