using System.Text.RegularExpressions;

namespace DeclIndex.Hook;

/// <summary>
/// PreToolUse: orienta o agente para a hierarquia de inteligência de código no Code (repositório cross-project,
/// arquivos de até 5000+ linhas). Port do check_code_intelligence.py de 2026-09-14, mesmas regras:
///   Glob : curinga no nome + .cs → nega e aponta find_usages/find_declarations -File; sob pasta explícita abaixo de Applications\ ou
///          Components\ é varredura de uma pasta, e passa.
///   Grep : arquivo único nomeado → permite, seja qual for o padrão (o find_usages só recebe pasta; grep, Read ou LSP
///          são o caminho); padrão com cara de declaração C# em alvo C# → nega com o comando exato do find_declarations;
///          identificador puro em pasta com .cs → nega apontando find_declarations (declaração) e find_usages (uso);
///          regex real, contexto, -i ou multiline → permite.
///   Read : .cs sem limit e ≥ 2000 linhas, ou outro código sem limit e ≥ 500 → nega e aponta summarize_file/find_declarations -File.
///   Bash/PowerShell : grep, rg, git grep, findstr e Select-String seguem a regra do Grep; find -name e
///          Get-ChildItem -Recurse -Filter/-Include seguem a do Glob. Busca lendo de um pipe passa. F=caminho da
///          própria linha resolve o $F do alvo; variável sem valor conhecido não tem alvo para julgar e passa;
///          git grep padrão <rev> -- lê outro commit e passa.
/// O hook só age dentro de um checkout do Code (ancestral com Applications\ e Components\) ou sob MC_HOOK_ROOTS.
/// </summary>
public static class CodeIntelligenceHook
{
	private const int CsBlockLines = 2000;
	private const int OtherBlockLines = 500;
	private const string Identifier = "[A-Za-z_][A-Za-z0-9_]*";

	private static readonly HashSet<string> CodeExtensions = new(StringComparer.Ordinal) { ".cs", ".js", ".ts", ".tsx", ".jsx", ".sql", ".py" };
	private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", ".git", ".vs", "packages", "dist", "publish" };
	private static readonly Dictionary<string, string> DeclarationKinds = new(StringComparer.Ordinal)
	{
		["class"] = "class", ["interface"] = "interface", ["struct"] = "struct", ["enum"] = "enum", ["record"] = "record", ["partial"] = "class",
	};

	private static readonly Regex BroadCsGlob = new(@"\*[^/\\]*\.cs$");
	private static readonly Regex ClassToken = new(@"\\[sSwWdD][*+?]?");
	private static readonly Regex DotQuantifier = new(@"(?<!\\)\.[*+?]");
	private static readonly Regex Whitespace = new(@"\s+");
	private static readonly Regex GitBashDrive = new(@"^/([a-zA-Z])(/|$)");
	private static readonly Regex AttributePattern = new(@"\\\[ ?\(?(" + Identifier + ")");
	private static readonly Regex BasePattern = new(@"(?<!:): ?\(?(?:" + Identifier + @"\|)*(" + Identifier + ")");
	private static readonly Regex KeywordPattern = new(@"(?<![\w.])(class|interface|struct|enum|record|partial)\b(?: class)? ?\(?(" + Identifier + ")?");
	private static readonly Regex Modifiers = new(@"\b(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async)\b");
	private static readonly Regex MethodPattern = new("(" + Identifier + @") ?\\\(");
	private static readonly Regex RegexMetacharacters = new(@"[.*+?\[\](){}|^$\\]");
	private static readonly Regex PureIdentifier = new(@"\A" + Identifier + @"\z");

	public static HookDecision Decide(HookRequest request, HookEnvironment environment)
	{
		// Registrado no nível de usuário (vale para toda sessão da máquina), o hook só age nos repositórios
		// que o ferramental cobre. Sem cwd no payload, assume que está dentro.
		if (request.Cwd != null && !SessionInScope(request.Cwd, environment.HookRoots)) return HookDecision.Allow;

		return request.ToolName switch
		{
			"Glob" => HandleGlob(request, environment),
			"Grep" => HandleGrep(request, environment),
			"Read" => HandleRead(request, environment),
			"Bash" or "PowerShell" => HandleShell(request, environment),
			_ => HookDecision.Allow,
		};
	}

