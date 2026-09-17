using System.Diagnostics;
using System.Text;

namespace DeclIndex;

public sealed record SourceEntry(string RelativePath, string Sha)
{
	public bool IsCSharp => RelativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
}

public sealed class GitException(string message) : Exception(message);

/// <summary>Lista os arquivos da worktree com o SHA de blob que o git conhece, recalculando o dos modificados e não rastreados. Os .cs alimentam o índice de declarações; todos alimentam o corpus.</summary>
public static class GitWorktree
{
	private static readonly Encoding Utf8SemBom = new UTF8Encoding(false);

	public static IReadOnlyList<SourceEntry> ListSources(string root)
	{
		var tracked = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var record in Run(root, "ls-files", "-s", "-z").Split('\0', StringSplitOptions.RemoveEmptyEntries))
		{
			var tab = record.IndexOf('\t');
			var fields = record[..tab].Split(' ');
			tracked[record[(tab + 1)..]] = fields[1];
		}

		var dirty = new List<string>();
		var records = Run(root, "status", "--porcelain", "-z", "--no-renames", "--untracked-files=all").Split('\0', StringSplitOptions.RemoveEmptyEntries);

		for (var index = 0; index < records.Length; index++)
		{
			var status = records[index][..2];
			var path = records[index][3..];
			if (status[0] == 'R' || status[0] == 'C') index++;

			if (status[0] == 'D' || status[1] == 'D') tracked.Remove(path);
			else if (status == "??" || status[1] == 'M' || status[1] == 'A' || status[1] == 'T') dirty.Add(path);
		}

		if (dirty.Count > 0)
		{
			var hashes = RunWithInput(root, string.Join('\n', dirty) + "\n", "hash-object", "--stdin-paths").Split('\n', StringSplitOptions.RemoveEmptyEntries);

			for (var index = 0; index < dirty.Count; index++)
			{
				tracked[dirty[index]] = hashes[index].Trim();
			}
		}

		return tracked
			.Select(pair => new SourceEntry(pair.Key, pair.Value))
			.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
			.ToList();
	}

	private static string Run(string root, params string[] arguments) => RunWithInput(root, null, arguments);

	private static string RunWithInput(string root, string? input, params string[] arguments)
	{
		var info = new ProcessStartInfo("git")
		{
			WorkingDirectory = root,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = input != null,
			StandardOutputEncoding = Utf8SemBom,
			StandardErrorEncoding = Utf8SemBom
		};

		if (input != null) info.StandardInputEncoding = Utf8SemBom;

		info.ArgumentList.Add("-c");
		info.ArgumentList.Add("core.quotepath=off");
		foreach (var argument in arguments) info.ArgumentList.Add(argument);

		using var process = Process.Start(info) ?? throw new GitException("não foi possível iniciar o git");

		if (input != null)
		{
			process.StandardInput.Write(input);
			process.StandardInput.Close();
		}

		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		process.WaitForExit();
		if (process.ExitCode != 0) throw new GitException($"git {arguments[0]} falhou em {root}: {error.Trim()}");

		return output;
	}
}
