using System.Text.RegularExpressions;

namespace DeclIndex;

public sealed record Usage(string Path, int Line);

/// <summary>
/// Consulta de uso: acertos do corpus traduzidos para os caminhos da worktree pelo manifesto, com os filtros que
/// o find_usages.ps1 sempre teve — pasta de escopo, globs de inclusão e as pastas que o rg da árvore pulava.
/// O mesmo blob em dois caminhos sai nos dois.
/// </summary>
public static class Usages
{
	private static readonly HashSet<string> ExcludedDirectories = new(["node_modules", "bin", "obj", "dist", ".git", "packages", ".vs", ".vscode", "publish"], StringComparer.OrdinalIgnoreCase);

	public static List<Usage> Find(string storeRoot, string root, string symbol, string? scope, IReadOnlyList<string> includes)
	{
		var corpus = new Corpus(storeRoot);
		var hits = corpus.Search(symbol);
		var paths = WorktreeIndex.ReadManifest(WorktreeIndex.ManifestPathFor(storeRoot, Path.GetFullPath(root)), hits.Select(hit => corpus.ShaOf(hit.Id)).ToHashSet(StringComparer.Ordinal));
		var prefix = NormalizeScope(scope);
		var patterns = includes.Where(include => include != "*").Select(GlobToRegex).ToList();
		var usages = new HashSet<Usage>();

		foreach (var hit in hits)
		{
			if (!paths.TryGetValue(corpus.ShaOf(hit.Id), out var candidates)) continue;

			foreach (var path in candidates)
			{
				if (Accept(path, prefix, patterns)) usages.Add(new Usage(path, hit.Line));
			}
		}

		return usages.OrderBy(usage => usage.Path, StringComparer.Ordinal).ThenBy(usage => usage.Line).ToList();
	}

	private static bool Accept(string path, string prefix, List<Regex> patterns)
	{
		if (prefix.Length > 0 && !path.Equals(prefix, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) return false;

		var slash = path.LastIndexOf('/');
		if (slash > 0 && path[..slash].Split('/').Any(ExcludedDirectories.Contains)) return false;

		return patterns.Count == 0 || patterns.Any(pattern => pattern.IsMatch(path));
	}

	private static string NormalizeScope(string? scope)
	{
		var normalized = (scope ?? "").Replace('\\', '/').Trim('/');
		while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];

		return normalized == "." ? "" : normalized;
	}

	/// <summary>Como o --glob do rg: sem barra casa o nome do arquivo; com barra casa o caminho relativo inteiro.</summary>
	private static Regex GlobToRegex(string glob)
	{
		var pattern = glob.Replace('\\', '/');
		var whole = pattern.Contains('/');
		var regex = Regex.Escape(pattern).Replace(@"\*\*", ".*").Replace(@"\*", whole ? "[^/]*" : ".*").Replace(@"\?", whole ? "[^/]" : ".");

		return new Regex(whole ? $"^{regex}$" : $"(^|/){regex}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	}
}
