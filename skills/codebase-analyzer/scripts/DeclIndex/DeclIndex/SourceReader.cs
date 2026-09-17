using System.Text;

namespace DeclIndex;

/// <summary>Decodifica fonte como o csc: BOM manda; sem BOM, UTF-8 estrito e, se inválido, cp1252.</summary>
public static class SourceReader
{
	private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
	private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

	public static string Read(string path) => Decode(File.ReadAllBytes(path));

	/// <summary>Texto para o corpus; null se passa de <see cref="Corpus.MaxBytes"/> ou é binário (NUL sem BOM UTF-16), como o rg pula.</summary>
	public static string? ReadText(string path)
	{
		var info = new FileInfo(path);
		if (!info.Exists || info.Length > Corpus.MaxBytes) return null;

		var bytes = File.ReadAllBytes(path);
		var utf16 = bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF));
		if (!utf16 && Array.IndexOf(bytes, (byte)0) >= 0) return null;

		return Decode(bytes);
	}

	public static string Decode(byte[] bytes)
	{
		if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
		if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
		if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

		try
		{
			return StrictUtf8.GetString(bytes);
		}
		catch (DecoderFallbackException)
		{
			return Windows1252.GetString(bytes);
		}
	}
}
