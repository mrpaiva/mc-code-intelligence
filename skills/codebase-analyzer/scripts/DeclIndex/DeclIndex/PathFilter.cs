using System.Text.RegularExpressions;

namespace DeclIndex;

/// <summary>
/// Os filtros de caminho que o find_usages.ps1 sempre teve: pasta de escopo, globs de inclusão e as pastas que o
/// rg da árvore pulava (node_modules, bin, obj...). Aplicado aos caminhos do manifesto, nunca ao corpus — e o
/// search aplica aos 46 mil caminhos da worktree, então sem alocação por caminho e com "*.cs" virando EndsWith.
/// </summary>
public sealed class PathFilter
{
	private static readonly HashSet<string> ExcludedDirectories = new(["node_modules", "bin", "obj", "dist", ".git", "packages", ".vs", ".vscode", "publish"], StringComparer.OrdinalIgnoreCase);
	private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> ExcludedLookup = ExcludedDirectories.GetAlternateLookup<ReadOnlySpan<char>>();

	private readonly string prefix;
	private readonly List<Func<string, bool>> matchers;

	public PathFilter(string? scope, IEnumerable<string> includes)
	{
		prefix = NormalizeScope(scope);
		matchers = includes.Where(include => include != "*").Select(Matcher).ToList();
	}

	public bool Accept(string path)
	{
		if (prefix.Length > 0 && !path.Equals(prefix, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) return false;
		if (HasExcludedDirectory(path)) return false;

		return matchers.Count == 0 || matchers.Any(matcher => matcher(path));
	}

	private static bool HasExcludedDirectory(string path)
	{
		var rest = path.AsSpan();
		int slash;

		while ((slash = rest.IndexOf('/')) >= 0)
		{
			if (ExcludedLookup.Contains(rest[..slash])) return true;

			rest = rest[(slash + 1)..];
		}

		return false;
	}

	private static string NormalizeScope(string? scope)
	{
		var normalized = (scope ?? "").Replace('\\', '/').Trim('/');
		while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];

		return normalized == "." ? "" : normalized;
	}

	/// <summary>Como o --glob do rg: sem barra casa o nome do arquivo; com barra casa o caminho relativo inteiro. "*" não atravessa "/".</summary>
	private static Func<string, bool> Matcher(string glob)
	{
		var pattern = glob.Replace('\\', '/');

		if (pattern.StartsWith('*') && pattern.AsSpan(1).IndexOfAny('*', '?', '/') < 0)
		{
			var suffix = pattern[1..];
			return path => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
		}

		var whole = pattern.Contains('/');
		var regex = new Regex((whole ? "^" : "(^|/)") + Regex.Escape(pattern).Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

		return path => regex.IsMatch(path);
	}
}
