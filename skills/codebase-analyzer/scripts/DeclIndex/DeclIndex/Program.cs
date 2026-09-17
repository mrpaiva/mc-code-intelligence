// Índice de declarações C# do monorepo. Uso:
//   DeclIndex refresh --worktree <raiz> [--store <dir>] [--quiet]   atualiza o índice e imprime o caminho do TSV
//   DeclIndex closure --worktree <raiz> (--scope <pasta> | --symbol <nome> | --file <arquivo>) [--no-refresh]
//                                                                    fatia para análise de serviços WCF: tipos (coluna
//                                                                    extra "loaded" 1/0) e membros dos tipos carregados
//   DeclIndex prune [--store <dir>]                                  remove blobs sem referência em manifesto algum
// Códigos de saída: 0 ok, 1 uso, 2 git falhou.

using DeclIndex;

var store = Environment.GetEnvironmentVariable("MC_CODEINDEX")
	?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mc-code-intelligence", "index");
string? worktree = null;
string? scope = null;
string? symbol = null;
string? file = null;
var quiet = false;
var noRefresh = false;
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
		case "--quiet": quiet = true; break;
		case "--no-refresh": noRefresh = true; break;
		default: return Usage();
	}
}

try
{
	switch (command)
	{
		case "refresh" when worktree != null:
			return RunRefresh(worktree, store, quiet);
		case "closure" when worktree != null && (scope != null || symbol != null || file != null):
			return RunClosure(worktree, store, scope, symbol, file, noRefresh);
		case "prune":
			return Prune(store);
		default:
			return Usage();
	}
}
catch (GitException exception)
{
	Console.Error.WriteLine(exception.Message);
	return 2;
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
		Console.Error.WriteLine($"arquivos: {report.Files} | parseados: {report.Parsed} | blobs lidos: {report.BlobsRead} | invalidou: {report.Invalidated} | erros de parse: {report.ParseErrors.Count} | {report.Elapsed.TotalMilliseconds:N0} ms (git {phases.GitMs} + leitura {phases.ReadMs} + parse {phases.ParseMs} + materialização {phases.MaterializeMs})");
	}

	Console.WriteLine(report.TsvPath);
	return 0;
}

static int Usage()
{
	Console.Error.WriteLine("uso: DeclIndex refresh --worktree <raiz> [--store <dir>] [--quiet] | DeclIndex closure --worktree <raiz> (--scope <pasta> | --symbol <nome> | --file <arquivo>) [--no-refresh] | DeclIndex prune [--store <dir>]");
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
	return 0;
}
