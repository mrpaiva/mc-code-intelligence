namespace DeclIndex.UnitTests;

[TestClass]
public class DeclarationTests
{
	[TestMethod]
	public void Declaração_vai_e_volta_do_TSV_sem_perder_coluna()
	{
		var original = new Declaration("method", "Save", "A.B", "public static", "Obsolete", "", "void (DataFactory,int)", 271);

		var lida = Declaration.FromTsv(original.ToTsv());

		lida.Should().Be(original);
		original.ToTsv().Split('\t').Should().HaveCount(8);
	}
}
