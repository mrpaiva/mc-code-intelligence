# Deep Reading Protocols

Carregado quando vai ler método >50 linhas ou viu try/catch
em operação com efeito colateral (pagamento, checkout, integração,
persistência).

## S2 — Enumerate-first

Pergunta pede "todos/completo/full":
1. Grep para enumerar TODAS as ocorrências
2. Conte quantas são
3. Leia cada uma — não pare na primeira

## S3 — Read-depth

Método >100 linhas → leia em seções, não pare na primeira
if/else ou switch → siga TODOS os branches
Delegação para método em outra classe → siga a cadeia

Complemento — quando NÃO ler em profundidade:
- "Que classes tem?" → Glob + nomes. Não leia cada classe.
- "É chamado?" → find_usages. Não leia o corpo primeiro.
- "Quantos?" → Grep count. Não leia cada resultado.

## S9 — Error paths

Em operações com efeito colateral (pagamento, checkout,
integração externa, persistência):
1. Identifique try/catch blocks
2. Leia o catch COMPLETO — não pule
3. "O catch faz mais do que logar?"
   Sim → é business logic: siga a cadeia
   - Chama outro método? → siga
   - Muda estado? → documente
   - Lógica condicional por tipo de exceção? → mapeie branches

## S14 — Teste de vivacidade

Antes de basear conclusão em um método:
1. Código comentado? → pule
2. [Obsolete]? → note como obsoleto
3. Verifique callers (find_usages):
   - Zero callers → provavelmente morto, não baseie conclusão
   - Tem callers → vivo
4. EXCEÇÕES (entry points sem callers no código):
   Controller actions, Main(), event handlers por binding,
   [TestMethod], [ServiceContract]/[OperationContract]
5. Incerto? → "método existe mas não confirmei que é chamado"
