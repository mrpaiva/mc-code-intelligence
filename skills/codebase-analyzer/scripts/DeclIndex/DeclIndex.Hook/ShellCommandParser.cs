using System.Text;
using System.Text.RegularExpressions;

namespace DeclIndex.Hook;

public sealed record ShellSegment(IReadOnlyList<string> Tokens, bool Piped);

public sealed record ShellCommand(string? Name, IReadOnlyList<string> Arguments, bool FedByXargs);

/// <summary><paramref name="SearchesRevision"/>: <c>git grep padrão &lt;rev&gt; -- caminhos</c> lê a árvore de outro commit, não a worktree.</summary>
public sealed record SearchArguments(string? Pattern, IReadOnlyList<string> Paths, bool Recursive, bool SearchesRevision = false);

/// <summary>
/// Lê uma linha de Bash/PowerShell como o shlex posix do hook Python: aspas, escapes, pontuação (| || &amp;&amp; ; &amp;)
/// como token próprio, redirecionamentos removidos antes. Reconhece grep/rg/git grep/findstr/Select-String e as
/// listagens (find, Get-ChildItem) que a regra do Glob cobre.
/// </summary>
public static class ShellCommandParser
{
	public static readonly HashSet<string> SearchCommands = new(StringComparer.Ordinal) { "grep", "egrep", "fgrep", "rg", "findstr", "select-string", "sls" };
	public static readonly HashSet<string> ListingCommands = new(StringComparer.Ordinal) { "find", "get-childitem", "gci", "ls", "dir" };
	public static readonly HashSet<string> ChangeDirectoryCommands = new(StringComparer.Ordinal) { "cd", "pushd", "set-location", "push-location", "sl" };

	private static readonly HashSet<string> SegmentBreaks = new(StringComparer.Ordinal) { "|", "||", "&&", ";", "&" };

	/// <summary>Token da quebra de linha: no Bash e no PowerShell cada linha é um comando.</summary>
	public const string NewLine = "\n";

	/// <summary>Opções de grep/rg que consomem o token seguinte; o resto é switch (-r fica switch, como no grep).</summary>
	private static readonly HashSet<string> SearchOptionsWithValue = new(StringComparer.Ordinal)
	{
		"-e", "--regexp", "-f", "--file", "-g", "--glob", "--iglob", "-t", "--type", "-T", "--type-not",
		"--include", "--exclude", "--exclude-dir", "-A", "-B", "-C", "-m", "--max-count", "-d", "--directories",
		"--color", "--colour", "-j", "--threads", "--max-depth",
	};

	// O alvo para na pontuação do shell, não só no espaço: "2>/dev/null;" não pode levar o ";" junto.
	private static readonly Regex Redirection = new(@"(?<=\s)(?:\d?>{1,2}|&>|<)\s*(?:&\d+|[^\s;|&()<>]+)");
	private static readonly Regex RecursiveShortFlag = new(@"\A-[a-zA-Z]*[rR][a-zA-Z]*\z");
	private static readonly Regex ShortFlags = new(@"\A-[a-zA-Z]+\d*\z");
	private static readonly Regex StarExtension = new(@"\*\.([A-Za-z0-9]+)");
	private static readonly Regex TypeOption = new(@"(?:^|\s)(?:-t\s*|--type[= ]\s*)([A-Za-z0-9]+)");
	private static readonly Regex BashAssignment = new(@"\A([A-Za-z_]\w*)=(.*)\z", RegexOptions.Singleline);
	private static readonly Regex VariableReference = new(@"\$\{?([A-Za-z_]\w*)\}?");

	/// <summary>Remove <c>2>/dev/null</c>, <c>> arquivo</c>, <c>&lt; arquivo</c> para o alvo do redirect não virar caminho.</summary>
	public static string StripRedirections(string command) => Redirection.Replace(command, " ");

	/// <summary>
	/// Divide a linha em segmentos (por |, ||, &amp;&amp;, ;, &amp;, quebra de linha e ")") já tokenizados. O ")" fecha o comando, e o
	/// que vem depois (o ".Count" de <c>(... | Select-String x).Count</c>) não é argumento dele. Quebra de linha logo depois de
	/// |, || ou &amp;&amp; continua o comando na linha seguinte. Lança FormatException se as aspas não fecham.
	/// </summary>
	public static List<ShellSegment> SplitSegments(string command)
	{
		var segments = new List<ShellSegment>();
		var current = new List<string>();
		var piped = false;

		foreach (var token in Tokenize(StripRedirections(command)))
		{
			if (token == NewLine || IsRunOf(token, ')'))
			{
				if (current.Count == 0) continue;

				segments.Add(new ShellSegment(current, piped));
				current = new List<string>();
				piped = false;
			}
			else if (SegmentBreaks.Contains(token))
			{
				if (current.Count > 0) segments.Add(new ShellSegment(current, piped));
				current = new List<string>();
				piped = token == "|";
			}
			else if (!IsRunOf(token, '('))
			{
				current.Add(token);
			}
		}

		if (current.Count > 0) segments.Add(new ShellSegment(current, piped));
		return segments;
	}

