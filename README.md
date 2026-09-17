# mc-code-intelligence

Plugin do Claude Code para navegar o repositório `Code` do MultiClubes/MultiVendas sem varrer 23 mil
arquivos `.cs` com `grep`: um índice de declarações, uma busca de usos compacta, a tabela de roteamento
injetada na sessão e um hook que nega a busca crua quando um script cobria.

## Por que existe

O `Code` tem 23 mil arquivos C# em dezenas de projetos. O namespace raramente espelha a pasta, o mesmo
nome de tipo aparece em quatro assemblies (`GameMatch` é entidade no desktop e DTO em Cloud, OnlineServices
e Profile), e parte do código vive dentro de `#if`. Um agente que explora isso com `grep` recebe centenas de
linhas misturando declaração com chamada, escolhe uma e lê o arquivo inteiro. Cada rodada dessas custa
segundos de busca e milhares de tokens de contexto, e a resposta muitas vezes ainda vem incompleta.

Três perguntas resumem o dia a dia de quem explora esse código com um agente:

1. **Estrutura:** quem declara X? Quem herda de X? Quais tipos têm `[X]`? Que membros X tem?
2. **Referência:** onde X é usado?
3. **Leitura:** o que este arquivo faz?

O `rg` responde bem a segunda e mal a primeira. `rg FacilityOccurrenceCalculator` devolve 60 linhas em 27
arquivos; a única subclasse está numa delas, e a tentativa de filtrar por `: FacilityOccurrenceCalculator`
devolve zero, porque a herança está escrita de outra forma. Foi assim que o achado mais valioso da avaliação
de 2026-09-14 (a regra de conflito de reserva mora em `MultiClubes.Reports.UI`, e o OnlineServices depende
desse assembly) saiu de **uma linha** do índice, e estaria enterrado nas 60 do `rg`.

O plugin ataca as três perguntas com uma ferramenta para cada: um índice de declarações para a estrutura,
uma busca compacta para a referência, e um preview para a leitura. Injeta no início da sessão a tabela
"pergunta → ferramenta", que é o que faz o agente usar a ferramenta certa sem ninguém pedir. E quando o
agente ainda assim tenta `grep -rn "class X"` numa pasta com C#, o hook `PreToolUse` nega a chamada e
devolve o comando certo: a medição de 2026-09-11 mostrou que só a tabela na skill não muda o comportamento,
porque a skill não é carregada para toda pergunta; o hook é o que garante.

## O que muda

Medido em 2026-09-15 no commit `da3bed4a34` de `develop` (23.074 `.cs`), num worktree dedicado. Linhas e
bytes são da saída que o agente lê; tempos são de uma máquina com Defender ativo no disco do checkout (os
absolutos variam com a máquina, as proporções não).

| Pergunta | Com `rg` | Com o plugin | O que muda |
|---|---|---|---|
| Quem herda de `FacilityOccurrenceCalculator`? | `rg -n '\bFacilityOccurrenceCalculator\b'`: 60 linhas, 11 KB, 27 arquivos, 3,1 s. `rg ': FacilityOccurrenceCalculator'`: 0 | `find_declarations -Base FacilityOccurrenceCalculator`: 1 declaração (0,3 KB), 1,4 s | A resposta vem numa linha; no `rg` está enterrada em 60 |
| Quem declara o método `Save`? | `rg -n '\bSave\s*\('`: 3.009 linhas, 404 KB, 1.067 arquivos (declaração e chamada misturadas). Regex de modificador + `Save(`: 353 linhas, 50 KB, incompleto e sem tipo | `-Name Save -Kind method`: 367 declarações com tipo, projeto e assinatura (47 KB), 0,8 s | 8,6× menos bytes que a busca crua, e completo, o que o regex caprichado não garante |
| Que membros o partial `FormFacilityRentStepOne` tem? | achar os 2 arquivos e ler 868 linhas (29 KB) | `-Container FormFacilityRentStepOne -IncludeGenerated`: 55 membros com linha e assinatura (7 KB), 1,2 s | 4× menos bytes, e a metade do `Designer.cs` vem junto (o LSP por arquivo perde) |
| Quem declara `GameMatch`? | `rg -n 'class GameMatch\b'`: 4 linhas. Funciona | `-Name GameMatch -Kind class`: as mesmas 4, agrupadas por projeto e namespace | Aqui o `rg` empata. O índice acrescenta o agrupamento (é o que dispara a checagem de homônimos) e enxerga código dentro de `#if` |
| Quantos métodos cada controller de Facilities tem? | sem equivalente | `-Kind method -File MultiClubes.Controller/Facilities/ -GroupBy container`: 28 controllers ranqueados, `FacilityRentController` com 62 no topo, 0,6 s | Pergunta que antes não se fazia |
| Quantas operações `[OperationContract]` cada serviço do OnlineServices expõe? | `rg` devolve a linha do atributo; o nome do método está na seguinte (`-A1`: 2.399 linhas, 254 KB) | `-Attribute OperationContract -File Applications/OnlineServices/ -GroupBy container`: 800 operações em 239 serviços, ranqueados (17 KB), 0,5 s | Idem |
| Onde `FacilityRentController` é usado? | `rg -n '\bFacilityRentController\b'`: 122 ocorrências, 22 KB, 4,8 s | `find_usages FacilityRentController`: as mesmas 122, agrupadas por arquivo (5,8 KB), 5,4 s | 3,7× menos bytes pelo mesmo resultado; meio segundo a mais |

