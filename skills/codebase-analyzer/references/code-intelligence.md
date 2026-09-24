# Code intelligence — MultiClubes/MultiVendas

Atualizado em 2026-09-15. Define a hierarquia de ferramentas para navegar
código do monorepo. Os scripts citados moram em `<Base directory>\scripts\`
(a pasta desta skill); chame pelo caminho absoluto.

## Limitação crítica do plugin csharp-lsp v1.0.0

**O LSP não indexa o workspace inteiro.** Testes confirmaram:
- `findReferences` retorna apenas referências dentro do projeto atual
- `workspaceSymbol` ignora a query e retorna os primeiros 100 símbolos
- Refactors cross-project precisam de `find_usages.ps1` ou `Grep`, não LSP

**Reavaliar quando o plugin for atualizado.**

## Hierarquia por tipo de pergunta

### Buscar usos cross-project (a maior parte do trabalho)

Linguagem-agnóstica — vale para C#, SQL, JS/TS, .cshtml, .php:

- `& "<Base directory>\scripts\find_usages.ps1" <símbolo>` — referências
  word-boundary, saída compacta agrupada por arquivo. **~60% mais econômico
  em token** que `Grep --output_mode=content` em buscas com muitos matches.
  Dentro de um checkout consulta o corpus do DeclIndex (todo arquivo de texto da
  worktree, até 1 MB, compartilhado entre worktrees; ~0,6 s contra 5 a 9 s do rg na
  árvore); o símbolo é literal, não regex, e arquivo modificado sem commit sai com
  o conteúdo atual. `-Include *.cs` restringe; `-Path` limita a uma pasta; `-ShowLine`
  traz o texto de cada linha (sem indentação, até 200 caracteres).
- `& "<Base directory>\scripts\rank_files.ps1" <termo> <termo>...` — os arquivos
  da worktree ranqueados por relevância (BM25 sobre o mesmo corpus) para "onde
  vive a lógica de X" em várias palavras; `-Top`, `-Path` e `-Include` como acima.
- `Grep` nativo — quando precisar de regex, contexto (-A/-B/-C), ou
  `output_mode=files_with_matches` para listar só caminhos.

### Declarações C# (índice `find_declarations.ps1`, desde 2026-09-11)

Índice sintático de todo o monorepo (Roslyn, sem MSBuild), compartilhado entre worktrees em
`%LOCALAPPDATA%\mc-code-intelligence\index` (`MC_CODEINDEX` sobrescreve). Responde **declaração**, nunca uso:

| Pergunta | Comando |
|---|---|
| Quem declara `Save`? | `find_declarations.ps1 -Name Save [-Kind method] [-Project MultiVendas.Core]` |
| Quem herda de `ServiceBase`? | `find_declarations.ps1 -Base ServiceBase -Kind class` |
| Quais tipos têm `[MessageContract]`? | `find_declarations.ps1 -Attribute MessageContract [-IncludeGenerated]` |
| Membros do tipo parcial `GuardService`? | `find_declarations.ps1 -Container MultiVendas.Services.GuardService [-IncludeGenerated]` |
| Este arquivo é de qual projeto? | `find_declarations.ps1 -File Tef/Service.cs` |
| Enums/interfaces/classes por pasta, contagens | `find_declarations.ps1 -Kind enummember -File Applications/MultiVendas/ -GroupBy container` (também `file`, `project`, `kind`, `name`) |

Regras:

- `-File` é substring do caminho: `Applications/MultiVendas/` com a barra final, senão pega `MultiVendasPos` e
  `MultiVendasWeb` junto.
- Código gerado (`Reference.cs`, `*.Designer.cs`, `GeneratedCode`) sai por padrão. A metade `*.Designer.cs` de
  um tipo parcial WinForms só aparece com `-IncludeGenerated`.
- O índice se atualiza sozinho quando algo pode ter mudado — primeira chamada em 60 s, Edit/Write num `.cs`
  da worktree (marcador gravado pelo hook `PostToolUse`) ou index do git reescrito — e custa 2 a 4 s, quase
  tudo `git status`; as demais chamadas reusam o TSV em 0,3 s. Edição por `sed`/script fora de Edit/Write só
  entra quando a janela vence. `-NoRefresh` consulta o que já está materializado, sem checar nada. A raiz é o ancestral do cwd com `Applications\` e `Components\` (checkout ou
  worktree do `Code`); fora de um, `MC_CODE_ROOT`; sem os dois, erro. A primeira linha da saída diz qual usou;
  `-Root` prevalece. A primeira materialização de um checkout custa 40 a 70 s, uma vez por máquina.
- Ele enxerga código dentro de `#if PAF` e afins (parseia com a união dos símbolos do repo); `Grep` também,
  mas o LSP não.
