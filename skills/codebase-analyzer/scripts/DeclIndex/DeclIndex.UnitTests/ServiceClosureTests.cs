namespace DeclIndex.UnitTests;

[TestClass]
public class ServiceClosureTests
{
	// Um repositório em miniatura: serviço no escopo herdando de ServiceBase, que herda de
	// ServiceCore (fora do escopo); contrato [ServiceContract]; um homônimo de ServiceBase em
	// outro namespace; um tipo qualquer no mesmo arquivo do serviço; e um serviço fora do escopo.
	private static readonly string[] Tsv =
	[
		"kind\tname\tcontainer\tmodifiers\tattributes\tbases\tsignature\tline\tfile\tproject",
		"class\tService\tApp.Sales\tinternal\t\tServiceBase,IService\t\t5\tApplications/App/Sales/Service.cs\tApp",
		"method\tSave\tApp.Sales.Service\tpublic\t\t\tvoid (int)\t7\tApplications/App/Sales/Service.cs\tApp",
		"method\tSaveAction\tApp.Sales.Service\tprivate\t\t\tvoid (int)\t12\tApplications/App/Sales/Service.cs\tApp",
		"class\tHelper\tApp.Sales\tinternal\t\t\t\t30\tApplications/App/Sales/Service.cs\tApp",
		"method\tRun\tApp.Sales.Helper\tpublic\t\t\tvoid ()\t32\tApplications/App/Sales/Service.cs\tApp",
		"interface\tIService\tApp.Sales\tpublic\tServiceContract\t\t\t3\tApplications/App/Sales/IService.cs\tApp",
		"method\tSave\tApp.Sales.IService\t\tOperationContract\t\tvoid (int)\t5\tApplications/App/Sales/IService.cs\tApp",
		"class\tServiceBase\tApp.Services\tpublic abstract\t\tServiceCore\t\t4\tApplications/App/Services/ServiceBase.cs\tApp",
		"method\tAction\tApp.Services.ServiceBase\tprotected\t\t\tT (Func<T>)\t9\tApplications/App/Services/ServiceBase.cs\tApp",
		"class\tServiceCore\tMulti.ServiceModel\tpublic abstract\t\t\t\t6\tComponents/Multi/ServiceCore.cs\tMulti",
		"method\tGetActionMethod\tMulti.ServiceModel.ServiceCore\tprivate\t\t\tMethodInfo (string)\t20\tComponents/Multi/ServiceCore.cs\tMulti",
		"class\tServiceBase\tSystem.ServiceProcess\tpublic\t\t\t\t1\tComponents/Bcl/ServiceBase.cs\tBcl",
		"class\tOther\tApp.Other\tpublic\t\t\t\t1\tApplications/App/Other/Other.cs\tApp",
		"method\tDoIt\tApp.Other.Other\tpublic\t\t\tvoid ()\t3\tApplications/App/Other/Other.cs\tApp",
		"class\tService\tPos.Sales\tinternal\t\tServiceBase\t\t5\tApplications/Pos/Sales/Service.cs\tPos",
		"method\tLoad\tPos.Sales.Service\tpublic\t\t\tvoid ()\t7\tApplications/Pos/Sales/Service.cs\tPos",
	];

	private static ServiceClosure Closure() => new(Tsv.Skip(1).Select(IndexRow.Parse).Where(row => row != null)!);

	[TestMethod]
	public void Escopo_seleciona_o_serviço_da_pasta_e_segue_bases_e_contrato_para_fora_dela()
	{
		var slice = Closure().ForScope("Applications/App/Sales/");

		slice.LoadedTypeKeys.Should().BeEquivalentTo(["App.Sales.Service", "App.Sales.IService", "App.Services.ServiceBase", "System.ServiceProcess.ServiceBase", "Multi.ServiceModel.ServiceCore"]);
		slice.Members.Select(m => m.TypeKey).Should().NotContain("App.Sales.Helper", "membro de tipo que não é serviço não entra");
		slice.Members.Select(m => $"{m.Container}.{m.Name}").Should().Contain("App.Sales.Service.SaveAction").And.Contain("Multi.ServiceModel.ServiceCore.GetActionMethod");
	}

	[TestMethod]
	public void Tipos_do_mesmo_arquivo_entram_como_fronteira_sem_membros()
	{
		var slice = Closure().ForScope("Applications/App/Sales/");

		slice.Types.Select(t => t.TypeKey).Should().Contain("App.Sales.Helper");
		slice.LoadedTypeKeys.Should().NotContain("App.Sales.Helper");
		slice.Types.Select(t => t.TypeKey).Should().NotContain("App.Other.Other");
	}

	[TestMethod]
	public void Escopo_vazio_ou_raiz_cobre_o_repositório_inteiro()
	{
		Closure().ForScope("").LoadedTypeKeys.Should().Contain("App.Sales.Service").And.Contain("Pos.Sales.Service");
		Closure().ForScope("/").LoadedTypeKeys.Should().Contain("Pos.Sales.Service");
		Closure().ForScope(".").LoadedTypeKeys.Should().Contain("Pos.Sales.Service");
	}

	[TestMethod]
	public void Escopo_não_traz_serviço_de_outra_pasta_com_prefixo_parecido()
	{
		var slice = Closure().ForScope("Applications/App/");

		slice.LoadedTypeKeys.Should().NotContain("Pos.Sales.Service");
	}

	[TestMethod]
	public void Símbolo_seleciona_quem_declara_a_operação_ou_o_action_e_segue_as_bases()
	{
		var slice = Closure().ForSymbol("SaveAction");

		slice.LoadedTypeKeys.Should().Contain("App.Sales.Service").And.Contain("App.Services.ServiceBase").And.Contain("App.Sales.IService");
		slice.LoadedTypeKeys.Should().NotContain("Pos.Sales.Service");
	}

	[TestMethod]
	public void Arquivo_seleciona_os_tipos_declarados_nele()
	{
		var slice = Closure().ForFile("Applications/Pos/Sales/Service.cs");

		slice.LoadedTypeKeys.Should().Contain("Pos.Sales.Service").And.Contain("App.Services.ServiceBase");
		slice.Members.Select(m => m.Name).Should().Contain("Load");
	}
}