Na avaliação de 2026-09-14 (exploração real do domínio Facilities, 12 perguntas), o índice respondeu 9
sozinho e nunca deu resposta errada; o controle por `grep` é que errou uma vez, ao excluir `*.Designer.cs`
e deixar passar um `.designer.cs` com "d" minúsculo. As 3 restantes eram de uso, e foram ao `find_usages`
por desenho.

## O custo

O índice não é grátis. Os números da mesma máquina:

| Momento | Custo |
|---|---|
| Primeira sessão em cada versão do plugin | compilar o DeclIndex: 10 a 20 s (mais o restore de pacotes na primeira vez) |
| Primeira consulta num checkout | materializar o índice: 40 a 70 s (159 s numa máquina sob carga); um worktree novo parte do índice de outro e leva ~17 s |
| Consulta que precisa de refresh (primeira em 60 s, ou depois de Edit/Write num `.cs`, ou index do git reescrito) | 2 a 4 s, quase tudo `git status` em 23 mil arquivos |
| Consulta com o TSV reusado (as demais) ou com `-NoRefresh` | 0,3 a 0,6 s |
| Hook `PreToolUse`, por chamada de `Glob`/`Grep`/`Read`/`Bash`/`PowerShell` | 0,19 s no `DeclIndex.Hook.exe` pela cadeia do `bash` que o Claude Code usa; dentro do `DeclIndex.exe`, com o Roslyn no `deps.json`, custava 0,38 s; o hook Python anterior, 0,59 s |
| Hook `PostToolUse`, por chamada de `Edit`/`Write`/`MultiEdit`/`NotebookEdit` | 0,2 s: só grava o marcador de edição da worktree |

O índice é sintático: sabe quem **declara**, não quem **usa**. Overload, herança virtual e dispatch por
reflexão não são resolvidos. Para uso, `find_usages`; para semântica dentro de um arquivo, o LSP.

## Como funciona

Três peças, uma para cada pergunta:

- **DeclIndex** (estrutura). Indexador sintático em C# (Roslyn, sem MSBuild). No refresh, `git ls-files`
  e `git status` dizem que arquivos mudaram; só os blobs novos são parseados, e o TSV do checkout é
  rematerializado. O refresh só acontece quando algo pode ter mudado: a cada 60 s, quando o hook
  `PostToolUse` registrou Edit/Write na worktree, ou quando o index do git foi reescrito (checkout, pull,
  stash, reset); fora disso a consulta reusa o TSV sem spawnar git. Edição por fora do agente (IDE, `sed`)
  entra quando a janela vence. O armazém é compartilhado entre worktrees: um worktree novo parte do índice de outro.
  `find_declarations.ps1` consulta o TSV com `rg`.
- **find_usages.ps1** (referência). `rg` com limite de palavra sobre todo arquivo de texto (`.cs`, `.config`,
  `.resx`, `.xaml`, `.sql`, `.md`), saída agrupada por arquivo. Não usa o índice: é o que torna seguro trocar
  um `grep` cru por ele, porque o que o `grep` acharia, ele acha.
- **summarize_file.ps1** (leitura). Head e tail de um arquivo, com busca por nome quando o caminho é
  desconhecido, para decidir se vale ler tudo.

