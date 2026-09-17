// Hook do Claude Code: lê o payload JSON do stdin e escreve a resposta no stdout. Sem argumentos; o evento
// vem no payload. SessionStart compila o DeclIndex se falta e injeta o roteamento (ver SessionStart.cs);
// PreToolUse decide (ver CodeIntelligenceHook.cs); PostToolUse de Edit/Write marca a worktree editada para o
// refresh do DeclIndex (ver EditMarker.cs). Código de saída sempre 0.

using DeclIndex.Hook;

// Nunca falha a ferramenta: payload ilegível ou exceção viram permissão (fail-open), como o hook Python.
var json = "{}";
try
{
	var request = HookRequest.Parse(Console.In.ReadToEnd());
	if (request?.HookEventName == "PostToolUse") EditMarker.Mark(request, EditMarker.DefaultStore());
	else if (request?.HookEventName == "SessionStart") json = SessionStart.Run(request, HookEnvironment.FromProcess());
	else if (request != null) json = CodeIntelligenceHook.Decide(request, HookEnvironment.FromProcess()).ToJson();
}
catch (Exception exception)
{
	Console.Error.WriteLine($"hook: {exception.Message}");
}

using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
output.Write(json);
return 0;
