using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DeclIndex;

/// <summary>Percorre a árvore e emite uma Declaration por tipo, membro e membro de enum.</summary>
public sealed class DeclarationWalker : CSharpSyntaxWalker
{
	private readonly Stack<string> containers = new();
	private readonly List<Declaration> declarations = [];

	public IReadOnlyList<Declaration> Declarations => declarations;

	private string Container => containers.Count == 0 ? "" : string.Join(".", containers.Reverse());

	// A linha é a do identificador, não a do início do nó: o nó começa nos atributos, e uma citação
	// file:line que cai em "[DBTable(" em vez de "public class X" não leva a lugar nenhum.
	private void Emit(string kind, string name, SyntaxTokenList modifiers, SyntaxList<AttributeListSyntax> attributes, string bases, string signature, SyntaxToken identifier)
	{
		var line = identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
		var modifierText = string.Join(" ", modifiers.Select(modifier => modifier.Text));
		var attributeText = string.Join(",", attributes.SelectMany(list => list.Attributes).Select(attribute => attribute.Name.ToString()));

		declarations.Add(new Declaration(kind, Clean(name), Container, modifierText, Clean(attributeText), Clean(bases), Clean(signature), line));
	}

	private static string Clean(string value) => value.Replace("\t", " ").Replace("\r", "").Replace("\n", " ");

	private static string TypeName(TypeDeclarationSyntax node)
		=> node.TypeParameterList == null ? node.Identifier.Text : $"{node.Identifier.Text}`{node.TypeParameterList.Parameters.Count}";

	private static string Bases(BaseListSyntax? baseList)
		=> baseList == null ? "" : string.Join(",", baseList.Types.Select(type => type.Type.ToString()));

	private static string Parameters(BaseParameterListSyntax list)
		=> string.Join(",", list.Parameters.Select(parameter => parameter.Type?.ToString() ?? "?"));

	private static string ExplicitPrefix(ExplicitInterfaceSpecifierSyntax? specifier) => specifier?.ToString() ?? "";

	public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
	{
		containers.Push(node.Name.ToString());
		base.VisitNamespaceDeclaration(node);
		containers.Pop();
	}

	public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
	{
		containers.Push(node.Name.ToString());
		base.VisitFileScopedNamespaceDeclaration(node);
		containers.Pop();
	}

	private void VisitType(TypeDeclarationSyntax node, string kind)
	{
		var name = TypeName(node);
		Emit(kind, name, node.Modifiers, node.AttributeLists, Bases(node.BaseList), "", node.Identifier);
		containers.Push(name);
		DefaultVisit(node);
		containers.Pop();
	}

	public override void VisitClassDeclaration(ClassDeclarationSyntax node) => VisitType(node, "class");

	public override void VisitStructDeclaration(StructDeclarationSyntax node) => VisitType(node, "struct");

	public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => VisitType(node, "interface");

	public override void VisitRecordDeclaration(RecordDeclarationSyntax node) => VisitType(node, "record");

	public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
	{
		Emit("enum", node.Identifier.Text, node.Modifiers, node.AttributeLists, Bases(node.BaseList), "", node.Identifier);
		containers.Push(node.Identifier.Text);

		foreach (var member in node.Members)
		{
			Emit("enummember", member.Identifier.Text, default, member.AttributeLists, "", "", member.Identifier);
		}

		containers.Pop();
	}

	public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
		=> Emit("delegate", node.Identifier.Text, node.Modifiers, node.AttributeLists, "", $"{node.ReturnType} ({Parameters(node.ParameterList)})", node.Identifier);

	public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
		=> Emit("method", ExplicitPrefix(node.ExplicitInterfaceSpecifier) + node.Identifier.Text, node.Modifiers, node.AttributeLists, "", $"{node.ReturnType} ({Parameters(node.ParameterList)})", node.Identifier);

	public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
		=> Emit("ctor", node.Identifier.Text, node.Modifiers, node.AttributeLists, "", $"({Parameters(node.ParameterList)})", node.Identifier);

	public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
		=> Emit("property", ExplicitPrefix(node.ExplicitInterfaceSpecifier) + node.Identifier.Text, node.Modifiers, node.AttributeLists, "", node.Type.ToString(), node.Identifier);

	public override void VisitIndexerDeclaration(IndexerDeclarationSyntax node)
		=> Emit("indexer", "this", node.Modifiers, node.AttributeLists, "", $"{node.Type} [{Parameters(node.ParameterList)}]", node.ThisKeyword);

	public override void VisitEventDeclaration(EventDeclarationSyntax node)
		=> Emit("event", node.Identifier.Text, node.Modifiers, node.AttributeLists, "", node.Type.ToString(), node.Identifier);

	public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
	{
		foreach (var variable in node.Declaration.Variables)
		{
			Emit("event", variable.Identifier.Text, node.Modifiers, node.AttributeLists, "", node.Declaration.Type.ToString(), variable.Identifier);
		}
	}

	public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
	{
		foreach (var variable in node.Declaration.Variables)
		{
			Emit("field", variable.Identifier.Text, node.Modifiers, node.AttributeLists, "", node.Declaration.Type.ToString(), variable.Identifier);
		}
	}
}
