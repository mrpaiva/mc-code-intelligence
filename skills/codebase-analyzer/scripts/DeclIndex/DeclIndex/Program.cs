// Índice de declarações C# e corpus de texto do monorepo. Uso:
//   DeclIndex refresh --worktree <raiz> [--store <dir>] [--quiet]   atualiza índice, manifesto e corpus; imprime o caminho do TSV
//   DeclIndex usages --worktree <raiz> --symbol <nome> [--scope <pasta>] [--include <glob>]... [--no-refresh] [--show-line]
//                                                                    onde o símbolo aparece como palavra inteira, em todo texto
//                                                                    da worktree: uma linha "caminho<TAB>linha" por acerto;
//                                                                    --show-line acrescenta "<TAB>texto" (sem indentação, até 200
//                                                                    caracteres + "…"; pode conter TAB, é sempre a última coluna)
//   DeclIndex search --worktree <raiz> --term <palavra>... [--top 20] [--scope <pasta>] [--include <glob>]... [--no-refresh]
//                                                                    arquivos ranqueados por BM25 sobre os termos (palavra inteira):
//                                                                    "caminho<TAB>score<TAB>termos casados<TAB>linhas"
//   DeclIndex closure --worktree <raiz> (--scope <pasta> | --symbol <nome> | --file <arquivo>) [--no-refresh]
//                                                                    fatia para análise de serviços WCF: tipos (coluna
//                                                                    extra "loaded" 1/0) e membros dos tipos carregados
//   DeclIndex prune [--store <dir>]                                  remove blobs e linhas do corpus sem referência em manifesto algum
// Códigos de saída: 0 ok, 1 uso, 2 git ou armazém falhou.

using DeclIndex;

var store = Environment.GetEnvironmentVariable("MC_CODEINDEX")
	?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mc-code-intelligence", "index");
string? worktree = null;
string? scope = null;
string? symbol = null;
string? file = null;
var includes = new List<string>();
var terms = new List<string>();
var top = 20;
var quiet = false;
var noRefresh = false;
var showLine = false;
var command = args.Length > 0 ? args[0] : "";

for (var index = 1; index < args.Length; index++)
{
	switch (args[index])
	{
		case "--store": store = args[++index]; break;
		case "--worktree": worktree = args[++index]; break;
		case "--scope": scope = args[++index]; break;
		case "--symbol": symbol = args[++index]; break;
		case "--file": file = args[++index]; break;
		case "--include": includes.Add(args[++index]); break;
		case "--term": terms.Add(args[++index]); break;
		case "--top": top = int.Parse(args[++index]); break;
		case "--quiet": quiet = true; break;
		case "--no-refresh": noRefresh = true; break;
		case "--show-line": showLine = true; break;
		default: return Usage();
	}
}

try
{
	switch (command)
	{
		case "refresh" when worktree != null:
			return RunRefresh(worktree, store, quiet);
		case "usages" when worktree != null && symbol != null:
			return RunUsages(worktree, store, symbol, scope, includes, noRefresh, showLine);
		case "search" when worktree != null && terms.Count > 0:
			return RunSearch(worktree, store, terms, top, scope, includes, noRefresh);
		case "closure" when worktree != null && (scope != null || symbol != null || file != null):
			return RunClosure(worktree, store, scope, symbol, file, noRefresh);
		case "prune":
			return Prune(store);
		default:
			return Usage();
	}
}
catch (Exception exception) when (exception is GitException or IOException)
{
	Console.Error.WriteLine(exception.Message);
	return 2;
}

static int RunUsages(string worktree, string store, string symbol, string? scope, List<string> includes, bool noRefresh, bool showLine)
{
	var stopwatch = System.Diagnostics.Stopwatch.StartNew();
	var root = Path.GetFullPath(worktree);

	if (!noRefresh || !File.Exists(WorktreeIndex.ManifestPathFor(store, root)))
	{
		var report = Refresh.Run(root, store);
		foreach (var error in report.ParseErrors) Console.Error.WriteLine($"parse-error\t{error}");
	}

	var usages = Usages.Find(store, root, symbol, scope, includes, showLine);

	using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
	foreach (var usage in usages) output.WriteLine(showLine ? $"{usage.Path}\t{usage.Line}\t{usage.Text}" : $"{usage.Path}\t{usage.Line}");

	Console.Error.WriteLine($"usages: {usages.Count} referência(s) em {usages.Select(usage => usage.Path).Distinct().Count()} arquivo(s) | {stopwatch.ElapsedMilliseconds:N0} ms");
	return 0;
}

