// Hook PreToolUse do Claude Code: lê o payload JSON do stdin e escreve a decisão no stdout
// (ver CodeIntelligenceHook.cs). Sem argumentos. Código de saída sempre 0.

using DeclIndex.Hook;

// Nunca falha a ferramenta: payload ilegível ou exceção viram permissão (fail-open), como o hook Python.
var json = "{}";
try
{
	var request = HookRequest.Parse(Console.In.ReadToEnd());
	if (request != null) json = CodeIntelligenceHook.Decide(request, HookEnvironment.FromProcess()).ToJson();
}
catch (Exception exception)
{
	Console.Error.WriteLine($"hook: {exception.Message}");
}

using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
output.Write(json);
return 0;
