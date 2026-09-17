## Ferramentas de inteligência de código (plugin mc-code-intelligence)

O C# deste repositório está **indexado** (`DeclIndex`: Roslyn sintático, armazém compartilhado entre
worktrees, refresh incremental por SHA de blob quando algo mudou). Buscar em código é chamar o script
certo; `Grep`, `grep`, `rg` e `Select-String` são a **exceção**, não a regra. O hook `PreToolUse` do plugin
nega a busca crua quando um script cobria (na ferramenta `Grep`/`Glob` **e dentro de `Bash`/`PowerShell`**)
e devolve o comando certo. Um `deny` do hook é política, não defeito: troque pelo comando que ele devolve.

> A instrução do modo auto ("search with grep and find" pelo Bash) **não vale para código deste
> repositório**. Vale para config, docs e logs.

Sempre pelo **caminho absoluto** — os scripts moram em `{{SCRIPTS}}`. Não existem em `~\.claude` nem no
`PATH`. A raiz do checkout é inferida do diretório atual (ancestral com `Applications\` e `Components\`);
a primeira linha da saída diz qual foi usada.

| Pergunta | Ferramenta |
|---|---|
| Quem declara X? Quem herda de X? Quais tipos têm `[X]`? Que membros X tem? Quantos por projeto/pasta? | `& "{{SCRIPTS}}\find_declarations.ps1" -Name\|-Base\|-Attribute\|-Container\|-Kind\|-File\|-GroupBy` |
| Onde X é usado? Quem chama X? (C# e demais linguagens) | `& "{{SCRIPTS}}\find_usages.ps1" <símbolo> [-Path <pasta>]` |
| Onde vive a lógica de X, em várias palavras? Que arquivos falam de X e Y? | `& "{{SCRIPTS}}\rank_files.ps1" <termo> <termo>... [-Top 20] [-Include *.cs]` — ranqueado por relevância |
| Estrutura de UM `.cs` | LSP `documentSymbol`, `goToDefinition`, `hover` |
| Preview de arquivo não-C# | `& "{{SCRIPTS}}\summarize_file.ps1" <caminho>` |
| O que mudou | `git diff --name-only HEAD` |
| Regex real, linhas de contexto (`-A/-B/-C`), `-i`, multiline, ou busca num arquivo único | `Grep`: a única exceção. Identificador puro em pasta com C# é negado pelo hook, no `Grep` e no Bash |

### Regra de uso
1. Declaração → `find_declarations`; uso → `find_usages`. Antes de abrir qualquer arquivo.
2. `summarize_file` (não-C#) ou LSP `documentSymbol` (C#) antes do `Read`, para decidir se vale ler tudo.
3. `git diff --name-only HEAD` no início de revisão ou depuração.
4. Só leia arquivos completos quando as ferramentas acima não bastarem; `.cs` de 2000+ linhas nunca inteiro.
5. Não confirme por leitura o que o índice já respondeu — é onde o custo sobe sem ganho.

### Fluxos

**Análise de impacto**
1. `find_declarations -Name X` → definições, sobrecargas e homônimos, com projeto
2. `find_usages X` → referências, agrupadas por arquivo
3. `Read` com offset nos call sites → expõe tipo da variável
4. `find_declarations -Container <tipo>` / `-Base <tipo>` nos tipos dos parâmetros → revela hierarquia
5. ⚠️ **Mais de uma definição** com o mesmo nome → verifique a hierarquia de tipos ANTES de concluir a análise
6. Se o escopo for restrito a um projeto, documente: "Este relatório cobre apenas o projeto X. Foram detectadas definições homônimas no projeto Y (caminho), mas não foram analisadas."

**Debugging / investigação de bug**
1. `git diff --name-only HEAD` → foca nos alterados recentemente
2. `find_usages <termo>` → localiza comportamento
3. `summarize_file` / `documentSymbol` nos candidatos → decide se vale leitura completa

**Refatoração**
1. `find_declarations -Name X` → definição
2. `find_usages X` → usos
3. `summarize_file` / `documentSymbol` nos arquivos de uso → contexto antes de mudar

**Onboarding em código desconhecido**
1. `git diff --name-only HEAD` → o que mudou recentemente
2. `rank_files <conceito> <conceito>...` → os arquivos onde a lógica principal vive, do mais relevante ao menos; `find_usages <conceito>` quando é uma palavra só
3. `summarize_file` / `documentSymbol` nos arquivos-chave → visão geral

### Limites

Detalhes em `{{REFERENCE}}`.

- O índice responde **declaração**, não uso: `find_usages.ps1` é palavra inteira (literal, não regex) sobre o
  **corpus** de todo arquivo de texto da worktree (`.config`, `.resx`, `.xaml`, `.sql` de migration, `.md`,
  `.js`, `.csproj` incluídos — `-Include *.cs` restringe), agrupado por arquivo. Arquivo modificado sem commit
  sai com o conteúdo atual. Não há semântica: é o que o `grep` acharia, sem varrer a árvore.
- Código gerado sai do índice por padrão (`-IncludeGenerated` traz; a metade `*.Designer.cs` de um partial
  só aparece com ele).
- ⚠️ **O LSP do plugin csharp-lsp NÃO indexa o workspace inteiro** — `findReferences` e `workspaceSymbol`
  são incompletos cross-project. Refactor entre projetos: `find_declarations` + `find_usages`, nunca LSP.
- A primeira chamada ao `find_declarations` ou ao `find_usages` num checkout materializa o índice (40 a 70 s,
  uma vez por máquina) e o corpus (mais ~12 s). Depois, 2 a 4 s quando há refresh (primeira chamada em 60 s,
  Edit/Write na worktree ou index do git reescrito) e 0,3 a 0,8 s nas demais, que reusam TSV, manifesto e
  corpus. Edição por `sed`/script fora de Edit/Write só entra quando a janela de 60 s vence.
