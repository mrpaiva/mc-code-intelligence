namespace DeclIndex;

/// <summary>Uma declaração extraída de um arquivo. Nenhuma coluna depende do caminho do arquivo.</summary>
public sealed record Declaration(string Kind, string Name, string Container, string Modifiers, string Attributes, string Bases, string Signature, int Line)
{
	public const string Header = "kind\tname\tcontainer\tmodifiers\tattributes\tbases\tsignature\tline";

	public string ToTsv() => string.Join('\t', Kind, Name, Container, Modifiers, Attributes, Bases, Signature, Line);

	public static Declaration FromTsv(string line)
	{
		var columns = line.Split('\t');

		return new Declaration(columns[0], columns[1], columns[2], columns[3], columns[4], columns[5], columns[6], int.Parse(columns[7]));
	}
}
