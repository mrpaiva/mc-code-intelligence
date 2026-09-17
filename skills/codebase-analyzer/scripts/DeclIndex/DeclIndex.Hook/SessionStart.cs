using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DeclIndex.Hook;

/// <summary>
/// SessionStart: dentro de um checkout do Code (ou de MC_HOOK_ROOTS), garante o DeclIndex.exe compilado — uma vez
/// por versão do plugin, porque a atualização troca a pasta versionada — e devolve o bloco de roteamento
/// (hooks\routing.md com os caminhos absolutos resolvidos) como additionalContext. Fora do escopo devolve nada:
/// o plugin é invisível nos outros projetos do dev. Port do session-start.ps1 (pwsh: 5 s p50, 12 s p90, e exposto
/// ao bloqueio de pwsh lançado por bash), que fica como bootstrap para quando este executável ainda não existe.
/// </summary>
public static class SessionStart
{
	/// <summary>Vazio fora do escopo; senão o JSON com additionalContext. <paramref name="build"/> devolve null no sucesso ou o motivo da falha.</summary>
	public static string Run(HookRequest request, HookEnvironment environment, Func<string, string?>? build = null)
	{
		var cwd = request.Cwd ?? Directory.GetCurrentDirectory();
		if (!CodeIntelligenceHook.SessionInScope(cwd, environment.HookRoots)) return "";

		var scripts = environment.ScriptsDirectory;
		var pluginRoot = Path.GetFullPath(Path.Combine(scripts, "..", "..", ".."));
		var notes = new List<string>();

		var exe = Path.Combine(scripts, "DeclIndex", "DeclIndex", "bin", "Release", "net10.0", "DeclIndex.exe");
		if (!File.Exists(exe))
		{
			var failure = (build ?? Build)(Path.Combine(scripts, "DeclIndex", "DeclIndex.slnx"));
			if (failure != null || !File.Exists(exe)) notes.Add($"> ⚠️ DeclIndex não compilou (`dotnet build` em `{Path.Combine(scripts, "DeclIndex")}`): {failure ?? "o executável não apareceu"}");
		}

		if (!OnPath("rg")) notes.Add("> ⚠️ `rg` (ripgrep) não está no PATH; `find_declarations` precisa dele (e `find_usages` fora de um checkout).");

		var block = File.ReadAllText(Path.Combine(pluginRoot, "hooks", "routing.md"), Encoding.UTF8)
			.Replace("{{SCRIPTS}}", scripts)
			.Replace("{{REFERENCE}}", Path.Combine(pluginRoot, "skills", "codebase-analyzer", "references", "code-intelligence.md"));
		if (notes.Count > 0) block = block.TrimEnd() + "\n\n" + string.Join("\n", notes) + "\n";

		return ToJson(block);
	}

	private static string? Build(string solution)
	{
		try
		{
			var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
			foreach (var argument in new[] { "build", solution, "-c", "Release", "--nologo", "-v", "q" }) info.ArgumentList.Add(argument);

			using var process = Process.Start(info)!;
			var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
			process.WaitForExit();
			if (process.ExitCode == 0) return null;

			var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			return string.Join(' ', lines.TakeLast(3));
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			return "`dotnet` (SDK .NET 10) não está no PATH.";
		}
	}

	private static bool OnPath(string executable)
	{
		var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

		return directories.Any(directory => File.Exists(Path.Combine(directory, executable)) || File.Exists(Path.Combine(directory, executable + ".exe")));
	}

	/// <summary>Escrito à mão com Utf8JsonWriter, como o HookDecision: o serializador por reflexão custa JIT que um processo de um disparo não amortiza.</summary>
	private static string ToJson(string block)
	{
		using var buffer = new MemoryStream();
		using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
		{
			writer.WriteStartObject();
			writer.WriteStartObject("hookSpecificOutput");
			writer.WriteString("hookEventName", "SessionStart");
			writer.WriteString("additionalContext", block);
			writer.WriteEndObject();
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(buffer.ToArray());
	}
}
