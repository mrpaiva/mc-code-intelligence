namespace DeclIndex;

/// <summary>Uma linha do TSV materializado de uma worktree (as oito colunas da declaração mais arquivo e projeto).</summary>
public sealed record IndexRow(string Kind, string Name, string Container, string Modifiers, string Attributes, string Bases, string Signature, int Line, string File, string Project)
{
	public static readonly HashSet<string> TypeKinds = new(StringComparer.Ordinal) { "class", "interface", "struct", "record" };

	/// <summary>Chave do tipo no índice: container mais nome com aridade. Para membros, o container já é a chave do dono.</summary>
	public string TypeKey => Container.Length == 0 ? Name : $"{Container}.{Name}";

	/// <summary>Nome simples, sem aridade (``Repository`1`` vira `Repository`).</summary>
	public string SimpleName
	{
		get
		{
			var tick = Name.IndexOf('`');
			return tick < 0 ? Name : Name[..tick];
		}
	}

	public bool IsType => TypeKinds.Contains(Kind);

	public string ToTsv() => string.Join('\t', Kind, Name, Container, Modifiers, Attributes, Bases, Signature, Line, File, Project);

	public static IndexRow? Parse(string line)
	{
		var columns = line.Split('\t');
		if (columns.Length < 10 || !int.TryParse(columns[7], out var lineNumber)) return null;

		return new IndexRow(columns[0], columns[1], columns[2], columns[3], columns[4], columns[5], columns[6], lineNumber, columns[8], columns[9]);
	}

	/// <summary>Nomes simples das bases: sem genéricos e sem namespace, respeitando vírgulas dentro de &lt;&gt;.</summary>
	public IEnumerable<string> BaseSimpleNames()
	{
		foreach (var part in SplitTopLevel(Bases))
		{
			var angle = part.IndexOf('<');
			var name = (angle < 0 ? part : part[..angle]).Trim();
			var dot = name.LastIndexOf('.');
			if (dot >= 0) name = name[(dot + 1)..];
			if (name.Length > 0) yield return name;
		}
	}

	private static IEnumerable<string> SplitTopLevel(string text)
	{
		if (string.IsNullOrEmpty(text)) yield break;

		var depth = 0;
		var start = 0;

		for (var index = 0; index < text.Length; index++)
		{
			var character = text[index];
			if (character is '<' or '(' or '[') depth++;
			else if (character is '>' or ')' or ']') depth--;
			else if (character == ',' && depth <= 0)
			{
				yield return text[start..index];
				start = index + 1;
			}
		}

		yield return text[start..];
	}
}