static int RunSearch(string worktree, string store, List<string> terms, int top, string? scope, List<string> includes, bool noRefresh)
{
	var stopwatch = System.Diagnostics.Stopwatch.StartNew();
	var root = Path.GetFullPath(worktree);

	if (!noRefresh || !File.Exists(WorktreeIndex.ManifestPathFor(store, root)))
	{
		var report = Refresh.Run(root, store);
		foreach (var error in report.ParseErrors) Console.Error.WriteLine($"parse-error\t{error}");
	}

	var files = TermSearch.Rank(store, root, terms, top, scope, includes);

	using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
	foreach (var file in files) output.WriteLine($"{file.Path}\t{file.Score.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}\t{string.Join(',', file.Terms)}\t{string.Join(',', file.Lines)}");

	Console.Error.WriteLine($"search: {files.Count} arquivo(s) | {stopwatch.ElapsedMilliseconds:N0} ms");
	return 0;
}

static int RunClosure(string worktree, string store, string? scope, string? symbol, string? file, bool noRefresh)
{
	var stopwatch = System.Diagnostics.Stopwatch.StartNew();
	var tsvPath = WorktreeIndex.TsvPathFor(store, Path.GetFullPath(worktree));

	if (!noRefresh || !File.Exists(tsvPath))
	{
		var report = Refresh.Run(worktree, store);
		foreach (var error in report.ParseErrors) Console.Error.WriteLine($"parse-error\t{error}");
		tsvPath = report.TsvPath;
	}

	var rows = File.ReadLines(tsvPath).Skip(1).Select(IndexRow.Parse).Where(row => row != null).Select(row => row!);
	var closure = new ServiceClosure(rows);
	var slice = scope != null ? closure.ForScope(scope) : symbol != null ? closure.ForSymbol(symbol) : closure.ForFile(file!);
	var loaded = new HashSet<string>(slice.LoadedTypeKeys, StringComparer.Ordinal);

	using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
	output.WriteLine(WorktreeIndex.Header + "\tloaded");
	foreach (var type in slice.Types) output.WriteLine(type.ToTsv() + "\t" + (loaded.Contains(type.TypeKey) ? "1" : "0"));
	foreach (var member in slice.Members) output.WriteLine(member.ToTsv() + "\t1");

	Console.Error.WriteLine($"closure: tipos {slice.Types.Count} (carregados {loaded.Count}) | membros {slice.Members.Count} | {stopwatch.ElapsedMilliseconds:N0} ms");
	return 0;
}

static int RunRefresh(string worktree, string store, bool quiet)
{
	var report = Refresh.Run(worktree, store);

	foreach (var error in report.ParseErrors)
	{
		Console.Error.WriteLine($"parse-error\t{error}");
	}

	if (!quiet)
	{
		var phases = report.Phases;
		if (report.Reused) Console.Error.WriteLine($"arquivos: {report.Files} | TSV reusado (carimbo dentro de {Freshness.Window.TotalSeconds:N0} s, index do git igual, sem edição pelo agente) | {report.Elapsed.TotalMilliseconds:N0} ms");
		else Console.Error.WriteLine($"arquivos: {report.Files} (texto: {report.Texts}) | parseados: {report.Parsed} | blobs lidos: {report.BlobsRead} | corpus +{report.CorpusAppended} | invalidou: {report.Invalidated} | erros de parse: {report.ParseErrors.Count} | {report.Elapsed.TotalMilliseconds:N0} ms (git {phases.GitMs} + leitura {phases.ReadMs} + parse {phases.ParseMs} + materialização {phases.MaterializeMs} + corpus {phases.CorpusMs})");
	}

	Console.WriteLine(report.TsvPath);
	return 0;
}

static int Usage()
{
	Console.Error.WriteLine("uso: DeclIndex refresh --worktree <raiz> [--store <dir>] [--quiet] | DeclIndex usages --worktree <raiz> --symbol <nome> [--scope <pasta>] [--include <glob>]... [--no-refresh] [--show-line] | DeclIndex search --worktree <raiz> --term <palavra>... [--top 20] [--scope <pasta>] [--include <glob>]... [--no-refresh] | DeclIndex closure --worktree <raiz> (--scope <pasta> | --symbol <nome> | --file <arquivo>) [--no-refresh] | DeclIndex prune [--store <dir>]");
	return 1;
}

static int Prune(string storeRoot)
{
	var worktrees = Path.Combine(storeRoot, "worktrees");
	if (!Directory.Exists(worktrees)) return 0;

	var referenced = new HashSet<string>(StringComparer.Ordinal);

	foreach (var manifest in Directory.EnumerateFiles(worktrees, "*.manifest"))
	{
		foreach (var line in File.ReadLines(manifest))
		{
			referenced.Add(line.Split('\t')[0]);
		}
	}

	new BlobStore(storeRoot).Prune(referenced);
	new Corpus(storeRoot).Prune(referenced);
	return 0;
}
