using System.Text.Json;

namespace DeclIndex;

/// <summary>Armazém endereçado por conteúdo: um TSV por SHA de blob, mais meta.json com versão do parser e símbolos.</summary>
public sealed class BlobStore(string root)
{
	#region Comments
	//Subir a versão invalida o armazém inteiro. Histórico: 1 = inicial; 2 = quebra de linha "\n" nos TSVs.
	#endregion Comments
	public const int ParserVersion = 3;

	private sealed record Meta(int ParserVersion, string[] Symbols);

	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private readonly string metaPath = Path.Combine(root, "meta.json");
	private readonly HashSet<string> known = LoadKnown(Path.Combine(root, "blobs"));
	private Meta meta = LoadMeta(Path.Combine(root, "meta.json"));

	public string Root => root;

	public IReadOnlyCollection<string> Symbols => meta.Symbols;

	private string PathFor(string sha) => Path.Combine(root, "blobs", sha[..2], sha + ".tsv");

	public bool Contains(string sha) => known.Contains(sha);

	public void Write(string sha, IReadOnlyList<Declaration> declarations)
	{
		AtomicFile.WriteLines(PathFor(sha), declarations.Select(declaration => declaration.ToTsv()));

		lock (known) known.Add(sha);
	}

	public IEnumerable<Declaration> Read(string sha) => File.ReadLines(PathFor(sha)).Select(Declaration.FromTsv);

	/// <summary>Garante que os blobs foram parseados com a versão e os símbolos atuais; se não, apaga tudo. Devolve true se invalidou.</summary>
	public bool EnsureSymbols(IReadOnlyCollection<string> symbols)
	{
		var union = meta.Symbols.Union(symbols, StringComparer.Ordinal).OrderBy(symbol => symbol, StringComparer.Ordinal).ToArray();
		if (meta.ParserVersion == ParserVersion && union.SequenceEqual(meta.Symbols) && File.Exists(metaPath)) return false;

		var blobs = Path.Combine(root, "blobs");
		if (Directory.Exists(blobs)) Directory.Delete(blobs, true);

		#region Comments
		//Os TSVs materializados foram gerados com os símbolos antigos; manifesto "batendo" serviria índice velho.
		#endregion Comments
		var worktrees = Path.Combine(root, "worktrees");
		if (Directory.Exists(worktrees)) Directory.Delete(worktrees, true);

		known.Clear();
		meta = new Meta(ParserVersion, union);
		Directory.CreateDirectory(root);
		File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, JsonOptions));

		return true;
	}

	public void Prune(ISet<string> referenced)
	{
		var blobs = Path.Combine(root, "blobs");
		if (!Directory.Exists(blobs)) return;

		foreach (var file in Directory.EnumerateFiles(blobs, "*.tsv", SearchOption.AllDirectories))
		{
			var sha = Path.GetFileNameWithoutExtension(file);
			if (referenced.Contains(sha)) continue;

			File.Delete(file);
			known.Remove(sha);
		}
	}

	/// <summary>Uma enumeração da pasta custa menos que milhares de File.Exists.</summary>
	private static HashSet<string> LoadKnown(string blobs)
	{
		if (!Directory.Exists(blobs)) return new HashSet<string>(StringComparer.Ordinal);

		return Directory.EnumerateFiles(blobs, "*.tsv", SearchOption.AllDirectories)
			.Select(Path.GetFileNameWithoutExtension)
			.ToHashSet(StringComparer.Ordinal)!;
	}

	private static Meta LoadMeta(string path)
	{
		if (!File.Exists(path)) return new Meta(0, []);

		return JsonSerializer.Deserialize<Meta>(File.ReadAllText(path)) ?? new Meta(0, []);
	}
}
