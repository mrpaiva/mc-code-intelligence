using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DeclIndex.Hook;

/// <summary>Decisão do hook no formato que o Claude Code lê no stdout: <c>{}</c> permite; o objeto com deny bloqueia e explica.</summary>
public sealed record HookDecision(bool Denied, string Reason)
{
	public static readonly HookDecision Allow = new(false, "");

	public static HookDecision Deny(string reason) => new(true, reason);

	/// <summary>Escrito à mão com Utf8JsonWriter: o serializador por reflexão custa JIT que um processo de um disparo não amortiza.</summary>
	public string ToJson()
	{
		if (!Denied) return "{}";

		using var buffer = new MemoryStream();
		using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
		{
			writer.WriteStartObject();
			writer.WriteStartObject("hookSpecificOutput");
			writer.WriteString("hookEventName", "PreToolUse");
			writer.WriteString("permissionDecision", "deny");
			writer.WriteString("permissionDecisionReason", Reason);
			writer.WriteEndObject();
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(buffer.ToArray());
	}
}
