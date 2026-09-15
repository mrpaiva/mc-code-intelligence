# DeclIndex: índice de declarações C#

Indexador sintático (Roslyn, sem MSBuild) que alimenta o `find_declarations.ps1`.

## Build e testes

```powershell
dotnet build .\DeclIndex.slnx -c Release
dotnet test  .\DeclIndex.slnx
```

O `session-start.ps1` do plugin compila na primeira sessão de cada versão, e o `find_declarations.ps1` compila
sozinho na primeira chamada se o executável não existir. Os testes de
`RefreshTests` criam um repositório git temporário e exigem `git` no PATH.

## Uso direto

```
DeclIndex refresh --worktree <raiz> [--store <dir>] [--quiet]   atualiza e imprime o caminho do TSV
DeclIndex closure --worktree <raiz> (--scope <pasta> | --symbol <nome> | --file <arquivo>) [--no-refresh]
                                                                 fatia para análise de serviços WCF (ver abaixo)
DeclIndex prune [--store <dir>]                                  remove blobs sem referência em manifesto algum
```

Códigos de saída: 0 ok, 1 uso, 2 git falhou. O relatório vai para stderr, com o tempo por fase
(git, leitura, parse, materialização) e uma linha `parse-error` por arquivo que o Roslyn não conseguiu ler.

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
worktrees\<chave>.tsv      índice materializado da worktree, com file e project
worktrees\<chave>.manifest sha e caminho de cada .cs usado na última materialização
```

A chave da worktree é o caminho completo com `:`, `\` e `/` trocados por `_`. Quebra de linha é sempre `\n`.

## Como o refresh decide o que fazer

1. `git ls-files -s` dá caminho e SHA dos `.cs` rastreados; `git status` aponta modificados, novos e apagados,
   e `git hash-object` recalcula o SHA dos modificados e novos.
2. Só blobs ausentes do armazém são lidos e parseados (em paralelo). Os símbolos de `#if` deles entram na união;
   se a união cresceu, o armazém inteiro (blobs e TSVs de todas as worktrees) é invalidado e reparseado.
3. Se o manifesto da worktree não mudou, nada é reescrito. Se mudou, a materialização parte de uma semente
   (o TSV anterior da própria worktree ou, para worktree nova, o TSV mais recente de outra) e copia as linhas
   dos arquivos cujo SHA não mudou; só os demais leem blob.

Números de referência no `Code` (22 mil `.cs`, 3,5 M linhas): primeira indexação 40 a 70 s; sem mudança 2 a 3 s
(quase tudo `git status`); worktree nova a partir da semente 5 a 8 s.

## Invalidar de propósito

Subir `BlobStore.ParserVersion` apaga o armazém na próxima execução. É o caminho para qualquer mudança de
formato do TSV ou de regra de extração.

## Limitações conhecidas

- Ramos `#else` e `#if !X` não são indexados (a união define todos os símbolos).
- `project` é o csproj mais próximo subindo a árvore; arquivo linkado por `Compile Include` de outra pasta pode
  ganhar o projeto errado.
- Pastas `worktrees\<chave>` de worktrees removidas não são podadas (a chave não é reversível para o caminho).
- Só C#. Sem semântica: declaração, não referência.
