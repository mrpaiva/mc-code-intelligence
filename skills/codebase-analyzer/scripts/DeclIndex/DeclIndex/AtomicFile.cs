using System.Text;

namespace DeclIndex;

/// <summary>
/// Grava em .novo e substitui de uma vez, para que um leitor concorrente nunca veja arquivo pela metade.
/// Quebra de linha sempre "\n": o "$" do rg não casa antes de "\r", e o git prefere assim.
/// </summary>
public static class AtomicFile
{
	private static readonly Encoding Utf8SemBom = new UTF8Encoding(false);

	public static void WriteLines(string path, IEnumerable<string> lines)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var temporary = path + ".novo";

		using (var writer = new StreamWriter(temporary, false, Utf8SemBom) { NewLine = "\n" })
		{
			foreach (var line in lines) writer.WriteLine(line);
		}

		MoveWithRetry(temporary, path);
	}

	/// <summary>No Windows o arquivo recém-gravado pode estar preso por antivírus ou indexador por alguns milissegundos.</summary>
	private static void MoveWithRetry(string source, string destination)
	{
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				File.Move(source, destination, true);
				return;
			}
			catch (IOException) when (attempt < 5)
			{
				Thread.Sleep(20 * attempt);
			}
		}
	}
}
