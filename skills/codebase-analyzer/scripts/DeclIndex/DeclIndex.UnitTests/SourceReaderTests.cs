namespace DeclIndex.UnitTests;

[TestClass]
public class SourceReaderTests
{
	[TestMethod]
	public void Bytes_UTF8_válidos_são_lidos_como_UTF8()
		=> SourceReader.Decode(Encoding.UTF8.GetBytes("Senha_é_válida")).Should().Be("Senha_é_válida");

	[TestMethod]
	public void Bytes_UTF8_com_BOM_perdem_o_BOM()
		=> SourceReader.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("x")]).Should().Be("x");

	[TestMethod]
	public void Bytes_cp1252_sem_BOM_caem_para_cp1252_e_preservam_acentos()
	{
		var cp1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

		SourceReader.Decode(cp1252.GetBytes("Senha_é_válida")).Should().Be("Senha_é_válida");
	}

	[TestMethod]
	public void Bytes_UTF16_com_BOM_são_lidos_como_UTF16()
		=> SourceReader.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("é")]).Should().Be("é");

	[TestMethod]
	public void Arquivo_com_NUL_é_binário_e_não_vira_texto_salvo_UTF16_com_BOM()
	{
		var pasta = Directory.CreateTempSubdirectory("declindex-texto-").FullName;

		try
		{
			File.WriteAllBytes(Path.Combine(pasta, "bin.png"), [(byte)'P', 0, (byte)'N', (byte)'G']);
			File.WriteAllBytes(Path.Combine(pasta, "utf16.txt"), [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("Total")]);
			File.WriteAllBytes(Path.Combine(pasta, "grande.txt"), new byte[Corpus.MaxBytes + 1]);
			File.WriteAllText(Path.Combine(pasta, "texto.txt"), "Total");

			SourceReader.ReadText(Path.Combine(pasta, "bin.png")).Should().BeNull();
			SourceReader.ReadText(Path.Combine(pasta, "utf16.txt")).Should().Be("Total");
			SourceReader.ReadText(Path.Combine(pasta, "grande.txt")).Should().BeNull();
			SourceReader.ReadText(Path.Combine(pasta, "texto.txt")).Should().Be("Total");
			SourceReader.ReadText(Path.Combine(pasta, "inexistente.txt")).Should().BeNull();
		}
		finally
		{
			Directory.Delete(pasta, true);
		}
	}
}