	// ---------- Escopo ----------

	/// <summary>Dentro de um checkout do Code (ancestral com Applications\ e Components\) ou sob uma das raízes extras.</summary>
	public static bool SessionInScope(string cwd, IReadOnlyList<string> hookRoots)
	{
		var current = NormalizeDirectory(ResolvePath(cwd, null));
		if (FindCodeAncestor(current) != null) return true;

		foreach (var root in hookRoots)
		{
			var normalizedRoot = NormalizeDirectory(root);
			if (normalizedRoot.Length == 0) continue;
			if (current.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
				current.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	public static string? FindCodeAncestor(string directory)
	{
		var current = directory;
		while (!string.IsNullOrEmpty(current))
		{
			if (Directory.Exists(Path.Combine(current, "Applications")) && Directory.Exists(Path.Combine(current, "Components"))) return current;
			current = Path.GetDirectoryName(current);
		}

		return null;
	}

	private static string NormalizeDirectory(string path)
	{
		try
		{
			return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}
		catch (Exception)
		{
			return path;
		}
	}

	// ---------- Glob ----------

	/// <summary>Verdadeiro quando o componente de nome do pattern tem curinga e termina em .cs (**/*.cs, **/*Controller*.cs; não **/ExactFile.cs nem *.csproj).</summary>
	public static bool IsBroadCsGlob(string pattern)
	{
		var filename = Regex.Split(pattern, @"[/\\]")[^1];
		return BroadCsGlob.IsMatch(filename);
	}

	private static HookDecision HandleGlob(HookRequest request, HookEnvironment environment)
	{
		var pattern = request.GetString("pattern") ?? "";
		if (!IsBroadCsGlob(pattern)) return HookDecision.Allow;

		// Sem path, a pasta de partida é o prefixo fixo do próprio pattern (Applications/X/Tests/**/*.cs).
		var start = request.GetString("path") ?? GlobPrefix(pattern);
		return ListingIsBounded(start, request.Cwd) ? HookDecision.Allow : DenyBroadGlob("Glob", pattern, environment);
	}

	/// <summary>Componentes do pattern antes do primeiro curinga: <c>Applications/X/**/*.cs</c> → <c>Applications/X</c>.</summary>
	private static string GlobPrefix(string pattern)
	{
		var fixedParts = Regex.Split(pattern, @"[/\\]").TakeWhile(part => part.IndexOfAny(['*', '?', '[']) < 0).ToList();
		return string.Join("/", fixedParts);
	}

	/// <summary>
	/// Listagem sob caminho explícito abaixo de Applications\ ou Components\ (ou fora deles) é varredura de uma pasta, não a
	/// varredura cross-project que a regra do Glob evita. Raiz, Applications\ e Components\ inteiros continuam amplos; variável
	/// sem valor conhecido não dá para julgar e passa.
	/// </summary>
	public static bool ListingIsBounded(string start, string? basePath)
	{
		if (start.Length == 0) return false;
		if (ShellCommandParser.HasVariable(start)) return true;

		var resolved = NormalizeDirectory(ResolvePath(start, basePath));
		var root = FindCodeAncestor(resolved);
		if (root == null) return false;

		var relative = resolved.Length > root.Length ? resolved.Substring(root.Length).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : "";
		if (relative.Length == 0) return false;

		var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
		return parts.Length > 1 || !(parts[0].Equals("Applications", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("Components", StringComparison.OrdinalIgnoreCase));
	}

	private static HookDecision DenyBroadGlob(string toolLabel, string pattern, HookEnvironment environment)
	{
		return HookDecision.Deny(
			$"{toolLabel} '{pattern}' sobre .cs é ineficaz neste repositório cross-project.\n" +
			"Hierarquia correta de code intelligence:\n" +
			$"  • Símbolo ou texto específico  →  {Script(environment, "find_usages.ps1")} <símbolo>\n" +
			$"  • Estrutura de um .cs localizado  →  {Script(environment, "find_declarations.ps1")} -File <caminho/do/arquivo.cs>\n" +
			"  • Caminho exato desconhecido  →  Glob OK, ex: '**/ExactFile.cs'  (sem wildcard no nome)\n" +
			"  • Arquivos de uma pasta específica  →  git ls-files <pasta>  (ou a listagem com a pasta explícita, abaixo de Applications\\ ou Components\\)");
	}

	// ---------- Padrões ----------

	/// <summary>Reduz o regex a algo legível: tokens de classe (\s, \w, \d, \S), .* e \b viram espaço.</summary>
	public static string NormalizePattern(string pattern)
	{
		var text = ClassToken.Replace(pattern, " ");
		text = DotQuantifier.Replace(text, " ");
		text = text.Replace(@"\b", "");
		text = Whitespace.Replace(text, " ");
		return text.Trim();
	}

	/// <summary>
	/// (descrição, argumentos do find_declarations) quando o padrão tem cara de declaração C#; senão null. Só formas
	/// inequívocas: palavra-chave de tipo, base após dois-pontos, atributo entre colchetes escapados, ou modificador
	/// seguido de nome e parêntese.
	/// </summary>
	public static (string Description, string Arguments)? ClassifyDeclaration(string pattern)
	{
		var text = NormalizePattern(pattern);

		var attribute = AttributePattern.Match(text);
		if (attribute.Success)
		{
			return ($"tipos com o atributo [{attribute.Groups[1].Value}]", $"-Attribute {attribute.Groups[1].Value}");
		}

		var baseMatch = BasePattern.Match(text);
		if (baseMatch.Success && !text.Substring(0, baseMatch.Index).Contains('?') && !text.Contains("::"))
		{
			return ($"quem herda ou implementa '{baseMatch.Groups[1].Value}'", $"-Base {baseMatch.Groups[1].Value} -Kind class");
		}

		var keyword = KeywordPattern.Match(text);
		if (keyword.Success && keyword.Groups[2].Success && keyword.Groups[2].Value.Length > 0 && !DeclarationKinds.ContainsKey(keyword.Groups[2].Value))
		{
			var kind = DeclarationKinds[keyword.Groups[1].Value];
			return ($"declaração de {kind} '{keyword.Groups[2].Value}'", $"-Name {keyword.Groups[2].Value} -Kind {kind}");
		}

		if (Modifiers.IsMatch(text))
		{
			var method = MethodPattern.Match(text);
			if (method.Success)
			{
				return ($"declaração do método '{method.Groups[1].Value}'", $"-Name {method.Groups[1].Value} -Kind method,ctor");
			}
		}

		if (keyword.Success)
		{
			var kind = DeclarationKinds[keyword.Groups[1].Value];
			return ($"declarações de {kind}", $"-Kind {kind} -File <pasta/>   (com -Raw dá para agrupar e contar)");
		}

		return null;
	}

	/// <summary>Regex de verdade (metacaracteres além de \b) é o que o find_usages não cobre.</summary>
	public static bool HasRegex(string pattern) => RegexMetacharacters.IsMatch(pattern.Replace(@"\b", ""));

	private static HookDecision DenyDeclaration(string toolLabel, string pattern, string description, string arguments, HookEnvironment environment)
	{
		return HookDecision.Deny(
			$"{toolLabel} '{pattern}' procura {description}: isso é DECLARAÇÃO em C#, e o {toolLabel} mistura chamadas, " +
			"perde partials e não separa por kind.\n" +
			"Use o índice de declarações (só declarações, cobre #if, agrupa por arquivo, mostra projeto):\n" +
			$"  {Script(environment, "find_declarations.ps1")} {arguments}\n" +
			"Filtros: -Name, -Kind (class,interface,enum,struct,method,ctor,property,field,enummember), -Base, " +
			"-Attribute, -Container Tipo, -File <trecho/ com barra final>, -Project, -IncludeGenerated, -Raw.\n" +
			"Grep e find_usages.ps1 continuam certos para USO (\"onde X é usado\", \"quem chama X\").");
	}

	private static HookDecision DenyPlainSearch(string toolLabel, string pattern, HookEnvironment environment)
	{
		var lines = new List<string> { $"{toolLabel} '{pattern}' em C# sem regex nem contexto: o script cobre isso com menos ruído, agrupado por arquivo." };

		if (PureIdentifier.IsMatch(pattern))
		{
			lines.Add($"  • Declaração (quem declara, herda, atributo, membros)  →  {Script(environment, "find_declarations.ps1")} -Name {pattern}");
			lines.Add($"  • Uso (onde é usado, quem chama)                        →  {Script(environment, "find_usages.ps1")} {pattern} [-Path <pasta>]");
		}
		else
		{
			lines.Add($"  → {Script(environment, "find_usages.ps1")} \"{pattern}\" [-Path <pasta>]");
		}

		lines.Add("Grep só para regex real, linhas de contexto (-A/-B/-C), -i ou multiline; num arquivo único, Read ou LSP.");
		return HookDecision.Deny(string.Join("\n", lines));
	}

	private static string Script(HookEnvironment environment, string name) => $"& \"{Path.Combine(environment.ScriptsDirectory, name)}\"";

	// ---------- Alvo ----------

	/// <summary>Caminho absoluto: converte <c>/e/x</c> do Git Bash em <c>E:/x</c> e resolve relativo contra a base (cwd do payload).</summary>
	public static string ResolvePath(string? path, string? basePath)
	{
		path = GitBashDrive.Replace(path ?? "", match => match.Groups[1].Value.ToUpperInvariant() + ":/");
		if (basePath != null && !Path.IsPathRooted(path)) path = Path.Combine(basePath, path);
		return path;
	}

	/// <summary>
	/// Verdadeiro se houver algum .cs até maxDepth níveis. Diretório inexistente ou árvore grande demais para decidir
	/// dentro do teto de entradas contam como código: é onde o grep cru mais custa.
	/// </summary>
	public static bool DirectoryHasCSharp(string directory, int maxDepth = 4, int maxEntries = 3000)
	{
		if (!Directory.Exists(directory)) return true;

		var pending = new Stack<(string Path, int Depth)>();
		pending.Push((directory, 0));
		var seen = 0;

		while (pending.Count > 0)
		{
			var (current, depth) = pending.Pop();
			IEnumerable<FileSystemInfo> entries;
			try
			{
				entries = new DirectoryInfo(current).EnumerateFileSystemInfos().ToList();
			}
			catch (Exception)
			{
				continue;
			}

			foreach (var entry in entries)
			{
				seen++;
				if (seen > maxEntries) return true;
				if (entry is FileInfo file && file.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return true;
				if (entry is DirectoryInfo child && depth < maxDepth && !SkipDirectories.Contains(child.Name)) pending.Push((child.FullName, depth + 1));
			}
		}

		return false;
	}

	/// <summary>
	/// Pasta existente: só é C# se tiver .cs dentro. Arquivo: a extensão decide (um .ps1 ou .md nunca é declaração C#).
	/// O disco é consultado antes da extensão porque pastas com ponto no nome (MultiClubes.Controller) são a regra no Code.
	/// </summary>
	public static bool PathIsCSharp(string path)
	{
		if (Directory.Exists(path)) return DirectoryHasCSharp(path);

		var extension = ExtensionOf(path);
		if (extension.Length > 0) return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase);
		return DirectoryHasCSharp(path);
	}

	/// <summary>
	/// Arquivo nomeado (com extensão, sem curinga e que não é uma pasta existente): o find_usages só recebe pasta, então a
	/// busca crua passa. <c>pasta/*.cs</c> tem extensão mas é a pasta inteira — o shell ou o Select-String expandem.
	/// </summary>
	private static bool LooksLikeFile(string path, string? basePath)
	{
		if (path.IndexOfAny(['*', '?']) >= 0) return false;

		return ExtensionOf(path).Length > 0 && !Directory.Exists(ResolvePath(path, basePath));
	}

	/// <summary>Como o os.path.splitext: extensão só do último componente, e um nome que começa por ponto não tem extensão.</summary>
	private static string ExtensionOf(string path)
	{
		var name = Regex.Split(path, @"[/\\]")[^1];
		var dot = name.LastIndexOf('.');
		return dot <= 0 ? "" : name.Substring(dot);
	}

	private static bool IsCSharpTarget(HookRequest request)
	{
		var fileType = (request.GetString("type") ?? "").ToLowerInvariant();
		var glob = (request.GetString("glob") ?? "").ToLowerInvariant();

		if (fileType.Length > 0) return fileType is "cs" or "csharp";
		if (glob.Length > 0) return glob.Contains(".cs") && !glob.Contains(".csproj") && !glob.Contains(".cshtml");
		return PathIsCSharp(ResolvePath(request.GetString("path") ?? "", request.Cwd));
	}

	// ---------- Grep ----------

	/// <summary>Contexto, -i, multiline ou regex: o que só o Grep dá.</summary>
	private static bool GrepNeedsRawSearch(HookRequest request, string pattern)
	{
		if (request.IsPresent("-A") || request.IsPresent("-B") || request.IsPresent("-C") || request.IsPresent("context")) return true;
		if (request.IsTruthy("-i") || request.IsTruthy("multiline")) return true;
		return HasRegex(pattern);
	}

	private static HookDecision HandleGrep(HookRequest request, HookEnvironment environment)
	{
		var pattern = request.GetString("pattern") ?? "";
		if (pattern.Length == 0 || !IsCSharpTarget(request)) return HookDecision.Allow;

		// Arquivo único: o find_usages só recebe pasta; aqui Grep, Read ou LSP são o caminho, seja qual for o padrão.
		if (LooksLikeFile(request.GetString("path") ?? "", request.Cwd)) return HookDecision.Allow;

		var classified = ClassifyDeclaration(pattern);
		if (classified != null) return DenyDeclaration("Grep", pattern, classified.Value.Description, classified.Value.Arguments, environment);

		if (GrepNeedsRawSearch(request, pattern)) return HookDecision.Allow;

		return DenyPlainSearch("Grep", pattern, environment);
	}

	// ---------- Bash / PowerShell ----------

	private static HookDecision HandleShell(HookRequest request, HookEnvironment environment)
	{
		var command = request.GetString("command") ?? "";
		List<ShellSegment> segments;
		try
		{
			segments = ShellCommandParser.SplitSegments(command);
		}
		catch (FormatException)
		{
			return HookDecision.Allow;
		}

		var named = segments.Select(segment => ShellCommandParser.CommandName(segment.Tokens)).ToList();
		var assignments = ShellCommandParser.Assignments(segments);

		// `cd X && grep ...`: os caminhos do resto da linha são relativos a X.
		var basePath = request.Cwd;
		if (named.Count > 0 && named[0].Name != null && ShellCommandParser.ChangeDirectoryCommands.Contains(named[0].Name!) && named[0].Arguments.Count > 0)
		{
			basePath = ResolvePath(named[0].Arguments[^1], request.Cwd);
		}

		for (var position = 0; position < named.Count; position++)
		{
			var (name, arguments, fedByXargs) = named[position];
			if (!ShellCommandParser.IsSearchCommand(name)) continue;

			var search = ShellCommandParser.ParseSearchArguments(name!, arguments);
			var previousName = position > 0 ? named[position - 1].Name : null;
			if (search.Pattern == null || search.Pattern.Length == 0) continue;
			if (search.SearchesRevision) continue;
			if (!ShellCommandParser.IsTreeSearch(name!, search.Paths, search.Recursive, segments[position].Piped, previousName, fedByXargs)) continue;

			// F=caminho da própria linha resolve; variável de $(...) ou de pipeline não, e sem valor não há alvo para julgar.
			var paths = search.Paths.Select(path => ShellCommandParser.Resolve(path, assignments)).ToList();
			if (paths.Count > 0 && paths.All(ShellCommandParser.HasVariable)) continue;
			paths = paths.Where(path => !ShellCommandParser.HasVariable(path)).ToList();

			if (!ShellTargetIsCSharp(command, paths, basePath)) continue;

			// Só arquivos nomeados (sem pasta): o find_usages só recebe pasta; aqui grep, Read ou LSP são o caminho, seja qual for o padrão.
			if (paths.Count > 0 && paths.All(path => LooksLikeFile(path, basePath))) continue;

			var classified = ClassifyDeclaration(search.Pattern);
			if (classified != null) return DenyDeclaration(name!, search.Pattern, classified.Value.Description, classified.Value.Arguments, environment);

			if (ShellCommandParser.SearchNeedsRawSearch(name!, arguments, search.Pattern)) continue;

			return DenyPlainSearch(name!, search.Pattern, environment);
		}

		for (var position = 0; position < named.Count; position++)
		{
			var (name, arguments, _) = named[position];
			if (name == null || !ShellCommandParser.ListingCommands.Contains(name)) continue;

			// Listagem que alimenta uma busca (gci | Select-String, find | xargs grep) é julgada pela busca, acima.
			if (position + 1 < named.Count && ShellCommandParser.IsSearchCommand(named[position + 1].Name)) continue;

			var glob = ShellCommandParser.ListingGlob(name, arguments);
			if (glob == null || !IsBroadCsGlob(glob)) continue;

			var start = ShellCommandParser.Resolve(ShellCommandParser.ListingPath(name, arguments) ?? "", assignments);
			if (!ListingIsBounded(start, basePath)) return DenyBroadGlob(name, glob, environment);
		}

		return HookDecision.Allow;
	}

	/// <summary>Mesma regra do IsCSharpTarget: filtro explícito decide; senão cada caminho (ou o cwd) decide.</summary>
	private static bool ShellTargetIsCSharp(string command, IReadOnlyList<string> paths, string? basePath)
	{
		var hints = ShellCommandParser.ExtensionHints(command);
		if (hints.Count > 0) return hints.Contains("cs");

		var targets = paths.Count > 0 ? paths : [""];
		return targets.Any(path => PathIsCSharp(ResolvePath(path, basePath)));
	}

	// ---------- Read ----------

	private static HookDecision HandleRead(HookRequest request, HookEnvironment environment)
	{
		var path = request.GetString("file_path") ?? "";

		// Leitura com limit é cirúrgica: sempre permitida.
		if (request.IsPresent("limit")) return HookDecision.Allow;
		if (!File.Exists(path)) return HookDecision.Allow;

		var extension = ExtensionOf(path).ToLowerInvariant();
		if (!CodeExtensions.Contains(extension)) return HookDecision.Allow;

		var lines = CountLines(path);
		var threshold = extension == ".cs" ? CsBlockLines : OtherBlockLines;
		if (lines < threshold) return HookDecision.Allow;

		if (extension == ".cs")
		{
			return HookDecision.Deny(
				$"Arquivo C# com {lines} linhas: leitura integral desperdiça contexto.\n" +
				"Alternativas em ordem:\n" +
				$"  1. {Script(environment, "summarize_file.ps1")} \"{path}\"\n" +
				$"  2. {Script(environment, "find_declarations.ps1")} -File \"{RelativeToCode(path)}\" -IncludeGenerated  (lista classes, métodos, propriedades)\n" +
				"  3. Read com offset+limit  depois de identificar a região necessária");
		}

		return HookDecision.Deny(
			$"Arquivo {extension} com {lines} linhas: leitura integral desperdiça contexto.\n" +
			"Use:\n" +
			$"  {Script(environment, "summarize_file.ps1")} \"{path}\"\n" +
			"Depois Read com offset+limit na região necessária.");
	}

	/// <summary>O -File do find_declarations casa com trecho do caminho relativo ao checkout, com barra normal.</summary>
	private static string RelativeToCode(string path)
	{
		var root = FindCodeAncestor(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "");

		return root == null ? Path.GetFileName(path) : Path.GetRelativePath(root, path).Replace('\\', '/');
	}

	private static int CountLines(string path)
	{
		try
		{
			return File.ReadLines(path).Count();
		}
		catch (Exception)
		{
			return 0;
		}
	}
}
