namespace DeclIndex.UnitTests;

[TestClass]
public class DeclarationParserTests
{
	private static IReadOnlyList<Declaration> Parse(string fonte, params string[] símbolos) => DeclarationParser.Parse(fonte, símbolos).Declarations;

	[TestMethod]
	public void Classe_com_método_gera_linha_de_tipo_e_de_membro_com_container()
	{
		var linhas = Parse("namespace A.B { public class C { public static void Save(int x) { } } }");

		linhas.Should().ContainSingle(l => l.Kind == "class" && l.Name == "C" && l.Container == "A.B" && l.Modifiers == "public");
		linhas.Should().ContainSingle(l => l.Kind == "method" && l.Name == "Save" && l.Container == "A.B.C" && l.Signature == "void (int)" && l.Line == 1);
	}

	[TestMethod]
	public void Linha_é_a_do_identificador_e_não_a_do_atributo_que_o_precede()
	{
		var linhas = Parse("[DBTable(TableName = \"X\",\n\tGroup = \"g\")]\npublic class C\n{\n\t[DBField]\n\tpublic int Id { get; set; }\n}");

		linhas.Single(l => l.Kind == "class").Line.Should().Be(3);
		linhas.Single(l => l.Kind == "property").Line.Should().Be(6);
	}

	[TestMethod]
	public void Namespace_com_escopo_de_arquivo_é_reconhecido()
		=> Parse("namespace A;\nclass C { }").Should().ContainSingle(l => l.Kind == "class" && l.Container == "A");

	[TestMethod]
	public void Tipo_aninhado_entra_no_container_do_membro()
		=> Parse("class Outer { class Inner { int Campo; } }").Should().ContainSingle(l => l.Kind == "field" && l.Container == "Outer.Inner");

	[TestMethod]
	public void Tipo_genérico_leva_aridade_no_nome()
		=> Parse("class Repo<T, K> { }").Should().ContainSingle(l => l.Name == "Repo`2");

	[TestMethod]
	public void Bases_e_atributos_são_listados_separados_por_vírgula()
	{
		var linha = Parse("[Serializable, Obsolete(\"x\")] class C : Base, IFoo<int> { }").Single(l => l.Kind == "class");

		linha.Attributes.Should().Be("Serializable,Obsolete");
		linha.Bases.Should().Be("Base,IFoo<int>");
	}

	[TestMethod]
	public void Enum_gera_uma_linha_por_membro()
		=> Parse("enum Cor { Azul, Verde }").Where(l => l.Kind == "enummember").Select(l => l.Name).Should().Equal("Azul", "Verde");

	[TestMethod]
	public void Membros_de_interface_explícita_levam_o_nome_da_interface()
		=> Parse("class C : IDisposable { void IDisposable.Dispose() { } }").Should().ContainSingle(l => l.Kind == "method" && l.Name == "IDisposable.Dispose");

	[TestMethod]
	public void Código_dentro_de_if_só_é_indexado_com_o_símbolo_definido()
	{
		var fonte = "#if PAF\nclass Paf { }\n#endif";

		Parse(fonte).Should().BeEmpty();
		Parse(fonte, "PAF").Should().ContainSingle(l => l.Name == "Paf");
	}

	[TestMethod]
	public void Fonte_com_erro_de_sintaxe_reporta_o_primeiro_erro_e_mantém_o_que_recuperou()
	{
		var resultado = DeclarationParser.Parse("class C { void M( { } }", []);

		resultado.FirstError.Should().NotBeNull();
		resultado.Declarations.Should().Contain(l => l.Name == "C");
	}

	[TestMethod]
	public void Campos_com_tab_ou_quebra_na_assinatura_não_quebram_o_TSV()
	{
		var linha = Parse("class C { void M(Dictionary<string,\n\tint> d) { } }").Single(l => l.Kind == "method");

		linha.ToTsv().Split('\t').Should().HaveCount(8);
	}
}
