using System.Text.Json;

namespace DeclIndex.Hook;

/// <summary>Payload que o Claude Code entrega ao hook pelo stdin: evento (PreToolUse/PostToolUse), ferramenta, entrada e cwd da sessão.</summary>
public sealed class HookRequest
{
	public string HookEventName { get; }
	public string ToolName { get; }
	public JsonElement ToolInput { get; }
	public string? Cwd { get; }

	private HookRequest(string hookEventName, string toolName, JsonElement toolInput, string? cwd)
	{
		HookEventName = hookEventName;
		ToolName = toolName;
		ToolInput = toolInput;
		Cwd = cwd;
	}

	/// <summary>Null quando o JSON não parseia: o chamador permite, como o hook Python fazia.</summary>
	public static HookRequest? Parse(string json)
	{
		try
		{
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			var hookEventName = root.TryGetProperty("hook_event_name", out var eventName) && eventName.ValueKind == JsonValueKind.String ? eventName.GetString()! : "";
			var toolName = root.TryGetProperty("tool_name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : "";
			var toolInput = root.TryGetProperty("tool_input", out var input) && input.ValueKind == JsonValueKind.Object ? input.Clone() : EmptyObject();
			var cwd = root.TryGetProperty("cwd", out var cwdElement) && cwdElement.ValueKind == JsonValueKind.String ? cwdElement.GetString() : null;
			return new HookRequest(hookEventName, toolName, toolInput, cwd);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	public string? GetString(string property)
	{
		return ToolInput.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
	}

	/// <summary>Verdadeiro no sentido do Python: presente, não nulo, não falso, não zero, não vazio.</summary>
	public bool IsTruthy(string property)
	{
		if (!ToolInput.TryGetProperty(property, out var value)) return false;

		return value.ValueKind switch
		{
			JsonValueKind.True => true,
			JsonValueKind.Number => value.GetDouble() != 0,
			JsonValueKind.String => value.GetString()!.Length > 0,
			JsonValueKind.Array or JsonValueKind.Object => true,
			_ => false,
		};
	}

	public bool IsPresent(string property)
	{
		return ToolInput.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null;
	}

	private static JsonElement EmptyObject()
	{
		using var document = JsonDocument.Parse("{}");
		return document.RootElement.Clone();
	}
}
