#include "../../../dll/src/Parser/LanguageFrontend.h"

#include <algorithm>
#include <cassert>
#include <iostream>
#include <string>

using Nilesoft::Shell::StudioLanguage::Document;
using Nilesoft::Shell::StudioLanguage::Frontend;
using Nilesoft::Shell::StudioLanguage::FrontendLimits;

namespace
{
	std::string Reconstruct(const Document& document)
	{
		std::string result;
		for (const auto& token : document.tokens)
			if (token.kind != "eof") result += token.text;
		return result;
	}

	const Nilesoft::Shell::StudioLanguage::Node& Child(const Nilesoft::Shell::StudioLanguage::Node& node, std::size_t index)
	{
		assert(index < node.children.size());
		return node.children[index];
	}
}

int main()
{
	const std::wstring source = L"// preserve\r\nmenu(title='Root', where=sel.count > 0)\r\n{\r\n  item(title=\"Open\" cmd=command.copy(sel.path))\r\n  separator\r\n}\r\nimport 'imports/studio.nss'\r\n$answer = if(true, 1, 2)\r\n";
	const auto document = Frontend(source).Parse();
	assert(Reconstruct(document) == "// preserve\r\nmenu(title='Root', where=sel.count > 0)\r\n{\r\n  item(title=\"Open\" cmd=command.copy(sel.path))\r\n  separator\r\n}\r\nimport 'imports/studio.nss'\r\n$answer = if(true, 1, 2)\r\n");
	assert(document.nodes.size() == 3);
	assert(document.nodes[0].kind == "menu");
	assert(document.nodes[0].properties.size() == 2);
	assert(document.nodes[0].propertyInsert > document.nodes[0].start);
	assert(document.nodes[0].childInsert > document.nodes[0].start);
	assert(Child(document.nodes[0], 0).kind == "item");
	assert(Child(document.nodes[0], 1).kind == "separator");
	assert(Child(document.nodes[0], 0).properties[1].expression.kind == "call");
	assert(document.nodes[1].kind == "import");
	assert(document.nodes[1].hasExpression);
	assert(document.nodes[1].expression.text == "'imports/studio.nss'");
	assert(document.nodes[2].kind == "variable");
	assert(document.nodes[2].hasExpression);
	assert(document.nodes[2].expression.kind == "if");
	assert(document.diagnostics.empty());

	const auto malformed = Frontend(L"menu(title=\"broken\"\n").Parse();
	assert(!malformed.diagnostics.empty());

	const std::wstring interpolationSource = L"item(title='Hello @(sel.path[0].name) %user% world')";
	const auto interpolationDocument = Frontend(interpolationSource).Parse();
	assert(interpolationDocument.diagnostics.empty());
	const auto& interpolation = interpolationDocument.nodes[0].properties[0].expression;
	assert(interpolation.kind == "interpolation");
	assert(interpolation.children.size() == 5);
	assert(interpolation.children[0].kind == "interpolationText");
	assert(interpolation.children[0].text == "Hello ");
	assert(interpolation.children[1].kind == "interpolatedExpression");
	assert(interpolation.children[1].text == "@(sel.path[0].name)");
	assert(interpolation.children[1].children.size() == 1);
	assert(interpolation.children[1].children[0].kind == "group");
	const auto& member = interpolation.children[1].children[0].children[0];
	assert(member.kind == "member");
	assert(member.children.size() == 2);
	assert(member.children[0].kind == "index");
	assert(member.children[0].children.size() == 2);
	assert(member.children[0].children[0].kind == "member");
	assert(member.children[0].children[1].kind == "literal");
	assert(member.children[1].kind == "identifier");
	assert(interpolation.children[2].kind == "interpolationText");
	assert(interpolation.children[3].kind == "environment");
	assert(interpolation.children[3].text == "%user%");
	assert(interpolation.children[4].kind == "interpolationText");

	const auto statements = Frontend(L"item(title={foo() bar()})").Parse();
	assert(statements.diagnostics.empty());
	const auto& statement = statements.nodes[0].properties[0].expression;
	assert(statement.kind == "statement");
	assert(statement.children.size() == 2);
	assert(statement.children[0].kind == "call");
	assert(statement.children[1].kind == "call");

	const auto commented = Frontend(L"item(title=foo /* comment */ + bar)").Parse();
	assert(commented.diagnostics.empty());
	const auto& binary = commented.nodes[0].properties[0].expression;
	assert(binary.kind == "binary");
	assert(binary.children.size() == 2);
	assert(binary.children[1].kind == "identifier");

	// Names that are also menu-property keywords remain valid member segments
	// when they follow a dot.  The value-boundary scan must still split a
	// subsequent bare flag/property on the same line.
	const auto keywordMembers = Frontend(L"item(title=app.directory, where=this.checked, cmd=path.sep checked)").Parse();
	assert(keywordMembers.diagnostics.empty());
	assert(keywordMembers.nodes.size() == 1);
	assert(keywordMembers.nodes[0].properties.size() == 4);
	assert(keywordMembers.nodes[0].properties[0].expression.kind == "member");
	assert(keywordMembers.nodes[0].properties[1].expression.kind == "member");
	assert(keywordMembers.nodes[0].properties[2].expression.kind == "member");
	assert(keywordMembers.nodes[0].properties[3].name == "checked");

	// The recursive descent parser must fail closed before exhausting the
	// Windows thread stack on adversarial delimiter nesting.
	std::wstring deeplyNested = L"item(title=";
	deeplyNested.append(FrontendLimits::MaxRecursionDepth + 32, L'(');
	deeplyNested += L"1";
	deeplyNested.append(FrontendLimits::MaxRecursionDepth + 32, L')');
	deeplyNested += L")";
	const auto bounded = Frontend(deeplyNested).Parse();
	assert(std::any_of(bounded.diagnostics.begin(), bounded.diagnostics.end(),
		[](const auto& diagnostic) { return diagnostic.code == "LANG_LIMIT"; }));
	assert(DocumentToJson(bounded).find("\"code\":\"LANG_LIMIT\"") != std::string::npos);

	const auto capabilities = Nilesoft::Shell::StudioLanguage::CapabilitiesJson();
	assert(capabilities.find("\"complete\":false") != std::string::npos);
	assert(capabilities.find("\"name\":\"menu\"") != std::string::npos);
	std::cout << "ShellStudio.Language native tests passed\n";
	return 0;
}