E a cola, em duas partes. No início de cada sessão aberta dentro de um checkout do `Code`, o plugin injeta
no contexto do agente a tabela "pergunta → ferramenta" com os caminhos absolutos já resolvidos. E a cada
chamada de `Glob`, `Grep`, `Read`, `Bash` ou `PowerShell`, o hook `PreToolUse` (o `DeclIndex.Hook.exe`, um
executável só com as regras, sem Roslyn e sem Python) aplica as regras: padrão com cara de declaração C# em alvo C# é negado com o comando exato do
`find_declarations`; identificador puro em pasta com `.cs` é negado apontando os dois scripts; regex real,
contexto (`-A/-B/-C`), `-i`, multiline ou arquivo único passam; `grep`, `rg`, `git grep`, `findstr` e
`Select-String` dentro do shell seguem a mesma regra, e `find -name "*.cs"` segue a do `Glob`. Leitura
integral de `.cs` com 2000+ linhas (ou outro código com 500+) sem `limit` é negada apontando o
`summarize_file`. Fora de um checkout (ou de `MC_HOOK_ROOTS`), nada disso acontece: o plugin fica invisível.
A skill `codebase-analyzer` acompanha e dispara nas perguntas de exploração.

## Pré-requisitos

- Windows
- [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows) (`pwsh`)
- [SDK .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0): o DeclIndex é compilado na sua máquina
- [ripgrep](https://github.com/BurntSushi/ripgrep) no `PATH`: `winget install BurntSushi.ripgrep.MSVC`
- Claude Code

## Instalação

No Claude Code:

```
/plugin marketplace add mrpaiva/mc-code-intelligence
/plugin install mc-code-intelligence@mc-tools
```

Abra uma sessão dentro do checkout do `Code` (qualquer subpasta ou worktree serve). Para conferir que o
roteamento chegou, pergunte ao agente "quem herda de `ServiceBase` em `Applications\MultiVendas`?": a
resposta deve vir do `find_declarations.ps1`, com arquivo e linha, sem `Grep` cru.

## Onde as coisas ficam

| O quê | Onde | Muda com |
|---|---|---|
| Plugin (scripts, skill, DeclIndex) | `~\.claude\plugins\cache\mc-tools\mc-code-intelligence\<versão>\` | atualização do plugin |
| Índice | `%LOCALAPPDATA%\mc-code-intelligence\index\` | `MC_CODEINDEX` |
| Raiz do checkout | ancestral do diretório atual com `Applications\` e `Components\` | `MC_CODE_ROOT` (sessão fora do checkout) ou `-Root` |
| Onde o roteamento é injetado | dentro de um checkout do `Code` | `MC_HOOK_ROOTS` (diretórios extras, separados por `;`) |

O índice fica fora da pasta do plugin de propósito: a atualização troca a pasta versionada e o índice
sobrevive. Nenhum caminho de máquina está gravado no plugin; cada dev clona o `Code` onde quiser.

## Atualização

```
/plugin marketplace update mc-tools
```

A versão nova recompila o DeclIndex na sessão seguinte. O índice não é afetado.

## Desempenho

Se o checkout estiver num disco varrido pelo Defender (ou outro antivírus), o `rg` cai para poucos MB/s e
cada consulta paga isso. Quando a política da máquina permitir, adicione o diretório do checkout às
exclusões.

## Uso direto dos scripts

```powershell
$s = "$HOME\.claude\plugins\cache\mc-tools\mc-code-intelligence\<versão>\skills\codebase-analyzer\scripts"
& "$s\find_declarations.ps1" -Base ServiceBase -Kind class -File Applications/MultiVendas/
& "$s\find_declarations.ps1" -Container GuardService
& "$s\find_declarations.ps1" -Kind method -File MultiClubes.Controller/Facilities/ -GroupBy container
& "$s\find_usages.ps1" DefaultConnectionString -Include *.config
& "$s\summarize_file.ps1" CouponLotRebusManager.cs
```

`Get-Help` de cada script traz os parâmetros; o `README.md` em `scripts\DeclIndex\` descreve o índice
(formato do TSV, como o refresh decide o que fazer, limitações).

## Desenvolvimento

```powershell
git clone https://github.com/mrpaiva/mc-code-intelligence
dotnet test .\mc-code-intelligence\skills\codebase-analyzer\scripts\DeclIndex\DeclIndex.slnx
Invoke-Pester -Path .\mc-code-intelligence\skills\codebase-analyzer\scripts\find_declarations.Tests.ps1   # Pester 5+
```

Para usar o clone como plugin: `/plugin marketplace add <caminho do clone>` e o mesmo `install`. Os
testes Pester criam uma árvore sintética (`Applications\` + `Components\`, dois `.cs`) e um armazém
temporário; não precisam de um checkout do `Code`.

## Licença

MIT. O repositório contém apenas o ferramental: nenhum código do produto.
