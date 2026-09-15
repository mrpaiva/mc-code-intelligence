# mc-code-intelligence

Plugin do Claude Code para navegar o repositório `Code` do MultiClubes/MultiVendas sem varrer 22 mil
arquivos `.cs` com `grep`. Traz três peças:

- **DeclIndex** — índice sintático de declarações C# (Roslyn, sem MSBuild), compartilhado entre worktrees e
  atualizado por SHA de blob a cada consulta. Responde "quem declara X", "quem herda de X", "quais tipos têm
  `[X]`", "que membros X tem", contagens por projeto ou pasta.
- **Scripts** — `find_declarations.ps1` (consulta o índice), `find_usages.ps1` (referências, agrupadas por
  arquivo, em todo arquivo de texto) e `summarize_file.ps1` (head + tail de um arquivo, com busca por nome).
- **Roteamento** — no início de cada sessão aberta dentro de um checkout do `Code`, o plugin injeta a tabela
  "pergunta → ferramenta" no contexto do agente. Fora de um checkout, o plugin fica invisível.

A skill `codebase-analyzer` acompanha o plugin e dispara nas perguntas de exploração de código.

## Pré-requisitos

- Windows
- [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows) (`pwsh`)
- [SDK .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) — o DeclIndex é compilado na sua máquina
- [ripgrep](https://github.com/BurntSushi/ripgrep) no `PATH`: `winget install BurntSushi.ripgrep.MSVC`
- Claude Code

## Instalação

No Claude Code:

```
/plugin marketplace add mrpaiva/mc-code-intelligence
/plugin install mc-code-intelligence@mc-tools
```

Abra uma sessão dentro do checkout do `Code` (qualquer subpasta serve). Na primeira sessão de cada versão
do plugin, o DeclIndex é compilado (10 a 20 s, mais o restore de pacotes na primeira vez). A primeira
consulta ao `find_declarations` materializa o índice do checkout (40 a 70 s, uma vez por máquina); as
seguintes levam 2 a 4 s, quase tudo `git status`.

Para conferir que o roteamento chegou: pergunte ao agente "quem herda de `ServiceBase` em
`Applications\MultiVendas`?" — a resposta deve vir do `find_declarations.ps1`, com arquivo e linha, sem
`Grep` cru.

## Onde as coisas ficam

| O quê | Onde | Muda com |
|---|---|---|
| Plugin (scripts, skill, DeclIndex) | `~\.claude\plugins\cache\mc-tools\mc-code-intelligence\<versão>\` | atualização do plugin |
| Índice | `%LOCALAPPDATA%\mc-code-intelligence\index\` | `MC_CODEINDEX` |
| Raiz do checkout | ancestral do diretório atual com `Applications\` e `Components\` | `MC_CODE_ROOT` (sessão fora do checkout) ou `-Root` |
| Onde o roteamento é injetado | dentro de um checkout do `Code` | `MC_HOOK_ROOTS` (diretórios extras, separados por `;`) |

O índice fica fora da pasta do plugin de propósito: a atualização troca a pasta versionada e o índice
sobrevive.

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
& "$s\find_usages.ps1" DefaultConnectionString -Include *.config
& "$s\summarize_file.ps1" CouponLotRebusManager.cs
```

`Get-Help` de cada script traz os parâmetros; o `README.md` em `scripts\DeclIndex\` descreve o índice.

## Desenvolvimento

```powershell
git clone https://github.com/mrpaiva/mc-code-intelligence
dotnet test .\mc-code-intelligence\skills\codebase-analyzer\scripts\DeclIndex\DeclIndex.slnx
Invoke-Pester -Path .\mc-code-intelligence\skills\codebase-analyzer\scripts\find_declarations.Tests.ps1   # Pester 5+
```

Para usar o clone como plugin: `/plugin marketplace add <caminho do clone>` e o mesmo `install`. Os
testes Pester criam uma árvore sintética (`Applications\` + `Components\`, dois `.cs`) e um armazém
temporário — não precisam de um checkout do `Code`.

## Licença

MIT. O repositório contém apenas o ferramental: nenhum código do produto.
