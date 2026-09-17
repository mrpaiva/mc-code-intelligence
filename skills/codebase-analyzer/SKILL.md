---
name: codebase-analyzer
description: "Use ao explorar código MultiClubes/MultiVendas antes de agir: localizar entry point, rastrear fluxo, mapear dependências antes de refatorar, achar onde regra está implementada. Sinais: \"onde fica X?\", \"o que Y chama?\", \"como Z funciona?\", \"qual classe responsável?\", \"quem declara X?\", \"quem herda de X?\", \"quais tipos têm o atributo X?\", \"que membros o tipo X tem?\", \"quantos enums/classes/interfaces em Y?\". Cirúrgico (um fluxo) — não confunda com domain-analysis (domínio inteiro). Use mesmo se o fluxo parecer familiar."
---

# CodebaseAnalyzer

Você explora o codebase MultiClubes/MultiVendas para responder perguntas —
localizar entry points, rastrear fluxos, mapear dependências. Investiga
antes de agir e registra descobertas no notebook.

## Regras

1. **Nunca assuma, nunca invente.** Não encontrou → diga "não encontrei".
   Incerteza é sempre explícita.
2. **Cite a fonte.** Toda afirmação sobre comportamento cita file:line.
   Sem referência concreta = claim incerto, declare como tal.
3. **Ponteiros, não cópias.** Referencie `Arquivo.cs:Método() (Lnn)`.
4. **Verifique na fonte.** Comportamento de APIs, versões, contratos —
   confirme no código antes de afirmar.
5. **Enumere antes de mergulhar.** Pergunta pede "todos/complete/onde"?
   Liste todas as ocorrências primeiro (Grep/find_usages), conte,
   depois leia cada uma. Nunca pare na primeira.

## Cautelas

- Arquivo >500 linhas → NUNCA Read inteiro. Use offset+limit.
- LSP: funciona dentro de 1 projeto. Cross-project: find_usages/Grep.
- Pergunta estrutural em C# (declara, herda, atributo, membros, contagem
  por kind) → find_declarations.ps1 ANTES de Grep. O índice se atualiza
  sozinho quando algo mudou (2 a 4 s; as demais chamadas reusam o TSV em
  0,3 s); não precisa reindexar nem de -NoRefresh. Editou .cs por sed ou
  script, fora de Edit/Write? A consulta seguinte pode reusar por até 60 s.
- Grep retornou >20 resultados? PARE. Regresse com files_with_matches,
  filtre por projeto, refine o pattern. Nunca generalize de 5 para 200.
- Mesmo símbolo em 2+ projetos? NÃO leia nenhum ainda. Liste quais
  projetos têm matches, determine qual é relevante, scope para esse.

## Ferramentas

Os scripts moram em `<Base directory>\scripts\` — o caminho absoluto está na linha
"Base directory for this skill" que abre esta skill. Chame sempre pelo caminho absoluto
(`& "<Base directory>\scripts\find_declarations.ps1" …`); não existe cópia em `~\.claude`
nem no PATH. A raiz do Code é inferida do diretório atual (ancestral com `Applications\`
e `Components\`); fora de um checkout, `MC_CODE_ROOT` ou `-Root`.

"Que API este arquivo expõe?"
  → LSP documentSymbol (C#) ou summarize_file.ps1 (outros)

"Quem DECLARA X?" (declaração, não uso)
  → & "<Base directory>\scripts\find_declarations.ps1" -Name X [-Kind method,class] [-Project P]
  → -Name é exato; com curinga lista por padrão: -Name *Facility* -Kind class
  → NÃO use Grep para declaração em C#: mistura chamadas com declarações
    e não enxerga código dentro de #if

"Quem herda de X ou implementa X?"
  → find_declarations.ps1 -Base X -Kind class

"Quais tipos têm o atributo [X]?"
  → find_declarations.ps1 -Attribute X (código gerado sai; -IncludeGenerated traz)

"Que membros o tipo X tem?" (partial em vários arquivos)
  → find_declarations.ps1 -Container X [-Project P] [-IncludeGenerated]
    (nome simples basta — match por sufixo; não chute o namespace, no MultiClubes
    ele raramente espelha a pasta. Homônimo em 2+ projetos → -Project)
  → LSP documentSymbol é por arquivo e perde as outras partes

"Este arquivo é de qual projeto? O que declara?"
  → find_declarations.ps1 -File <trecho do caminho>

"Quantos/quais enums, interfaces, classes... por projeto ou pasta?"
  → find_declarations.ps1 -Kind enummember -File Applications/X/ -GroupBy container
  → NÃO escreva parser em Python/PowerShell para contar declarações

"Tabela (tipo × base, tipo × atributo) ou encadear com outro comando?"
  → find_declarations.ps1 … -Raw | ForEach-Object { $_ -split "`t" }
    (colunas: kind, name, container, modifiers, attributes, bases, signature,
    line, file, project; várias bases/atributos separados por vírgula)
  → a saída formatada já mostra "[Attr] Nome : Base1, Base2"; -Raw é para
    montar tabela, filtrar por coluna ou contar de outro jeito

"Onde X é usado no monorepo?"
  → find_usages.ps1 <símbolo> (cross-project, agrupado por arquivo)
  → Grep se precisar regex ou contexto (-A/-B/-C)
  → find_declarations NÃO responde uso: índice é de declaração

"O que este método faz?"
  → Read com offset+limit nas linhas específicas

"Quais arquivos contêm X?"
  → Grep output_mode=files_with_matches

"Que classes este projeto tem?"
  → Glob + nomes de arquivo. NÃO leia cada classe.

"Este método é chamado?"
  → find_usages primeiro. NÃO leia o método antes de saber se é vivo.

"Como mudou? Quando introduziram?"
  → git log --follow <file>, git blame

## Artefatos não-código

Respostas podem viver fora de .cs:
- Android: strings.xml, AndroidManifest.xml, build.gradle
- Web: .cshtml, .tsx, appsettings.json
- SQL: stored procedures, scripts de migração
- Config: .csproj (referências), launchSettings.json

## Quando carregar protocolos

Grep retornou matches em 2+ paths de projeto diferentes?
  → leia references/cross-project.md

Vai ler método >50 linhas? Viu try/catch em operação com efeito colateral?
  → leia references/deep-reading.md

Já leu 3+ arquivos nesta exploração?
  → leia references/investigation.md

Mesmo nome definido em 2+ projetos, LSP lento ou incompleto, ou dúvida
sobre qual ferramenta responde a pergunta?
  → leia references/code-intelligence.md (hierarquia completa, gate de
    múltiplas definições, latência do LSP, fluxos por tipo de tarefa)

## Notebook

Organizado em `<notebookPath>/[Projeto]/INDEX.md` + `[tópico].md`.
Path em `.codebase-debrief.json` na raiz do projeto.

- **Crie nota quando:** fluxo de 3+ arquivos, comportamento não-óbvio,
  padrão recorrente, integração não-trivial
- **Não crie quando:** óbvio pelo nome, já está em Analysis.md
- **Ao final de exploração longa:** releia INDEX.md, atualize com conexões
  entre notas existentes e novas descobertas
