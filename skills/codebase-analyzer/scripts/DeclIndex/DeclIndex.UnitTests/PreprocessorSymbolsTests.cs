namespace DeclIndex.UnitTests;

[TestClass]
public class PreprocessorSymbolsTests
{
	[TestMethod]
	public void Símbolos_de_if_e_elif_são_descobertos_sem_operadores()
	{
		var fonte = "#if PAF && !DEBUG\n#elif (TEF_HOMOLOG || SAFRA)\n#else\n#endif\n#if true\n";

		PreprocessorSymbols.Scan(fonte).Should().BeEquivalentTo(["PAF", "DEBUG", "TEF_HOMOLOG", "SAFRA"]);
	}

	[TestMethod]
	public void Fonte_sem_diretiva_não_gera_símbolo()
		=> PreprocessorSymbols.Scan("class A { }").Should().BeEmpty();
}
