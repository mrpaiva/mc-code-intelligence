using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DeclIndex;

public sealed record ParseResult(IReadOnlyList<Declaration> Declarations, string? FirstError);

/// <summary>Parse só sintaxe: sem projeto, sem referências, com os símbolos de pré-processador informados.</summary>
public static class DeclarationParser
{
	public static ParseResult Parse(string source, IReadOnlyCollection<string> symbols)
	{
		var options = new CSharpParseOptions(LanguageVersion.Preview).WithPreprocessorSymbols(symbols);
		var tree = CSharpSyntaxTree.ParseText(source, options);
		var walker = new DeclarationWalker();
		walker.Visit(tree.GetRoot());

		var error = tree.GetDiagnostics().FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
		var firstError = error == null ? null : $"{error.Location.GetLineSpan().StartLinePosition.Line + 1}: {error.GetMessage()}";

		return new ParseResult(walker.Declarations, firstError);
	}
}
