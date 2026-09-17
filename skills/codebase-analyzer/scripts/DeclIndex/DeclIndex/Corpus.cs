using System.IO.MemoryMappedFiles;
using System.Text;

namespace DeclIndex;

public sealed record CorpusHit(int Id, int Line);

/// <summary>
/// Texto de todos os blobs já vistos, compartilhado entre worktrees e endereçado por SHA: corpus.txt tem uma
/// linha "id:linha:texto" por linha não vazia de cada blob, e corpus.ids uma linha "sha\tfim\tlinhas" por blob,
/// cujo número de linha é o id. O prefixo é numérico de propósito — com o caminho na linha, "-w FormMemberEdit"
/// casava as 1 930 linhas do próprio FormMemberEdit.cs pelo nome do arquivo, e o arquivo quadruplicava.
/// Append-only sob mutex entre processos; quem consulta traduz id → sha → caminhos pelo manifesto da worktree.
/// Cauda sem entrada no .ids (gravação interrompida) fica fora da busca e é truncada no próximo acréscimo.
/// </summary>
public sealed class Corpus
{
	public const int MaxBytes = 1_048_576;

	private const string MutexName = "mc-code-intelligence-corpus";
	private static readonly Encoding Utf8SemBom = new UTF8Encoding(false);

	private readonly string textPath;
	private readonly string idsPath;
	private readonly List<string> shaById = [];
	private readonly List<int> linesById = [];
	private readonly Dictionary<string, int> idBySha = new(StringComparer.Ordinal);
	private long end;

	public Corpus(string storeRoot)
	{
		textPath = Path.Combine(storeRoot, "corpus.txt");
		idsPath = Path.Combine(storeRoot, "corpus.ids");
		Load();
	}

	public int Count => shaById.Count;

	public bool Contains(string sha) => idBySha.ContainsKey(sha);

	public string ShaOf(int id) => shaById[id - 1];

	/// <summary>Linhas do arquivo de origem (inclusive as vazias, que não estão no corpus); 0 para blob rejeitado.</summary>
	public int LinesOf(int id) => linesById[id - 1];

	public int IdOf(string sha) => idBySha[sha];

	/// <summary>Acrescenta os blobs ainda ausentes; texto nulo (binário, grande) entra sem linhas para não ser relido. Devolve quantos entraram.</summary>
	public int Append(IEnumerable<(string Sha, string? Text)> blobs)
	{
		using var mutex = new Mutex(false, MutexName);
		Acquire(mutex);

		try
		{
			#region Comments
			//Outro processo pode ter acrescentado desde a nossa carga: os ids são o número da linha, então recarrega.
			#endregion Comments
			Load();
			Reconcile();

			var appended = 0;
			using var text = new FileStream(textPath, FileMode.Append, FileAccess.Write, FileShare.Read);
			using var ids = new FileStream(idsPath, FileMode.Append, FileAccess.Write, FileShare.Read);
			using var writer = new StreamWriter(text, Utf8SemBom, 1 << 16) { NewLine = "\n" };
			using var register = new StreamWriter(ids, Utf8SemBom) { NewLine = "\n" };

			foreach (var (sha, content) in blobs)
			{
				if (idBySha.ContainsKey(sha)) continue;

				var id = shaById.Count + 1;
				var lines = WriteLines(writer, id, content);
				writer.Flush();
				end = text.Position;

				register.WriteLine($"{sha}\t{end}\t{lines}");
				register.Flush();

				shaById.Add(sha);
				linesById.Add(lines);
				idBySha[sha] = id;
				appended++;
			}

			return appended;
		}
		finally
		{
			mutex.ReleaseMutex();
		}
	}

