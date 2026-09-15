using System.Text.RegularExpressions;

namespace DeclIndex;

/// <summary>Descobre os símbolos usados em #if/#elif para parsear com a união deles: nada fica escondido além de ramos #else.</summary>
public static partial class PreprocessorSymbols
{
	private static readonly HashSet<string> Literals = ["true", "false"];

	public static IEnumerable<string> Scan(string source)
		=> DirectiveRegex().Matches(source)
			.SelectMany(directive => IdentifierRegex().Matches(directive.Groups[1].Value).Select(identifier => identifier.Value))
			.Where(symbol => !Literals.Contains(symbol))
			.Distinct();

	[GeneratedRegex(@"^[ \t]*#[ \t]*(?:if|elif)\b(.*)$", RegexOptions.Multiline)]
	private static partial Regex DirectiveRegex();

	[GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
	private static partial Regex IdentifierRegex();
}
