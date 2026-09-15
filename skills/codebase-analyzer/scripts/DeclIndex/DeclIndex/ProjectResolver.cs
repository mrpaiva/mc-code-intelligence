using System.Collections.Concurrent;

namespace DeclIndex;

/// <summary>Resolve o csproj mais próximo subindo a árvore a partir do arquivo, com cache por diretório.</summary>
public sealed class ProjectResolver(string root)
{
	// ConcurrentDictionary: Materialize chama ProjectOf de dentro de um Parallel.ForEach.
	private readonly ConcurrentDictionary<string, string> cache = new(StringComparer.OrdinalIgnoreCase);

	public string ProjectOf(string relativePath)
	{
		var directory = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? "";

		return Resolve(directory);
	}

	private string Resolve(string directory)
	{
		if (cache.TryGetValue(directory, out var cached)) return cached;

		var absolute = Path.Combine(root, directory);
		var csproj = Directory.Exists(absolute) ? Directory.EnumerateFiles(absolute, "*.csproj").FirstOrDefault() : null;
		var parent = Path.GetDirectoryName(directory)?.Replace('\\', '/') ?? "";
		var project = csproj != null ? Path.GetFileNameWithoutExtension(csproj) : directory.Length == 0 ? "" : Resolve(parent);

		cache[directory] = project;

		return project;
	}
}
