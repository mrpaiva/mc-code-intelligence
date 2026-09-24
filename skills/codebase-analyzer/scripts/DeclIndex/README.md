# DeclIndex: índice de declarações C# e corpus de texto

Indexador sintático (Roslyn, sem MSBuild) que alimenta o `find_declarations.ps1`, e corpus de texto por blob que
alimenta o `find_usages.ps1`.

## Build e testes

```powershell
dotnet build .\DeclIndex.slnx -c Release
dotnet test  .\DeclIndex.slnx
```

O `session-start.ps1` do plugin compila na primeira sessão de cada versão, e o `find_declarations.ps1` compila
sozinho na primeira chamada se o executável não existir. Os testes de
`RefreshTests` e `UsagesTests` criam um repositório git temporário e exigem `git` no PATH.

## Uso direto

```
DeclIndex refresh --worktree <raiz> [--store <dir>] [--quiet]   atualiza índice, manifesto e corpus; imprime o caminho do TSV
DeclIndex usages --worktree <raiz> --symbol <nome> [--scope <pasta>] [--include <glob>]... [--no-refresh] [--show-line]
                                                                 onde o símbolo aparece como palavra inteira (ver abaixo)
DeclIndex search --worktree <raiz> --term <palavra>... [--top 20] [--scope <pasta>] [--include <glob>]... [--no-refresh]
                                                                 arquivos ranqueados por BM25 sobre os termos (ver abaixo)
DeclIndex closure --worktree <raiz> (--scope <pasta> | --symbol <nome> | --file <arquivo>) [--no-refresh]
                                                                 fatia para análise de serviços WCF (ver abaixo)
DeclIndex prune [--store <dir>]                                  remove blobs e linhas do corpus sem referência em manifesto algum
```

Códigos de saída: 0 ok, 1 uso, 2 git ou armazém falhou. O relatório vai para stderr, com o tempo por fase
(git, leitura, parse, materialização, corpus) e uma linha `parse-error` por arquivo que o Roslyn não conseguiu ler.

### `usages`: o que o `find_usages.ps1` consome

Uma linha `caminho<TAB>linha` por acerto, em ordem de caminho e linha, para o símbolo como palavra inteira
(limite = não `[A-Za-z0-9_]` nem byte ≥ 0x80, então acento faz parte da palavra; literal, não regex). Varre o
corpus mapeado em memória em fatias paralelas e traduz os acertos para os caminhos da worktree pelo manifesto —
blob que está em dois caminhos sai nos dois; blob que saiu do manifesto (arquivo editado) não sai. `--scope`
restringe a uma pasta relativa, `--include` é glob como o `--glob` do rg (sem barra casa o nome do arquivo, com
barra o caminho inteiro), e as pastas `node_modules`, `bin`, `obj`, `dist`, `.git`, `packages`, `.vs`, `.vscode`
e `publish` ficam de fora, como no rg da árvore que o script fazia. No `Code`, `Save` (3 339 acertos em 1 134
arquivos) sai em ~0,4 s dentro do processo; o host .NET soma ~0,7 s a isso nesta máquina.

`--show-line` acrescenta uma terceira coluna, `caminho<TAB>linha<TAB>texto`: o texto da linha tirado do corpus
(nada é relido do disco), sem espaços nas pontas e cortado em 200 caracteres com `…`; pode conter TAB, então é
sempre a última coluna. O corte fica aqui e não no script: a linha de um `.js` minificado não atravessa o pipe, e o
PowerShell só repassa os bytes, que chegam intactos mesmo sob console CP 850 (o `pwsh` chamado pelo Bash).

### `search`: o que o `rank_files.ps1` consome

Uma linha `caminho<TAB>score<TAB>termos casados<TAB>linhas` por arquivo, do mais relevante ao menos, até `--top`.
Cada termo é varrido como no `usages` (palavra inteira, literal, sensível a caixa) e os arquivos da worktree que
passam no filtro são ranqueados por BM25 (k1 = 1,2, b = 0,75): tf = linhas do blob com o termo, df = blobs com o
termo, comprimento = linhas do arquivo (terceira coluna do `corpus.ids`), N e média sobre os blobs filtrados.
Sem índice invertido: k termos custam k varreduras (~0,25 s cada no `Code`), e o filtro sobre os 46 mil caminhos
custa ~0,1 s — `Voucher Cancel Reschedule` com `--include *.cs` fecha em 0,5 a 0,9 s dentro do processo.

### `closure`: a fatia que o `service_action_graph.ps1` consome

Calcula em C# o que o script fazia em PowerShell sobre 27 mil tipos: sementes (tipos de serviço da pasta,
quem declara `X`/`XAction`, ou os tipos de um arquivo), bases seguidas por nome simples com todos os
candidatos (para a análise enxergar ambiguidade), contratos `[ServiceContract]` e os membros disso tudo.
Sai em stdout no formato do TSV com uma coluna extra `loaded` (1 = tipo com membros na fatia; 0 = tipo que só
divide arquivo com um carregado, incluído para delimitar corpos de método). No `Code`, a fatia do MultiVendas
tem ~3 mil tipos (600 carregados) e sai em ~1,4 s; o script inteiro fecha em ~4 s com `-NoRefresh`.

