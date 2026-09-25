using System.Text.Json;
using DeclIndex.Hook;

namespace DeclIndex.UnitTests;

/// <summary>Port dos 49 casos do test_check_code_intelligence.py: payload do Claude Code → decisão.</summary>
[TestClass]
public class HookTests
{
	/// <summary>cwd padrão: uma árvore com .cs, como uma sessão dentro do repositório Code.</summary>
	private static string raiz = "";
	private static HookEnvironment ambiente = null!;
	private const string Scripts = @"C:\plugin\skills\codebase-analyzer\scripts";
	private const string CwdPadrão = "<raiz>";

	[ClassInitialize]
	public static void Preparar(TestContext _)
	{
		raiz = Directory.CreateTempSubdirectory("hook-code-").FullName;
		Directory.CreateDirectory(Path.Combine(raiz, "Applications", "X", "Sources"));
		Directory.CreateDirectory(Path.Combine(raiz, "Components"));
		File.WriteAllText(Path.Combine(raiz, "Applications", "X", "Sources", "Foo.cs"), "class Foo {}\n");
		// O hook é registrado no nível de usuário e só age sob estas raízes; nos testes, a pasta temporária inteira.
		ambiente = new HookEnvironment(Scripts, [Path.GetTempPath()]);
	}

	[ClassCleanup]
	public static void Limpar() => Directory.Delete(raiz, true);

	private static HookDecision Executar(string ferramenta, Dictionary<string, object?> entrada, string? cwd = CwdPadrão, HookEnvironment? env = null)
	{
		var payload = new Dictionary<string, object?> { ["tool_name"] = ferramenta, ["tool_input"] = entrada };
		if (cwd != null) payload["cwd"] = cwd == CwdPadrão ? raiz : cwd;
		var request = HookRequest.Parse(JsonSerializer.Serialize(payload))!;
		return CodeIntelligenceHook.Decide(request, env ?? ambiente);
	}

	private static Dictionary<string, object?> Grep(string pattern, string? type = null, string? glob = null, string? path = null)
	{
		var entrada = new Dictionary<string, object?> { ["pattern"] = pattern };
		if (type != null) entrada["type"] = type;
		if (glob != null) entrada["glob"] = glob;
		if (path != null) entrada["path"] = path;
		return entrada;
	}

	private static Dictionary<string, object?> Shell(string command) => new() { ["command"] = command };

	/// <summary>Forma como o hook cita um script: <c>&amp; "&lt;scripts&gt;\nome.ps1" argumentos</c>.</summary>
	private static string Comando(string script, string argumentos) => $"{script}\" {argumentos}";

	// ---------- Escopo: registrado no nível de usuário, o hook só age quando o cwd da sessão está sob uma das raízes ----------