	private static bool IsRunOf(string token, char character) => token.Length > 0 && token.All(item => item == character);

	/// <summary>
	/// Tokenizador no modo posix do shlex com punctuation_chars: aspas somem, \ escapa, ();&lt;&gt;|&amp; viram tokens próprios e
	/// a quebra de linha fora das aspas vira o token <see cref="NewLine"/>.
	/// </summary>
	public static List<string> Tokenize(string command)
	{
		var tokens = new List<string>();
		var word = new StringBuilder();
		var inWord = false;
		var index = 0;

		void Flush()
		{
			if (inWord) tokens.Add(word.ToString());
			word.Clear();
			inWord = false;
		}

		while (index < command.Length)
		{
			var character = command[index];

			if (character == '\'')
			{
				var close = command.IndexOf('\'', index + 1);
				if (close < 0) throw new FormatException("aspas simples sem fechar");
				word.Append(command, index + 1, close - index - 1);
				inWord = true;
				index = close + 1;
			}
			else if (character == '"')
			{
				index++;
				var closed = false;
				while (index < command.Length)
				{
					var inner = command[index];
					if (inner == '\\' && index + 1 < command.Length && (command[index + 1] == '"' || command[index + 1] == '\\'))
					{
						word.Append(command[index + 1]);
						index += 2;
					}
					else if (inner == '"')
					{
						closed = true;
						index++;
						break;
					}
					else
					{
						word.Append(inner);
						index++;
					}
				}

				if (!closed) throw new FormatException("aspas duplas sem fechar");
				inWord = true;
			}
			else if (character == '\\')
			{
				if (index + 1 >= command.Length) throw new FormatException("escape sem caractere");
				word.Append(command[index + 1]);
				inWord = true;
				index += 2;
			}
			else if ((character is '$' or '<' or '>') && index + 1 < command.Length && command[index + 1] == '(')
			{
				// Substituição de comando ($(...)) ou de processo (<(...), >(...)) é um único token opaco: o comando de
				// dentro não vira padrão nem caminho do comando de fora — "-f <(git diff ...)" consumia só o "<(" e o
				// "git" seguinte virava o padrão do grep.
				var close = FindSubstitutionEnd(command, index + 2);
				if (character != '$') Flush();
				word.Append(character).Append("(...)");
				inWord = true;
				index = close + 1;
			}
			else if (char.IsWhiteSpace(character))
			{
				Flush();
				if (character == '\n') tokens.Add(NewLine);
				index++;
			}
			else if (IsPunctuation(character))
			{
				Flush();
				var start = index;
				while (index < command.Length && IsPunctuation(command[index])) index++;
				tokens.Add(command.Substring(start, index - start));
			}
			else
			{
				word.Append(character);
				inWord = true;
				index++;
			}
		}

		Flush();
		return tokens;
	}

	private static bool IsPunctuation(char character) => character is '(' or ')' or ';' or '<' or '>' or '|' or '&';

	/// <summary>Índice do ")" que fecha a substituição aberta antes de <paramref name="start"/>, pulando aninhamentos e aspas. Lança FormatException se não fecha.</summary>
	private static int FindSubstitutionEnd(string command, int start)
	{
		var depth = 1;
		var index = start;

		while (index < command.Length)
		{
			var character = command[index];
			if (character is '\'' or '"')
			{
				var close = command.IndexOf(character, index + 1);
				if (close < 0) throw new FormatException("aspas sem fechar dentro de substituição");
				index = close + 1;
				continue;
			}

			if (character == '\\') { index += 2; continue; }
			if (character == '(') depth++;
			if (character == ')' && --depth == 0) return index;
			index++;
		}

		throw new FormatException("substituição sem fechar");
	}