- **Não** serve para "onde X é usado", "quem chama X": isso continua em `find_usages.ps1`. Não há semântica:
  overload, herança virtual e dispatch por reflexão (`*Action`) não são resolvidos.
- Saída `-Raw` são as linhas TSV: `kind name container modifiers attributes bases signature line file project`.

Medido no spike de 2026-09-11: "quem declara `Save`" caiu de 382 linhas (`Grep`, declarações e chamadas
misturadas) para 89 exatas; membros de tipo parcial, de "achar 2 arquivos e ler 145 linhas" para 1 consulta;
o join `*Action` sem fachada, de 20 s para 2,5 s.

### Entender UM arquivo C# aberto (LSP cobre bem)

| Operação | Quando usar |
|---|---|
| `documentSymbol` | "Que API o arquivo expõe?" — substitui `summarize_file.ps1` para C# |
| `goToDefinition` | Navegar do uso para a definição (dentro do mesmo projeto) |
| `hover` | Tipo/docs de um símbolo numa posição específica |
| `goToImplementation` | Implementações de interface dentro do projeto |
| `incomingCalls`/`outgoingCalls` | Cadeia de chamadas dentro do projeto |

### Preview de arquivo grande (qualquer linguagem)

- `& "<Base directory>\scripts\summarize_file.ps1" <nome-ou-caminho>` — head 40 +
  tail 20, com **fuzzy path** (acha pelo nome se você não souber o caminho
  completo).

### Git

- `git diff --name-only HEAD` — arquivos modificados desde HEAD
- `git log --oneline -<n>` — últimos N commits

## Gate de múltiplas definições (manual, sem LSP cross-project)

Se `find_usages.ps1` encontrar duas ou mais definições do mesmo símbolo
em projetos diferentes:

1. Leia a declaração de classe/interface do tipo sobre o qual o método opera
2. Verifique se há tipos homônimos em outros projetos do monorepo
3. Avalie se os homônimos têm relação semântica (herança, interface,
   conversão explícita) ou são domínios isolados
4. Documente o resultado no notebook ou na análise

Métodos de extensão em C# são resolvidos em build-time pelo tipo
declarado. Duas definições do mesmo método sobre tipos diferentes são
**independentes**, mas parecem iguais em grep ou find_usages.

## Latência do LSP

Primeira chamada na sessão pode levar **~10-30s** (load do workspace).
Após isso, fica em cache rápido.

**Heurística:**
- 3+ buscas estruturais em C# na mesma sessão → vale esperar o LSP carregar
- Busca pontual única → `documentSymbol` ainda economiza tokens vs leitura completa, vale também
- Qualquer busca cross-project → não tente o LSP, use `find_usages.ps1`/`Grep` direto

## Fluxos por tipo de tarefa

### Análise de impacto cross-project
1. `find_usages.ps1 <símbolo>` → mapeia onde está usado em todos os projetos
2. `LSP.documentSymbol` em cada arquivo de uso → contexto local sem ler tudo
3. `Read` com offset nos call sites que precisam análise profunda
4. Aplicar gate de múltiplas definições se aplicável

### Debugging
1. `git diff --name-only HEAD` → arquivos mudados na sessão atual
2. `find_usages.ps1` → localiza comportamento
3. `LSP.documentSymbol` (C#) ou `summarize_file.ps1` (outros) → preview

### Refatoração em C#
1. `find_usages.ps1 <símbolo>` → todos os usos (cross-project)
2. `find_declarations.ps1 -Name <símbolo>` → todas as declarações (sobrecargas, partials, homônimos em
   outros projetos), antes do `LSP.goToDefinition`, que só vê o projeto atual
3. `LSP.documentSymbol` nos arquivos de uso → contexto antes de mudar
4. Gate de múltiplas definições manual antes de mudar assinatura

### Onboarding em código desconhecido
1. `git log --oneline -20` ou `git diff --name-only HEAD~10` → o que mudou
2. `find_usages.ps1 <conceito>` ou `Grep "conceito"` → onde a lógica vive
3. `LSP.documentSymbol` ou `summarize_file.ps1` → visão geral dos arquivos-chave

## Setup técnico

- As operações LSP acima dependem do plugin `csharp-lsp@claude-plugins-official` (v1.0.0 na medição)
- Scripts do plugin `mc-code-intelligence`, em `<Base directory>\scripts\`: `find_declarations.ps1`
  (índice em `DeclIndex\`, README lá), `find_usages.ps1`, `summarize_file.ps1`
- Variáveis opcionais: `MC_CODE_ROOT` (raiz quando a sessão abre fora do checkout), `MC_CODEINDEX`
  (armazém do índice), `MC_HOOK_ROOTS` (diretórios extras, separados por `;`, onde o plugin injeta o
  roteamento)