## Armazém (`%LOCALAPPDATA%\mc-code-intelligence\index` ou `$env:MC_CODEINDEX`)

Fica fora da pasta do plugin de propósito: a atualização do plugin troca a pasta versionada, e o índice de um
`Code` inteiro custa 40 a 70 s para nascer.

```
meta.json                  versão do parser + união dos símbolos de #if encontrados no repo
blobs\<aa>\<sha>.tsv       declarações de um conteúdo (SHA de blob do git), sem coluna de caminho
corpus.txt                 uma linha "id:linha:texto" por linha não vazia de cada blob de texto já visto (append-only)
corpus.ids                 uma linha "sha<TAB>fim<TAB>linhas" por blob do corpus; o número da linha é o id
worktrees\<chave>.tsv      índice materializado da worktree, com file e project
worktrees\<chave>.manifest sha e caminho de cada arquivo da worktree (todos, não só .cs) no último refresh
worktrees\<chave>.stamp    carimbo do último refresh completo (instante, assinatura do index do git, arquivos)
```

O corpus é compartilhado entre worktrees e cresce só com blob novo, sob mutex entre processos; a consulta
filtra pelo manifesto da worktree. O prefixo é numérico de propósito: com o caminho na linha, `-w FormMemberEdit`
casava as 1 930 linhas do próprio `FormMemberEdit.cs` pelo nome do arquivo, e o arquivo ia de 114 MB a 472 MB.
Entram arquivos até 1 MB que não sejam binários (NUL sem BOM UTF-16), como o rg pula; o `Mono.Android.xml` de
49 MB fica fora. Blob rejeitado é registrado sem linhas para não ser relido a cada refresh. Cauda sem entrada no
`.ids` (gravação interrompida) fica fora da busca e é truncada no próximo acréscimo. No `Code`, 39 mil blobs de
46 mil arquivos dão 218 MB.

A chave da worktree é o caminho completo com `:`, `\` e `/` trocados por `_`. Quebra de linha é sempre `\n`.

## Como o refresh decide o que fazer

1. `git ls-files -s` dá caminho e SHA de todos os arquivos rastreados; `git status` aponta modificados, novos e
   apagados, e `git hash-object` recalcula o SHA dos modificados e novos.
2. Só blobs `.cs` ausentes do armazém são lidos e parseados (em paralelo). Os símbolos de `#if` deles entram na
   união; se a união cresceu, o armazém inteiro (blobs e TSVs de todas as worktrees) é invalidado e reparseado.
3. Se os `.cs` do manifesto não mudaram, o TSV não é reescrito. Se mudaram, a materialização parte de uma semente
   (o TSV anterior da própria worktree ou, para worktree nova, o TSV mais recente de outra) e copia as linhas
   dos arquivos cujo SHA não mudou; só os demais leem blob. O manifesto é regravado se qualquer arquivo mudou —
   editar um `.resx` troca o manifesto sem rematerializar o TSV.
4. Blobs ausentes do corpus entram em lotes de 2 048: o texto dos `.cs` recém-lidos é reaproveitado, o resto é
   lido do disco.

Antes de tudo isso, `Freshness` reusa o que existe sem spawnar git quando o carimbo tem menos de 60 s, o index do
git não foi reescrito e nenhum marcador `.dirty` (hook PostToolUse de Edit/Write) foi gravado.

Números de referência no `Code` (22 mil `.cs`, 3,5 M linhas; 46 mil arquivos de texto): primeira indexação 40 a
70 s, mais ~12 s para o corpus; sem mudança 2 a 3 s (quase tudo `git status`) ou 0,2 s no caminho rápido;
worktree nova a partir da semente 5 a 8 s. Do caminho frio, `git ls-files` custa ~0,5 s (0,27 s é o startup do
`git.exe` sob antivírus; o índice de 7,9 MB lê em 24 ms) e `git status` 0,9 a 2,9 s, que é o stat dos 46 mil
arquivos — `core.fsmonitor=true` + `core.untrackedCache=true` no repositório derrubariam isso para décimos, mas
é decisão do dono do checkout (sobe um daemon por repositório), não do plugin.

## Invalidar de propósito

Subir `BlobStore.ParserVersion` apaga o armazém na próxima execução. É o caminho para qualquer mudança de
formato do TSV ou de regra de extração.

## Limitações conhecidas

- Ramos `#else` e `#if !X` não são indexados (a união define todos os símbolos).
- `project` é o csproj mais próximo subindo a árvore; arquivo linkado por `Compile Include` de outra pasta pode
  ganhar o projeto errado.
- Pastas `worktrees\<chave>` de worktrees removidas não são podadas (a chave não é reversível para o caminho).
- Só C# no índice. Sem semântica: o índice responde declaração, e o corpus responde palavra inteira (literal),
  não referência resolvida.
- O corpus não é podado sozinho: cada edição salva acrescenta o blob novo, e o antigo fica até o `prune`.
