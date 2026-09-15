# Cross-Project Protocols

Carregado quando Grep retornou matches em 2+ paths de projeto diferentes.

## S1 — Tracing cross-project

Para traçar cadeia entre projetos:
1. Leia ProjectReferences no .csproj do projeto atual
2. Procure interfaces Refit (*.Contracts) ou [ServiceContract]
3. Quem tem a interface = client. Quem implementa = server.
4. Confirme via registro de DI qual implementação é injetada

## S7 — Triagem de homônimos

Grep retornou mesmo símbolo em 2+ projetos:
1. NÃO leia nenhum arquivo ainda
2. Liste projetos que contêm matches (pelo path)
3. Determine qual projeto é relevante para a pergunta
4. Scope greps seguintes para esse diretório
5. Cross-reference explícito: "agora olhando X em ProjectB"

## S8 — Resolução de DI

Encontrou dependência de interface (ISomething injetado):
1. NÃO leia implementações ainda
2. Ache o registro de DI:
   - .NET 8: grep "AddScoped|AddTransient|AddSingleton" + interface
   - Legado: grep "new ConcreteService(" ou factory patterns
3. Múltiplos registros? Verifique condições
4. Leia a implementação REGISTRADA, não a de melhor nome
5. Decorator/wrapper (A implementa I e recebe I no construtor)?
   Siga a cadeia — comportamento real está no mais interno

## S10 — Conexões mediadas por banco

Não encontrou chamada de código entre sistema A e sistema B,
MAS ambos operam sobre mesma entidade/tabela ([DBTable], SQL):

→ Registre no scratchpad (se ativo):
  [banco]: {A} e {B} acessam {Tabela} sem chamada direta

→ Na resposta final:
  VERIFICACAO HUMANA: {A} e {B} não têm chamada direta
  mas ambos acessam {Tabela}. Integração pode ser via banco.
  Confirme com o dev.

Ausência de chamada NÃO significa ausência de integração.
