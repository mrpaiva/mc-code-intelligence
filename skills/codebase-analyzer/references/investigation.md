# Investigation Protocols

Carregado quando já leu 3+ arquivos nesta exploração.

## S11 — Grep overwhelm

Grep retornou >20 resultados:
1. PARE — não leia resultados ainda
2. Regresse com output_mode=files_with_matches
3. Filtre pelo projeto relevante (path=<projeto>)
4. Ainda >20? Refine pattern (classe + método, namespace)
5. Cross-project? Use find_usages.ps1 (agrupa por arquivo)

Nunca generalize de 5 resultados para 200.

## S13 — Scratchpad (checkpoint/rewind)

Exploração de 3+ arquivos:

1. Crie <notebookPath>/<Projeto>/_scratch.md:
   # Scratch — <pergunta>

2. A cada 3 arquivos lidos, APPEND:
   ## Checkpoint N
   - [achado]: <fato> — <File.cs:Method() (Lnn)>
   - [conexão]: <A relaciona com B porque ...>
   - [pendente]: <o que falta investigar>

3. Antes da resposta final: RELEIA _scratch.md
   - Cada claim deve ter correspondência no scratch
   - Claim sem checkpoint? Verifique se está inventando

4. Promova descobertas duráveis para notebook

5. DELETE _scratch.md — scratch é intermediário, nunca artefato final.
   O que vale já foi promovido para o notebook no passo 4.
