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
}