	[TestMethod]
	public void Sessão_fora_das_raízes_não_é_tocada_mesmo_com_declaração_em_cs()
	{
		var fora = Path.Combine(Path.GetDirectoryName(Path.GetTempPath().TrimEnd('\\'))!, "outro-projeto");

		Executar("Grep", Grep("class MemberController", type: "cs"), cwd: fora).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Sessão_fora_das_raízes_não_é_tocada_no_Bash()
	{
		var fora = Path.Combine(Path.GetDirectoryName(Path.GetTempPath().TrimEnd('\\'))!, "outro-projeto");

		Executar("Bash", Shell("grep -rn \"class MemberController\" src/"), cwd: fora).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Sessão_sem_cwd_no_payload_é_tratada_como_dentro()
	{
		var semRaízes = new HookEnvironment(Scripts, []);

		Executar("Grep", Grep("class MemberController", type: "cs"), cwd: null, env: semRaízes).Denied.Should().BeTrue();
	}

	[TestMethod]
	public void Sessão_dentro_de_um_checkout_do_Code_está_no_escopo_sem_raiz_configurada()
	{
		var semRaízes = new HookEnvironment(Scripts, []);

		Executar("Grep", Grep("class MemberController", type: "cs"), cwd: Path.Combine(raiz, "Applications", "X"), env: semRaízes).Denied.Should().BeTrue();
	}

	// ---------- Ferramenta Grep ----------

	[TestMethod]
	public void Grep_de_declaração_em_alvo_cs_é_negado_com_o_comando_do_índice()
	{
		var decisão = Executar("Grep", Grep("class MemberController", type: "cs"));

		decisão.Denied.Should().BeTrue();
		decisão.Reason.Should().Contain(Comando("find_declarations.ps1", "-Name MemberController -Kind class"));
	}

	[TestMethod]
	public void Grep_de_identificador_puro_em_cs_é_negado_apontando_os_dois_scripts()
	{
		var decisão = Executar("Grep", Grep("MemberController", glob: "*.cs"));

		decisão.Denied.Should().BeTrue();
		decisão.Reason.Should().Contain(Comando("find_declarations.ps1", "-Name MemberController"));
		decisão.Reason.Should().Contain(Comando("find_usages.ps1", "MemberController"));
	}

	[TestMethod]
	public void Grep_de_texto_livre_em_cs_é_negado_apontando_só_o_find_usages()
	{
		var decisão = Executar("Grep", Grep("Título não encontrado", type: "cs"));

		decisão.Denied.Should().BeTrue();
		decisão.Reason.Should().Contain(Comando("find_usages.ps1", "\"Título não encontrado\""));
		decisão.Reason.Should().NotContain("find_declarations");
	}

	[TestMethod]
	public void Grep_com_regex_real_é_permitido()
	{
		Executar("Grep", Grep("Member.*Save", type: "cs")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Grep_com_linhas_de_contexto_é_permitido()
	{
		var entrada = Grep("MemberController", glob: "*.cs");
		entrada["-C"] = 3;

		Executar("Grep", entrada).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Grep_sem_distinguir_maiúsculas_é_permitido()
	{
		var entrada = Grep("membercontroller", glob: "*.cs");
		entrada["-i"] = true;

		Executar("Grep", entrada).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Grep_multilinha_é_permitido()
	{
		var entrada = Grep("MemberController", type: "cs");
		entrada["multiline"] = true;

		Executar("Grep", entrada).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Grep_de_identificador_num_arquivo_único_é_permitido()
	{
		Executar("Grep", Grep("MemberController", path: "Applications/X/Foo.cs")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Grep_de_declaração_num_arquivo_único_é_permitido()
	{
		Executar("Grep", Grep("class Foo", path: "Applications/X/Foo.cs")).Denied.Should().BeFalse();
		Executar("Grep", Grep(": ServiceBase", path: "Applications/X/Foo.cs")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Grep_de_identificador_em_glob_de_pasta_não_é_arquivo_único_e_é_negado()
	{
		Executar("Grep", Grep("MemberController", path: "Applications/X/*.cs")).Denied.Should().BeTrue();
	}

	[TestMethod]
	public void Grep_de_identificador_em_alvo_não_cs_é_permitido()
	{
		Executar("Grep", Grep("MemberController", glob: "*.ts")).Denied.Should().BeFalse();
	}

	// ---------- Bash: grep/rg/git grep/find seguem a mesma regra da ferramenta Grep/Glob ----------

	[TestMethod]
	public void Grep_recursivo_de_declaração_de_classe_é_negado_com_o_comando_do_índice()
	{
		var decisão = Executar("Bash", Shell("grep -rn \"class MemberController\" Applications/"));

		decisão.Denied.Should().BeTrue();
		decisão.Reason.Should().Contain(Comando("find_declarations.ps1", "-Name MemberController -Kind class"));
	}

	[TestMethod]
	public void Rg_sem_caminho_varre_a_árvore_e_é_negado_para_declaração()
	{
		Executar("Bash", Shell("rg 'class MemberController'")).Denied.Should().BeTrue();
	}

	[TestMethod]
	public void Rg_com_tipo_cs_e_padrão_de_herança_é_negado_com_Base()
	{
		var decisão = Executar("Bash", Shell("rg -t cs \": ServiceBase\""));

		decisão.Denied.Should().BeTrue();
		decisão.Reason.Should().Contain("-Base ServiceBase");
	}

	[TestMethod]
	public void Git_grep_de_declaração_é_negado()
	{
		var decisão = Executar("Bash", Shell("git grep -n \"interface IMemberService\""));

		decisão.Denied.Should().BeTrue();
		decisão.Reason.Should().Contain("-Name IMemberService -Kind interface");
	}

	[TestMethod]
	public void Grep_de_identificador_puro_em_cs_no_Bash_é_negado_apontando_os_dois_scripts()
	{
		var decisão = Executar("Bash", Shell("grep -rn \"MemberController\" --include=*.cs ."));

		decisão.Denied.Should().BeTrue();
		decisão.Reason.Should().Contain(Comando("find_declarations.ps1", "-Name MemberController"));
		decisão.Reason.Should().Contain(Comando("find_usages.ps1", "MemberController"));
	}

	[TestMethod]
	public void Rg_sem_distinguir_maiúsculas_é_permitido()
	{
		Executar("Bash", Shell("rg -i \"membercontroller\"")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Grep_com_flag_combinada_que_inclui_i_é_permitido()
	{
		Executar("Bash", Shell("grep -rni \"membercontroller\" Applications/")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Grep_com_linhas_de_contexto_no_Bash_é_permitido()
	{
		Executar("Bash", Shell("grep -rn -C 3 \"MemberController\" src/")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Rg_com_regex_real_é_permitido()
	{
		Executar("Bash", Shell("rg 'Member\\w+Controller' -t cs")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Grep_de_identificador_em_arquivo_cs_único_é_permitido()
	{
		Executar("Bash", Shell("grep -n \"MemberController\" Applications/X/Foo.cs")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Grep_de_declaração_em_arquivo_cs_único_é_permitido()
	{
		Executar("Bash", Shell("grep -n \"class Foo\" Applications/X/Foo.cs")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Grep_de_identificador_em_glob_de_pasta_cs_não_é_arquivo_único_e_é_negado()
	{
		Executar("Bash", Shell("grep -rn \"MemberController\" Applications/X/Sources/*.cs")).Denied.Should().BeTrue();
	}

	[TestMethod]
	public void Grep_com_filtro_de_extensão_não_cs_é_permitido()
	{
		Executar("Bash", Shell("grep -rn \"class Foo\" --include=*.md Documents/")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Grep_em_arquivo_único_não_cs_é_permitido()
	{
		Executar("Bash", Shell("grep -n \"class Foo\" README.md")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Grep_lendo_de_um_pipe_não_é_busca_na_árvore_e_é_permitido()
	{
		Executar("Bash", Shell("cat Foo.cs | grep \"class Foo\"")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Grep_precedido_de_rtk_é_tratado_como_grep()
	{
		Executar("Bash", Shell("rtk grep -rn \"class MemberController\" src/")).Denied.Should().BeTrue();
	}

	[TestMethod]
	public void Redirecionamento_e_pipe_posterior_não_escondem_o_grep()
	{
		Executar("Bash", Shell("grep -rn \"class MemberController\" . 2>/dev/null | head -5")).Denied.Should().BeTrue();
	}

	[TestMethod]
	public void Redirecionamento_colado_ao_separador_não_leva_o_comando_seguinte_para_dentro_do_grep()
	{
		// "2>/dev/null;" sem espaço: o alvo do redirect ia até o espaço, levava o ";" e "echo x" virava alvo do grep.
		Executar("Bash", Shell("grep -n \"class Foo\" README.md 2>/dev/null; echo x")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Find_por_nome_com_curinga_cs_é_negado_como_o_Glob()
	{
		var decisão = Executar("Bash", Shell("find . -name \"*Controller*.cs\""));

		decisão.Denied.Should().BeTrue();
		decisão.Reason.Should().Contain("find_usages.ps1");
	}

	[TestMethod]
	public void Find_por_nome_exato_é_permitido()
	{
		Executar("Bash", Shell("find . -name \"MemberController.cs\"")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Find_alimentando_xargs_grep_de_identificador_é_negado_pela_busca_e_não_pela_listagem()
	{
		var decisão = Executar("Bash", Shell("find Applications -name \"*.cs\" | xargs grep -l \"IMemberService\""));

		decisão.Denied.Should().BeTrue();
		decisão.Reason.Should().Contain(Comando("find_usages.ps1", "IMemberService"));
	}

	[TestMethod]
	public void Find_alimentando_xargs_grep_com_regex_é_permitido()
	{
		Executar("Bash", Shell("find Applications -name \"*.cs\" | xargs grep -l \"IMember\\w+Service\"")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Comando_sem_busca_é_permitido()
	{
		Executar("Bash", Shell("git status && dotnet build -m")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Comando_com_aspas_desbalanceadas_não_derruba_o_hook()
	{
		Executar("Bash", Shell("grep -rn \"class Foo")).Denied.Should().BeFalse();
	}

	// ---------- PowerShell: Select-String e Get-ChildItem seguem a mesma regra ----------

	[TestMethod]
	public void Gci_recursivo_em_cs_com_Select_String_de_declaração_é_negado()
	{
		var decisão = Executar("PowerShell", Shell("Get-ChildItem -Recurse -Filter *.cs | Select-String -Pattern \"class MemberController\""));

		decisão.Denied.Should().BeTrue();
		decisão.Reason.Should().Contain(Comando("find_declarations.ps1", "-Name MemberController -Kind class"));
	}

	[TestMethod]
	public void Select_String_com_Path_cs_e_declaração_é_negado()
	{
		Executar("PowerShell", Shell("Select-String -Path \"Applications\\*.cs\" -Pattern \"class Foo\"")).Denied.Should().BeTrue();
	}

	[TestMethod]
	public void Select_String_num_arquivo_cs_único_é_permitido_mesmo_com_cara_de_atributo()
	{
		// Padrão real de uma sessão: \[AsParameters\] era uso do atributo num parâmetro, não declaração de tipo.
		var comando = "Select-String -Path Applications\\X\\Sources\\Foo.cs -Pattern \"MapGet|ListProductsRoute|\\[AsParameters\\]|Query\"";

		Executar("PowerShell", Shell(comando)).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Select_String_de_identificador_puro_em_cs_é_negado()
	{
		var decisão = Executar("PowerShell", Shell("Get-ChildItem -Recurse -Filter *.cs | Select-String \"MemberController\""));

		decisão.Denied.Should().BeTrue();
		decisão.Reason.Should().Contain(Comando("find_usages.ps1", "MemberController"));
	}

	[TestMethod]
	public void Select_String_com_Context_é_permitido()
	{
		Executar("PowerShell", Shell("Get-ChildItem -Recurse -Filter *.cs | Select-String \"MemberController\" -Context 2")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Select_String_lendo_de_Get_Content_é_permitido()
	{
		Executar("PowerShell", Shell("Get-Content notas.md | Select-String \"class Foo\"")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Gci_recursivo_com_curinga_cs_sem_busca_é_negado_como_o_Glob()
	{
		Executar("PowerShell", Shell("Get-ChildItem -Recurse -Filter *.cs")).Denied.Should().BeTrue();
	}

	[TestMethod]
	public void Gci_recursivo_por_nome_exato_é_permitido()
	{
		Executar("PowerShell", Shell("Get-ChildItem -Recurse -Filter MemberController.cs")).Denied.Should().BeFalse();
	}

	// ---------- Variáveis da própria linha: F=caminho; grep ... $F é o idioma mais comum do agente ----------

	[TestMethod]
	public void Grep_de_declaração_em_arquivo_apontado_por_variável_da_própria_linha_é_permitido()
	{
		Executar("Bash", Shell("F=Applications/X/Sources/Foo.cs; wc -l $F; grep -n \"class Foo\" $F")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Grep_em_sql_apontado_por_variável_entre_aspas_não_é_alvo_cs_e_é_permitido()
	{
		Executar("Bash", Shell("F=\"Applications/X/Data/001. Create structure.sql\"; grep -n \"ADD CONSTRAINT FK_OrderItems_\" \"$F\"")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Grep_de_identificador_em_pasta_apontada_por_variável_da_própria_linha_é_negado()
	{
		Executar("Bash", Shell("D=Applications; grep -rn \"MemberController\" $D")).Denied.Should().BeTrue();
	}

	[TestMethod]
	public void Grep_em_variável_vinda_de_substituição_de_comando_não_tem_alvo_julgável_e_é_permitido()
	{
		Executar("Bash", Shell("F=$(git ls-files | head -1); grep -n \"class Foo\" $F")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Select_String_em_variável_vinda_de_pipeline_não_tem_alvo_julgável_e_é_permitido()
	{
		var comando = "$f = git ls-files Components | Select-Object -First 1; Select-String -Path $f -Pattern \"ApiKey\"";

		Executar("PowerShell", Shell(comando)).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Substituição_de_processo_no_argumento_do_grep_é_opaca_e_não_vira_padrão_nem_caminho()
	{
		// Caso real: -f consumia o token "<(" e o comando interno vazava como padrão 'git' e caminhos inexistentes.
		var comando = "git diff --name-only $(git merge-base HEAD origin/develop) origin/develop | grep -F -f <(git diff --name-only origin/develop...HEAD) || echo \"(nenhum)\"";

		Executar("Bash", Shell(comando)).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Substituição_de_comando_é_um_token_só_e_o_grep_de_fora_continua_julgado()
	{
		ShellCommandParser.Tokenize("F=$(git ls-files | head -1); grep -f <(cat x) -rn \"MemberController\" Applications/")
			.Should().ContainInOrder("F=$(...)", ";", "grep", "-f", "<(...)", "-rn", "MemberController", "Applications/");
		Executar("Bash", Shell("grep -f <(cat x) -rn \"MemberController\" Applications/")).Denied.Should().BeTrue();
	}

	// ---------- git grep num ref: árvore de outro commit, fora do alcance do find_usages ----------

	[TestMethod]
	public void Git_grep_com_revisão_antes_do_separador_é_permitido()
	{
		Executar("Bash", Shell("git grep -l \"OrderController\" origin/develop -- 'Applications/X/Tests'")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Git_grep_com_separador_mas_sem_revisão_continua_sendo_busca_na_worktree_e_é_negado()
	{
		Executar("Bash", Shell("git grep -n \"MemberController\" -- Applications/")).Denied.Should().BeTrue();
	}

	// ---------- Listagem sob caminho explícito: varrer uma pasta específica não é a varredura cross-project que a regra do Glob evita ----------

	[TestMethod]
	public void Find_de_cs_sob_pasta_específica_é_permitido()
	{
		Executar("Bash", Shell("find Applications/X/Tests -name '*.cs' -not -path '*/obj/*' | sort")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Find_de_cs_sob_pasta_apontada_por_variável_é_permitido()
	{
		Executar("Bash", Shell("T=Applications/X/Tests; ls $T; find $T/Foo.UnitTests -name '*.cs'")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Find_de_cs_na_raiz_ou_em_Applications_inteiro_continua_negado()
	{
		Executar("Bash", Shell("find . -name '*.cs'")).Denied.Should().BeTrue();
		Executar("Bash", Shell("find Applications -name '*.cs'")).Denied.Should().BeTrue();
	}

	[TestMethod]
	public void Deny_do_find_amplo_aponta_o_git_ls_files_para_listar_uma_pasta()
	{
		Executar("Bash", Shell("find . -name '*.cs'")).Reason.Should().Contain("git ls-files");
	}

	[TestMethod]
	public void Gci_recursivo_de_cs_sob_pasta_específica_é_permitido()
	{
		Executar("PowerShell", Shell("Get-ChildItem Applications/X/Sources -Recurse -Filter *.cs")).Denied.Should().BeFalse();
		Executar("PowerShell", Shell("Get-ChildItem -Path Applications/X/Sources -Recurse -Include *.cs")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Glob_amplo_de_cs_sem_caminho_é_negado()
	{
		Executar("Glob", new Dictionary<string, object?> { ["pattern"] = "**/*.cs" }).Denied.Should().BeTrue();
		Executar("Glob", new Dictionary<string, object?> { ["pattern"] = "Applications/**/*.cs" }).Denied.Should().BeTrue();
	}

	[TestMethod]
	public void Glob_amplo_negado_aponta_as_declarações_do_arquivo_para_a_estrutura_de_um_cs()
	{
		var decisão = Executar("Glob", new Dictionary<string, object?> { ["pattern"] = "**/*.cs" });

		decisão.Reason.Should().Contain("find_declarations.ps1\" -File <caminho/do/arquivo.cs>").And.NotContain("documentSymbol");
	}

	[TestMethod]
	public void Glob_de_cs_sob_pasta_específica_é_permitido()
	{
		Executar("Glob", new Dictionary<string, object?> { ["pattern"] = "**/*.cs", ["path"] = "Applications/X/Tests" }).Denied.Should().BeFalse();
		Executar("Glob", new Dictionary<string, object?> { ["pattern"] = "Applications/X/Tests/**/*.cs" }).Denied.Should().BeFalse();
	}

	// ---------- Diretório-alvo: sem filtro de extensão, só é C# se tiver .cs (varredura limitada) ----------

	private sealed class Árvore : IDisposable
	{
		public string Root { get; } = Directory.CreateTempSubdirectory("hook-tree-").FullName;
		public string Docs => Path.Combine(Root, "Documents");
		public string Code => Path.Combine(Root, "Code");

		public Árvore()
		{
			Directory.CreateDirectory(Path.Combine(Docs, "Sessions"));
			File.WriteAllText(Path.Combine(Docs, "Sessions", "notas.md"), "class Foo\n");
			Directory.CreateDirectory(Path.Combine(Code, "Applications", "X", "Sources"));
			File.WriteAllText(Path.Combine(Code, "Applications", "X", "Sources", "Foo.cs"), "class Foo {}\n");
		}

		public void Dispose() => Directory.Delete(Root, true);
	}

	[TestMethod]
	public void Grep_de_palavra_em_pasta_sem_cs_é_permitido()
	{
		using var árvore = new Árvore();

		Executar("Grep", Grep("TODO", path: árvore.Docs)).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Grep_de_declaração_em_pasta_sem_cs_é_permitido()
	{
		using var árvore = new Árvore();

		Executar("Grep", Grep("class Foo", path: árvore.Docs)).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Grep_de_palavra_em_pasta_com_cs_aninhado_é_negado()
	{
		using var árvore = new Árvore();

		Executar("Grep", Grep("TODO", path: árvore.Code)).Denied.Should().BeTrue();
	}

	[TestMethod]
	public void Grep_sem_caminho_usa_o_cwd_do_payload()
	{
		using var árvore = new Árvore();

		Executar("Grep", Grep("TODO"), cwd: árvore.Docs).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Grep_relativo_resolve_pelo_cwd_do_payload()
	{
		using var árvore = new Árvore();

		Executar("Bash", Shell("grep -rn \"TODO\" Documents/"), cwd: árvore.Root).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Cd_no_início_do_comando_muda_a_base_dos_caminhos()
	{
		using var árvore = new Árvore();

		Executar("Bash", Shell($"cd \"{árvore.Code}\" && grep -rn \"TODO\" ."), cwd: árvore.Docs).Denied.Should().BeTrue();
	}

	[TestMethod]
	public void Rg_depois_de_cd_com_e_comercial_não_é_stdin_e_é_negado()
	{
		using var árvore = new Árvore();

		Executar("Bash", Shell($"cd \"{árvore.Code}\" && rg \"MemberController\""), cwd: árvore.Docs).Denied.Should().BeTrue();
	}

	[TestMethod]
	public void Caminho_estilo_git_bash_é_convertido_para_Windows()
	{
		using var árvore = new Árvore();
		var drive = char.ToLowerInvariant(árvore.Docs[0]);
		var resto = árvore.Docs.Substring(2).Replace('\\', '/');

		Executar("Bash", Shell($"grep -rn \"TODO\" /{drive}{resto}")).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Pasta_com_ponto_no_nome_é_pasta_e_não_arquivo()
	{
		using var árvore = new Árvore();
		var projeto = Path.Combine(árvore.Code, "Applications", "MultiClubes.Controller");
		Directory.CreateDirectory(projeto);
		File.WriteAllText(Path.Combine(projeto, "Bar.cs"), "class Bar {}\n");

		Executar("Bash", Shell("grep -rn \"TODO\" Applications/MultiClubes.Controller"), cwd: árvore.Code).Denied.Should().BeTrue();
		Executar("Grep", Grep("TODO", path: projeto), cwd: árvore.Code).Denied.Should().BeTrue();
	}

	[TestMethod]
	public void Caminho_inexistente_é_tratado_como_código()
	{
		Executar("Bash", Shell("grep -rn \"TODO\" pasta/que/nao/existe/")).Denied.Should().BeTrue();
	}

	// ---------- Read ----------

	[TestMethod]
	public void Read_de_cs_grande_sem_limit_é_negado_apontando_summarize_e_as_declarações_do_arquivo()
	{
		var arquivo = Path.Combine(raiz, "Applications", "X", "Sources", "Grande.cs");
		File.WriteAllLines(arquivo, Enumerable.Repeat("// linha", 2000));

		var decisão = Executar("Read", new Dictionary<string, object?> { ["file_path"] = arquivo });

		decisão.Denied.Should().BeTrue();
		decisão.Reason.Should().Contain("summarize_file.ps1")
			.And.Contain("find_declarations.ps1\" -File \"Applications/X/Sources/Grande.cs\" -IncludeGenerated", "entre aspas, porque há pastas com espaço (Connected Services), e com gerado, porque .cs enorme costuma ser Reference.cs ou Designer.cs")
			.And.NotContain("documentSymbol", "o csharp-ls carrega uma solution só e não enxerga o resto do repositório");
	}

	[TestMethod]
	public void Read_de_cs_grande_com_limit_é_permitido()
	{
		var arquivo = Path.Combine(raiz, "Applications", "X", "Sources", "Grande2.cs");
		File.WriteAllLines(arquivo, Enumerable.Repeat("// linha", 2000));

		Executar("Read", new Dictionary<string, object?> { ["file_path"] = arquivo, ["limit"] = 50 }).Denied.Should().BeFalse();
	}

	[TestMethod]
	public void Read_de_cs_pequeno_é_permitido()
	{
		Executar("Read", new Dictionary<string, object?> { ["file_path"] = Path.Combine(raiz, "Applications", "X", "Sources", "Foo.cs") }).Denied.Should().BeFalse();
	}

	// ---------- Saída ----------

	[TestMethod]
	public void Permissão_sai_como_objeto_vazio_e_negação_no_formato_do_PreToolUse()
	{
		HookDecision.Allow.ToJson().Should().Be("{}");

		using var json = JsonDocument.Parse(HookDecision.Deny("motivo \"x\"").ToJson());
		var saída = json.RootElement.GetProperty("hookSpecificOutput");
		saída.GetProperty("hookEventName").GetString().Should().Be("PreToolUse");
		saída.GetProperty("permissionDecision").GetString().Should().Be("deny");
		saída.GetProperty("permissionDecisionReason").GetString().Should().Be("motivo \"x\"");
	}

	[TestMethod]
	public void Payload_ilegível_vira_permissão()
	{
		HookRequest.Parse("{ não é json").Should().BeNull();
	}
}
