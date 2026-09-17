using System.Collections.Concurrent;
using System.Diagnostics;

namespace DeclIndex;

public sealed record RefreshPhases(long GitMs, long ReadMs, long ParseMs, long MaterializeMs);

public sealed record RefreshReport(int Files, int Parsed, int Invalidated, int BlobsRead, IReadOnlyList<string> ParseErrors, string TsvPath, TimeSpan Elapsed, RefreshPhases Phases, bool Reused = false);

/// <summary>Atualiza o índice de uma worktree: lista pelo git, parseia só o que falta no armazém e materializa se o manifesto mudou. Reusa o TSV sem git quando <see cref="Freshness"/> garante que nada mudou.</summary>
public static class Refresh
{
	public static RefreshReport Run(string root, string storeRoot)
	{
		var stopwatch = Stopwatch.StartNew();
		var startedUtc = DateTime.UtcNow;
		root = Path.GetFullPath(root);

		if (Freshness.IsFresh(storeRoot, root, startedUtc, out var stampedFiles))
		{
			return new RefreshReport(stampedFiles, 0, 0, 0, [], WorktreeIndex.TsvPathFor(storeRoot, root), stopwatch.Elapsed, new RefreshPhases(0, 0, 0, 0), Reused: true);
		}

		var store = new BlobStore(storeRoot);
		var entries = GitWorktree.ListSources(root);
		var gitMs = stopwatch.ElapsedMilliseconds;

		var missing = DistinctBySha(entries.Where(entry => !store.Contains(entry.Sha)));
		var sources = new ConcurrentDictionary<string, string>();
		Parallel.ForEach(missing, entry => sources[entry.Sha] = SourceReader.Read(Path.Combine(root, entry.RelativePath)));

		var symbols = sources.Values.SelectMany(PreprocessorSymbols.Scan).Distinct().ToList();
		var invalidated = store.EnsureSymbols(symbols);

		if (invalidated)
		{
			missing = DistinctBySha(entries);
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

		if (!File.Exists(tsvPath) || !WorktreeIndex.ManifestMatches(manifestPath, entries))
		{
			var seed = invalidated ? null : WorktreeIndex.SeedFor(storeRoot, tsvPath);
			blobsRead = WorktreeIndex.Materialize(tsvPath, manifestPath, entries, store, new ProjectResolver(root), seed);
		}

		var materializeMs = stopwatch.ElapsedMilliseconds - gitMs - readMs - parseMs;
		var phases = new RefreshPhases(gitMs, readMs, parseMs, materializeMs);
		Freshness.Stamp(storeRoot, root, startedUtc, entries.Count);

		return new RefreshReport(entries.Count, missing.Count, invalidated ? 1 : 0, blobsRead, errors.OrderBy(error => error, StringComparer.Ordinal).ToList(), tsvPath, stopwatch.Elapsed, phases);
	}

	private static List<SourceEntry> DistinctBySha(IEnumerable<SourceEntry> entries)
		=> entries.GroupBy(entry => entry.Sha).Select(group => group.First()).ToList();
}