	/// <summary>Linhas onde o símbolo aparece como palavra inteira, na ordem do corpus; uma por linha.</summary>
	public List<CorpusHit> Search(string symbol)
	{
		if (end == 0 || symbol.Length == 0) return [];

		using var file = new FileStream(textPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		if (file.Length < end) throw new IOException($"{textPath} tem {file.Length} bytes, menos que os {end} registrados em corpus.ids: apague os dois e rode refresh");

		using var map = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
		using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

		return CorpusScanner.Scan(view, end, Utf8SemBom.GetBytes(symbol));
	}

	/// <summary>Reescreve os dois arquivos só com os blobs referenciados, renumerando.</summary>
	public void Prune(ISet<string> referenced)
	{
		using var mutex = new Mutex(false, MutexName);
		Acquire(mutex);

		try
		{
			Load();
			Reconcile();
			if (shaById.Count == 0) return;

			#region Comments
			//Os ids crescem ao longo do corpus, então o id novo de cada blob mantido é a sua posição entre os mantidos.
			#endregion Comments
			var entries = File.ReadAllLines(idsPath).Select(line => line.Split('\t')).ToList();
			var newIdByOld = new int[entries.Count + 1];
			var kept = 0;

			for (var oldId = 1; oldId <= entries.Count; oldId++)
			{
				if (referenced.Contains(entries[oldId - 1][0])) newIdByOld[oldId] = ++kept;
			}

			var bytesByNewId = new long[kept + 1];

			using (var reader = new StreamReader(textPath, Utf8SemBom))
			using (var writer = new StreamWriter(textPath + ".novo", false, Utf8SemBom, 1 << 16) { NewLine = "\n" })
			{
				var remaining = end;

				while (remaining > 0 && reader.ReadLine() is { } line)
				{
					remaining -= Utf8SemBom.GetByteCount(line) + 1;
					var colon = line.IndexOf(':');
					var newId = newIdByOld[int.Parse(line.AsSpan(0, colon))];
					if (newId == 0) continue;

					var rewritten = string.Concat(newId.ToString(), line.AsSpan(colon));
					writer.WriteLine(rewritten);
					bytesByNewId[newId] += Utf8SemBom.GetByteCount(rewritten) + 1;
				}
			}

			var offset = 0L;
			var lines = new List<string>(kept);

			for (var oldId = 1; oldId <= entries.Count; oldId++)
			{
				var newId = newIdByOld[oldId];
				if (newId == 0) continue;

				offset += bytesByNewId[newId];
				lines.Add($"{entries[oldId - 1][0]}\t{offset}\t{entries[oldId - 1][2]}");
			}

			File.Move(textPath + ".novo", textPath, true);
			AtomicFile.WriteLines(idsPath, lines);
			Load();
		}
		finally
		{
			mutex.ReleaseMutex();
		}
	}

	private static int WriteLines(StreamWriter writer, int id, string? content)
	{
		if (content == null) return 0;

		var number = 0;
		var start = 0;

		while (start < content.Length)
		{
			var newline = content.IndexOf('\n', start);
			var stop = newline < 0 ? content.Length : newline;
			if (stop > start && content[stop - 1] == '\r') stop--;
			number++;

			if (stop > start)
			{
				writer.Write(id);
				writer.Write(':');
				writer.Write(number);
				writer.Write(':');
				writer.Write(content.AsSpan(start, stop - start));
				writer.Write('\n');
			}

			if (newline < 0) break;

			start = newline + 1;
		}

		return number;
	}

	private void Load()
	{
		shaById.Clear();
		linesById.Clear();
		idBySha.Clear();
		end = 0;
		if (!File.Exists(idsPath)) return;

		foreach (var line in File.ReadLines(idsPath))
		{
			var columns = line.Split('\t');
			if (columns.Length < 3) continue;

			shaById.Add(columns[0]);
			linesById.Add(int.Parse(columns[2]));
			idBySha[columns[0]] = shaById.Count;
			end = long.Parse(columns[1]);
		}
	}

	/// <summary>Sob o mutex: cauda além do registrado é gravação interrompida e sai; arquivo menor que o registrado é corrupção e zera tudo.</summary>
	private void Reconcile()
	{
		var length = File.Exists(textPath) ? new FileInfo(textPath).Length : 0;
		if (length == end) return;

		if (length > end)
		{
			using var stream = new FileStream(textPath, FileMode.Open, FileAccess.Write, FileShare.Read);
			stream.SetLength(end);
			return;
		}

		File.Delete(textPath);
		File.Delete(idsPath);
		Load();
	}

	private static void Acquire(Mutex mutex)
	{
		try
		{
			mutex.WaitOne();
		}
		catch (AbandonedMutexException)
		{
			#region Comments
			//O dono anterior morreu segurando o mutex; a cauda que ele deixou é reconciliada logo em seguida.
			#endregion Comments
		}
	}
}
