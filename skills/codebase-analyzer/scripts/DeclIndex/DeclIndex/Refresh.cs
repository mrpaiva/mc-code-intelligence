using System.Collections.Concurrent;
using System.Diagnostics;

namespace DeclIndex;

public sealed record RefreshPhases(long GitMs, long ReadMs, long ParseMs, long MaterializeMs, long CorpusMs);

public sealed record RefreshReport(int Files, int Texts, int Parsed, int Invalidated, int BlobsRead, int CorpusAppended, IReadOnlyList<string> ParseErrors, string TsvPath, TimeSpan Elapsed, RefreshPhases Phases, bool Reused = false);

/// <summary>
/// Atualiza o índice de uma worktree: lista pelo git, parseia os .cs que faltam no armazém, materializa o TSV se
/// os .cs do manifesto mudaram, regrava o manifesto se qualquer arquivo mudou e acrescenta ao corpus os blobs
/// que ele ainda não tem. Reusa tudo sem git quando <see cref="Freshness"/> garante que nada mudou.
/// </summary>
public static class Refresh
{
	private const int CorpusBatch = 2048;

	public static RefreshReport Run(string root, string storeRoot)
	{
		var stopwatch = Stopwatch.StartNew();
		var startedUtc = DateTime.UtcNow;
		root = Path.GetFullPath(root);

		if (Freshness.IsFresh(storeRoot, root, startedUtc, out var stampedFiles))
		{
			return new RefreshReport(stampedFiles, 0, 0, 0, 0, 0, [], WorktreeIndex.TsvPathFor(storeRoot, root), stopwatch.Elapsed, new RefreshPhases(0, 0, 0, 0, 0), Reused: true);
		}

		var store = new BlobStore(storeRoot);
		var entries = GitWorktree.ListSources(root);
		var csharp = entries.Where(entry => entry.IsCSharp).ToList();
		var gitMs = stopwatch.ElapsedMilliseconds;

		var missing = DistinctBySha(csharp.Where(entry => !store.Contains(entry.Sha)));
		var sources = new ConcurrentDictionary<string, string>();
		Parallel.ForEach(missing, entry => sources[entry.Sha] = SourceReader.Read(Path.Combine(root, entry.RelativePath)));

		var symbols = sources.Values.SelectMany(PreprocessorSymbols.Scan).Distinct().ToList();
		var invalidated = store.EnsureSymbols(symbols);

		if (invalidated)
		{
			missing = DistinctBySha(csharp);
			Parallel.ForEach(missing.Where(entry => !sources.ContainsKey(entry.Sha)), entry => sources[entry.Sha] = SourceReader.Read(Path.Combine(root, entry.RelativePath)));
		}

		var readMs = stopwatch.ElapsedMilliseconds - gitMs;
		var errors = new ConcurrentBag<string>();

		Parallel.ForEach(missing, entry =>
		{
			var result = DeclarationParser.Parse(sources[entry.Sha], store.Symbols);
			if (result.FirstError != null) errors.Add($"{entry.RelativePath}:{result.FirstError}");

			store.Write(entry.Sha, result.Declarations);
		});

		var parseMs = stopwatch.ElapsedMilliseconds - gitMs - readMs;
		var tsvPath = WorktreeIndex.TsvPathFor(storeRoot, root);
		var manifestPath = WorktreeIndex.ManifestPathFor(storeRoot, root);
		var blobsRead = 0;

		if (!File.Exists(tsvPath) || !WorktreeIndex.ManifestMatchesCSharp(manifestPath, csharp))
		{
			var seed = invalidated ? null : WorktreeIndex.SeedFor(storeRoot, tsvPath);
			blobsRead = WorktreeIndex.Materialize(tsvPath, csharp, store, new ProjectResolver(root), seed);
		}

		if (!WorktreeIndex.ManifestMatches(manifestPath, entries)) WorktreeIndex.WriteManifest(manifestPath, entries);

		var materializeMs = stopwatch.ElapsedMilliseconds - gitMs - readMs - parseMs;
		var corpusAppended = FeedCorpus(root, storeRoot, entries, sources);
		var corpusMs = stopwatch.ElapsedMilliseconds - gitMs - readMs - parseMs - materializeMs;
		var phases = new RefreshPhases(gitMs, readMs, parseMs, materializeMs, corpusMs);
		Freshness.Stamp(storeRoot, root, startedUtc, csharp.Count);

		return new RefreshReport(csharp.Count, entries.Count, missing.Count, invalidated ? 1 : 0, blobsRead, corpusAppended, errors.OrderBy(error => error, StringComparer.Ordinal).ToList(), tsvPath, stopwatch.Elapsed, phases);
	}

	/// <summary>Blobs ausentes do corpus entram em lotes: o texto dos .cs recém-lidos é reaproveitado, o resto é lido do disco.</summary>
	private static int FeedCorpus(string root, string storeRoot, IReadOnlyList<SourceEntry> entries, ConcurrentDictionary<string, string> sources)
	{
		var corpus = new Corpus(storeRoot);
		var missing = DistinctBySha(entries.Where(entry => !corpus.Contains(entry.Sha)));
		var appended = 0;

		foreach (var batch in missing.Chunk(CorpusBatch))
		{
			var texts = new ConcurrentDictionary<string, string?>();
			Parallel.ForEach(batch, entry => texts[entry.Sha] = sources.TryGetValue(entry.Sha, out var text) ? text : SourceReader.ReadText(Path.Combine(root, entry.RelativePath)));
			appended += corpus.Append(batch.Select(entry => (entry.Sha, texts[entry.Sha])));
		}

		return appended;
	}

	private static List<SourceEntry> DistinctBySha(IEnumerable<SourceEntry> entries)
		=> entries.GroupBy(entry => entry.Sha).Select(group => group.First()).ToList();
}
