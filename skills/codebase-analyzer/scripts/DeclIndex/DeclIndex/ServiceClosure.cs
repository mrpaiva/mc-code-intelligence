namespace DeclIndex;

/// <summary>Fatia do índice que uma análise de serviços WCF precisa: tipos (com quais têm membros carregados) e membros.</summary>
public sealed record ClosureSlice(IReadOnlyList<IndexRow> Types, IReadOnlyCollection<string> LoadedTypeKeys, IReadOnlyList<IndexRow> Members);

/// <summary>
/// Calcula o fecho dos tipos de serviço (o que o service_action_graph.ps1 precisa) sem passar
/// pelo PowerShell: sementes por escopo, símbolo ou arquivo; bases seguidas por nome simples
/// (todos os candidatos, para a análise ver a ambiguidade); contratos [ServiceContract]; e os
/// membros de tudo isso. Tipos que só dividem arquivo com os carregados entram sem membros,
/// para delimitar corpos de método.
/// </summary>
public sealed class ServiceClosure
{
	private const int MaxRounds = 6;

	private readonly List<IndexRow> types = [];
	private readonly Dictionary<string, List<IndexRow>> membersByType = new(StringComparer.Ordinal);
	private readonly Dictionary<string, List<IndexRow>> typesBySimpleName = new(StringComparer.Ordinal);
	private readonly Dictionary<string, IndexRow> typesByKey = new(StringComparer.Ordinal);

	public ServiceClosure(IEnumerable<IndexRow> rows)
	{
		foreach (var row in rows)
		{
			if (row.IsType)
			{
				types.Add(row);
				typesByKey.TryAdd(row.TypeKey, row);
				if (!typesBySimpleName.TryGetValue(row.SimpleName, out var sameName)) typesBySimpleName[row.SimpleName] = sameName = [];
				sameName.Add(row);
				continue;
			}

			if (!membersByType.TryGetValue(row.Container, out var members)) membersByType[row.Container] = members = [];
			members.Add(row);
		}
	}

	public ClosureSlice ForScope(string scopePrefix)
	{
		var normalized = scopePrefix.Replace('\\', '/').TrimEnd('/');
		if (normalized == ".") normalized = "";
		if (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];

		var prefix = normalized.Length == 0 ? "" : normalized + "/";
		var seeds = types.Where(type => type.File.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (IsServiceLike(type) || IsServiceContract(type)));

		return Build(seeds);
	}

	public ClosureSlice ForSymbol(string symbol)
	{
		var operation = symbol.EndsWith("Action", StringComparison.Ordinal) ? symbol[..^6] : symbol;
		var action = operation + "Action";
		var owners = membersByType
			.Where(pair => pair.Value.Any(member => member.Kind == "method" && (member.Name == operation || member.Name == action)))
			.Select(pair => pair.Key)
			.Where(typesByKey.ContainsKey)
			.Select(key => typesByKey[key]);

		return Build(owners);
	}

	public ClosureSlice ForFile(string relativeFile)
	{
		var file = relativeFile.Replace('\\', '/');
		var seeds = types.Where(type => string.Equals(type.File, file, StringComparison.OrdinalIgnoreCase));

		return Build(seeds);
	}

	private ClosureSlice Build(IEnumerable<IndexRow> seeds)
	{
		var loaded = new Dictionary<string, IndexRow>(StringComparer.Ordinal);
		var frontier = new List<IndexRow>();

		foreach (var seed in seeds)
		{
			if (loaded.TryAdd(seed.TypeKey, seed)) frontier.Add(seed);
		}

		#region Comments
		//Sementes têm as bases seguidas sempre; nas rodadas seguintes só quem parece serviço, como o script fazia.
		#endregion Comments
		for (var round = 0; round < MaxRounds && frontier.Count > 0; round++)
		{
			var next = new List<IndexRow>();

			foreach (var type in frontier)
			{
				if (round > 0 && !IsServiceLike(type)) continue;

				foreach (var baseName in type.BaseSimpleNames())
				{
					if (!typesBySimpleName.TryGetValue(baseName, out var candidates)) continue;

					foreach (var candidate in candidates)
					{
						if (loaded.TryAdd(candidate.TypeKey, candidate)) next.Add(candidate);
					}
				}
			}

			frontier = next;
		}

		var files = new HashSet<string>(loaded.Values.Select(type => type.File), StringComparer.OrdinalIgnoreCase);
		var sliceTypes = types.Where(type => files.Contains(type.File)).ToList();
		var members = loaded.Keys
			.Where(membersByType.ContainsKey)
			.SelectMany(key => membersByType[key])
			.OrderBy(member => member.File, StringComparer.Ordinal)
			.ThenBy(member => member.Line)
			.ToList();

		return new ClosureSlice(sliceTypes, loaded.Keys.ToList(), members);
	}

	/// <summary>Mesmo critério do script: deriva de *ServiceBase ou *ServiceCore, ou implementa uma interface [ServiceContract].</summary>
	private bool IsServiceLike(IndexRow type)
	{
		if (type.Kind != "class") return false;

		foreach (var baseName in type.BaseSimpleNames())
		{
			if (baseName.EndsWith("ServiceBase", StringComparison.Ordinal) || baseName.EndsWith("ServiceCore", StringComparison.Ordinal)) return true;

			if (baseName.Length > 1 && baseName[0] == 'I' && char.IsUpper(baseName[1]) && typesBySimpleName.TryGetValue(baseName, out var candidates) && candidates.Any(IsServiceContract)) return true;
		}

		return false;
	}

	private static bool IsServiceContract(IndexRow type) => type.Kind == "interface" && type.Attributes.Contains("ServiceContract", StringComparison.Ordinal);
}