	/// <summary>Nome, argumentos e se veio de xargs, tirando os prefixos <c>rtk [proxy]</c> e <c>xargs [-flags]</c> e juntando <c>git grep</c>.</summary>
	public static ShellCommand CommandName(IReadOnlyList<string> tokens)
	{
		var list = tokens.ToList();
		var fedByXargs = false;

		if (list.Count > 0 && list[0].Equals("rtk", StringComparison.OrdinalIgnoreCase))
		{
			list = list.Count > 1 && list[1].Equals("proxy", StringComparison.OrdinalIgnoreCase) ? list.Skip(2).ToList() : list.Skip(1).ToList();
		}

		if (list.Count > 0 && list[0].Equals("xargs", StringComparison.OrdinalIgnoreCase))
		{
			fedByXargs = true;
			list = list.Skip(1).ToList();
			while (list.Count > 0 && (list[0].StartsWith('-') || list[0] == "{}")) list.RemoveAt(0);
		}

		if (list.Count == 0) return new ShellCommand(null, Array.Empty<string>(), fedByXargs);

		var name = list[0].ToLowerInvariant();
		if (name == "git" && list.Count > 1 && list[1].Equals("grep", StringComparison.OrdinalIgnoreCase))
		{
			return new ShellCommand("git grep", list.Skip(2).ToList(), fedByXargs);
		}

		return new ShellCommand(name, list.Skip(1).ToList(), fedByXargs);
	}

	public static bool IsSearchCommand(string? name) => name != null && (SearchCommands.Contains(name) || name == "git grep");

	/// <summary>Separa padrão, caminhos e recursividade dos argumentos de grep/rg/findstr/Select-String.</summary>
	public static SearchArguments ParseSearchArguments(string name, IReadOnlyList<string> arguments)
	{
		string? pattern = null;
		var paths = new List<string>();
		var recursive = name is "rg" or "git grep";
		var searchesRevision = false;
		var isSelectString = name is "select-string" or "sls";
		var index = 0;

		while (index < arguments.Count)
		{
			var token = arguments[index];
			var lower = token.ToLowerInvariant();

			if (token == "--")
			{
				// git grep: o que ficou entre o padrão e o "--" é revisão (origin/develop), não caminho da worktree.
				if (name == "git grep" && pattern != null && paths.Count > 0)
				{
					searchesRevision = true;
					paths.Clear();
				}

				paths.AddRange(arguments.Skip(index + 1));
				break;
			}

			if (token.StartsWith('-') && token.Length > 1)
			{
				if (isSelectString)
				{
					if (lower == "-path" || lower.StartsWith("-lit"))
					{
						if (index + 1 < arguments.Count) paths.Add(arguments[index + 1]);
						index += 2;
					}
					else if (lower.StartsWith("-pat"))
					{
						pattern = index + 1 < arguments.Count ? arguments[index + 1] : null;
						index += 2;
					}
					else if (lower.StartsWith("-inc") || lower.StartsWith("-exc") || lower.StartsWith("-con") || lower.StartsWith("-enc") || lower.StartsWith("-cul"))
					{
						index += 2;
					}
					else
					{
						index++;
					}

					continue;
				}

				if (token.Contains('='))
				{
					index++;
					continue;
				}

				if (token is "-e" or "--regexp")
				{
					pattern = index + 1 < arguments.Count ? arguments[index + 1] : null;
					index += 2;
					continue;
				}

				if (SearchOptionsWithValue.Contains(token))
				{
					index += 2;
					continue;
				}

				if (token is "--recursive" or "--dereference-recursive" || RecursiveShortFlag.IsMatch(token)) recursive = true;
				index++;
				continue;
			}

			if (name == "findstr" && token.StartsWith('/'))
			{
				if (lower.StartsWith("/c:")) pattern = token.Substring(3);
				else if (lower == "/s") recursive = true;
				index++;
				continue;
			}

			if (pattern == null) pattern = token;
			else paths.Add(token);
			index++;
		}

		return new SearchArguments(pattern, paths, recursive, searchesRevision);
	}

	/// <summary>
	/// Atribuições literais da própria linha: <c>F=caminho</c> no Bash e <c>$f = caminho</c> no PowerShell. Valor vindo de
	/// <c>$(...)</c>, de outra variável ou de pipeline fica de fora — o hook não tem como conhecê-lo.
	/// </summary>
	public static Dictionary<string, string> Assignments(IReadOnlyList<ShellSegment> segments)
	{
		var assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (var segment in segments)
		{
			var tokens = segment.Tokens;
			if (tokens.Count == 0) continue;

			var bash = BashAssignment.Match(tokens[0]);
			if (bash.Success && tokens.Count == 1) Assign(assignments, bash.Groups[1].Value, bash.Groups[2].Value);
			else if (tokens.Count == 3 && tokens[1] == "=" && tokens[0].StartsWith('$') && tokens[0].Length > 1) Assign(assignments, tokens[0].Substring(1), tokens[2]);
		}

		return assignments;
	}

	private static void Assign(Dictionary<string, string> assignments, string name, string value)
	{
		if (value.Length == 0 || value.Contains('$') || value.Contains('`')) return;

		assignments[name] = value;
	}

	/// <summary>Substitui <c>$F</c> e <c>${F}</c> pelos valores conhecidos; o que não tem valor fica como está.</summary>
	public static string Resolve(string path, IReadOnlyDictionary<string, string> assignments)
	{
		return VariableReference.Replace(path, match => assignments.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);
	}

	public static bool HasVariable(string path) => path.Contains('$');

	/// <summary>Caminho de partida de uma listagem (find caminho ..., Get-ChildItem [-Path] caminho); null se não houver.</summary>
	public static string? ListingPath(string name, IReadOnlyList<string> arguments)
	{
		if (name == "find")
		{
			var start = arguments.TakeWhile(token => !token.StartsWith('-') && token != "!" && token != "(").ToList();
			return start.Count > 0 ? start[0] : null;
		}

		for (var index = 0; index < arguments.Count - 1; index++)
		{
			if (arguments[index].StartsWith("-pat", StringComparison.OrdinalIgnoreCase) || arguments[index].StartsWith("-lit", StringComparison.OrdinalIgnoreCase)) return arguments[index + 1];
		}

		for (var index = 0; index < arguments.Count; index++)
		{
			var token = arguments[index];
			if (token.StartsWith('-'))
			{
				var lower = token.ToLowerInvariant();
				if (lower.StartsWith("-fil") || lower.StartsWith("-inc") || lower.StartsWith("-exc")) index++;
				continue;
			}

			if (!token.Contains('*')) return token;
		}

		return null;
	}

	/// <summary>Extensões pedidas explicitamente na linha inteira: <c>*.cs</c>, <c>--include=*.cs</c>, <c>-t cs</c>, <c>--type=cs</c>.</summary>
	public static HashSet<string> ExtensionHints(string command)
	{
		var hints = new HashSet<string>(StringComparer.Ordinal);
		foreach (Match match in StarExtension.Matches(command)) hints.Add(match.Groups[1].Value.ToLowerInvariant());
		foreach (Match match in TypeOption.Matches(command)) hints.Add(match.Groups[1].Value.ToLowerInvariant());
		return hints;
	}

	/// <summary>Só interessa busca que varre arquivos; grep lendo de um pipe ou de stdin não é busca na árvore.</summary>
	public static bool IsTreeSearch(string name, IReadOnlyList<string> paths, bool recursive, bool piped, string? previousName, bool fedByXargs)
	{
		if (paths.Count > 0 || fedByXargs) return true;
		if (name is "select-string" or "sls") return piped && previousName != null && ListingCommands.Contains(previousName);
		if (name is "grep" or "egrep" or "fgrep" or "findstr") return recursive;
		return !piped;
	}

	/// <summary>-i, contexto (-A/-B/-C, --context, -Context) ou regex: o que só a busca crua dá.</summary>
	public static bool SearchNeedsRawSearch(string name, IReadOnlyList<string> arguments, string pattern)
	{
		if (CodeIntelligenceHook.HasRegex(pattern)) return true;

		foreach (var token in arguments)
		{
			var lower = token.ToLowerInvariant();
			if (name is "select-string" or "sls")
			{
				if (lower.StartsWith("-con")) return true;
				continue;
			}

			if (lower.StartsWith("--ignore-case") || lower.StartsWith("--smart-case") || lower.StartsWith("--multiline") ||
				lower.StartsWith("--context") || lower.StartsWith("--after-context") || lower.StartsWith("--before-context"))
			{
				return true;
			}

			if (ShortFlags.IsMatch(token) && (token.Contains('i') || token.IndexOfAny(['A', 'B', 'C', 'S', 'U']) >= 0)) return true;
		}

		return false;
	}

	/// <summary>Padrão de nome de uma listagem recursiva (find -name, Get-ChildItem -Recurse -Filter/-Include); null se não houver.</summary>
	public static string? ListingGlob(string name, IReadOnlyList<string> arguments)
	{
		if (name == "find")
		{
			for (var index = 0; index < arguments.Count - 1; index++)
			{
				if (arguments[index] is "-name" or "-iname") return arguments[index + 1];
			}

			return null;
		}

		if (!arguments.Any(token => token.Equals("-r", StringComparison.OrdinalIgnoreCase) || token.StartsWith("-rec", StringComparison.OrdinalIgnoreCase))) return null;

		for (var index = 0; index < arguments.Count - 1; index++)
		{
			var lower = arguments[index].ToLowerInvariant();
			if (lower.StartsWith("-fil") || lower.StartsWith("-inc")) return arguments[index + 1];
		}

		return arguments.FirstOrDefault(token => !token.StartsWith('-') && token.Contains('*'));
	}
}
