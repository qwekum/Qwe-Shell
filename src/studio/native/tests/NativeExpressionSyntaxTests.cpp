#include "../../../dll/src/pch.h"
#include "../../../dll/src/Parser/NativeExpressionSyntax.h"
#include "../../../dll/src/Parser/Parser.h"

#include <cassert>
#include <cstdlib>
#include <iostream>
#include <map>
#include <set>
#include <string>

#undef assert
#define assert(expression) do { \
	if(!(expression)) { \
		std::cerr << "Assertion failed: " << #expression << " at line " << __LINE__ << "\n"; \
		std::exit(EXIT_FAILURE); \
	} \
} while(false)

using Nilesoft::Shell::Parser;
using Nilesoft::Shell::StudioLanguage::ExpressionNode;
using Nilesoft::Shell::StudioLanguage::ExpressionSource;
using Nilesoft::Shell::StudioLanguage::ExpressionSources;
using Nilesoft::Shell::StudioLanguage::ProjectNativeExpression;

// The parser path exercised here never evaluates color/runtime functions.
// Keep the focused executable independent from Explorer's Main.cpp while
// satisfying the one runtime-only symbol pulled by FuncExpression.cpp.
namespace Nilesoft::Shell
{
	uint32_t ImmersiveColor::GetColorByColorType(uint32_t) { return 0; }
	bool ImmersiveColor::IsSupported() { return false; }
}

namespace
{
	struct Projected final
	{
		std::wstring source;
		ExpressionNode syntax;
	};

	Projected Parse(std::wstring source)
	{
		Parser parser(Parser::SyntaxInput{ source, L"<native-expression-test>" });
		auto native = parser.ParseExpression(source);
		if(!native)
		{
			std::wcerr << L"Native parse failed for: " << source << L"\n";
			std::exit(EXIT_FAILURE);
		}
		assert(native);

		ExpressionSources sources;
		for(const auto& [expression, span] : parser.ExpressionSources)
			sources.emplace(expression, ExpressionSource{ span.file, span.start, span.length });
		auto syntax = ProjectNativeExpression(native.get(), source, sources);
		Projected result{ std::move(source), std::move(syntax) };
		assert(!result.syntax.id.empty());
		return result;
	}

	void AssertSpan(const ExpressionNode& node, const std::wstring& source)
	{
		assert(node.start >= 0);
		assert(node.length >= 0);
		const auto start = static_cast<std::size_t>(node.start);
		const auto length = static_cast<std::size_t>(node.length);
		assert(start <= source.size());
		assert(length <= source.size() - start);
		std::string expected;
		expected.reserve(length);
		for(std::size_t index = start; index < start + length; ++index)
		{
			const auto value = static_cast<unsigned>(source[index]);
			if(value <= 0x7f) expected.push_back(static_cast<char>(value));
			else expected.push_back('?');
		}
		// These focused cases are ASCII, which makes this check independent of
		// the platform's wide-character encoding while still checking exact spans.
		assert(node.text == expected);
		for(const auto& child : node.children) AssertSpan(child, source);
	}

	void AssertContained(int start, int length, int parentStart, int parentLength)
	{
		if(start < parentStart || length < 0 || start > parentStart + parentLength - length)
			std::cerr << "Source span " << start << "+" << length << " outside parent "
				<< parentStart << "+" << parentLength << "\n";
		assert(start >= parentStart);
		assert(length >= 0);
		assert(start <= parentStart + parentLength - length);
	}

	void AssertExpressionContained(const ExpressionNode& expression, int start, int length)
	{
		AssertContained(expression.start, expression.length, start, length);
		for(const auto& child : expression.children)
			AssertExpressionContained(child, expression.start, expression.length);
	}

	void AssertNodeContained(const Nilesoft::Shell::StudioLanguage::Node& node, int start, int length)
	{
		AssertContained(node.start, node.length, start, length);
		if(node.propertyInsert >= 0) AssertContained(node.propertyInsert, 0, node.start, node.length);
		if(node.childInsert >= 0) AssertContained(node.childInsert, 0, node.start, node.length);
		if(node.hasExpression) AssertExpressionContained(node.expression, node.start, node.length);
		for(const auto& property : node.properties)
		{
			AssertContained(property.start, property.length, node.start, node.length);
			AssertContained(property.valueStart, property.valueLength, property.start, property.length);
			if(property.hasExpression)
				AssertExpressionContained(property.expression, property.valueStart, property.valueLength);
		}
		for(const auto& child : node.children) AssertNodeContained(child, node.start, node.length);
	}

	void AssertUniqueIds(const ExpressionNode& node, std::set<std::string>& ids)
	{
		assert(!node.id.empty());
		assert(ids.insert(node.id).second);
		for(const auto& child : node.children) AssertUniqueIds(child, ids);
	}

	const ExpressionNode& Child(const ExpressionNode& node, std::size_t index)
	{
		assert(index < node.children.size());
		return node.children[index];
	}
}

int main()
{
	{
		auto projected = Parse(L"foo + bar");
		assert(projected.syntax.kind == "binary");
		assert(projected.syntax.text == "foo + bar");
		assert(Child(projected.syntax, 0).kind == "identifier");
		assert(Child(projected.syntax, 1).kind == "identifier");
	}

	{
		auto projected = Parse(L"$count += 1");
		assert(projected.syntax.kind == "assignment");
		assert(projected.syntax.text == "$count += 1");
		assert(Child(projected.syntax, 0).kind == "variable");
		assert(Child(projected.syntax, 0).text == "$count");
		assert(Child(projected.syntax, 1).kind == "literal");
		assert(Child(projected.syntax, 1).text == "1");
	}

	{
		auto projected = Parse(L"foo.bar(1).baz");
		assert(projected.syntax.kind == "member");
		assert(projected.syntax.text == "foo.bar(1).baz");
		const auto& call = Child(projected.syntax, 0);
		assert(call.kind == "call");
		assert(call.text == "foo.bar(1)");
		assert(Child(call, 0).kind == "literal");
		assert(Child(projected.syntax, 1).kind == "identifier");
	}

	{
		auto projected = Parse(L"sel[0].name");
		assert(projected.syntax.kind == "member");
		assert(projected.syntax.text == "sel[0].name");
		const auto& index = Child(projected.syntax, 0);
		assert(index.kind == "index");
		assert(Child(index, 0).kind == "identifier");
		assert(Child(index, 1).kind == "literal");
		assert(Child(projected.syntax, 1).kind == "identifier");
	}

	{
		auto projected = Parse(L"(foo + bar)");
		assert(projected.syntax.kind == "group");
		assert(projected.syntax.text == "(foo + bar)");
		assert(Child(projected.syntax, 0).kind == "binary");
		assert(Child(Child(projected.syntax, 0), 0).kind == "identifier");
	}

	{
		auto projected = Parse(L"'Hello @(sel.path) %user% world'");
		assert(projected.syntax.kind == "interpolation");
		// Interpolated strings retain their source structure and must not be
		// advertised as one decoded constant, even when their literal text is
		// otherwise valid.
		assert(!projected.syntax.literalString.has_value());
		assert(projected.syntax.children.size() == 5);
		assert(Child(projected.syntax, 0).kind == "interpolationText");
		assert(Child(projected.syntax, 0).text == "Hello ");
		assert(Child(projected.syntax, 1).kind == "interpolatedExpression");
		assert(Child(projected.syntax, 1).text == "@(sel.path)");
		assert(Child(Child(projected.syntax, 1), 0).kind == "group");
		assert(Child(Child(Child(projected.syntax, 1), 0), 0).kind == "member");
		assert(Child(projected.syntax, 2).kind == "interpolationText");
		assert(Child(projected.syntax, 3).kind == "environment");
		assert(Child(projected.syntax, 3).text == "%user%");
		assert(Child(projected.syntax, 4).kind == "interpolationText");
	}

	{
		auto projected = Parse(L"''");
		assert(projected.syntax.kind == "literal");
		assert(projected.syntax.literalString.has_value());
		assert(projected.syntax.literalString->empty());
	}

	{
		auto projected = Parse(L"'decoded'");
		assert(projected.syntax.kind == "literal");
		assert(projected.syntax.literalString.has_value());
		assert(*projected.syntax.literalString == "decoded");
	}

	{
		auto projected = Parse(L"'imports/studio.nss'");
		assert(projected.syntax.kind == "literal");
		assert(projected.syntax.literalString.has_value());
		assert(*projected.syntax.literalString == "imports/studio.nss");
	}

	{
		auto projected = Parse(L"\"escaped \\\"quote\\\"\"");
		assert(projected.syntax.kind == "literal");
		assert(projected.syntax.literalString.has_value());
		assert(*projected.syntax.literalString == "escaped \"quote\"");
	}

	{
		auto projected = Parse(L"'100% complete'");
		assert(projected.syntax.kind == "literal");
		assert(projected.syntax.literalString.has_value());
		assert(*projected.syntax.literalString == "100% complete");
	}

	{
		auto projected = Parse(L"'%USERPROFILE%'");
		assert(projected.syntax.kind == "interpolation");
		assert(!projected.syntax.literalString.has_value());
		assert(projected.syntax.children.size() == 1);
		assert(Child(projected.syntax, 0).kind == "environment");
	}

	{
		auto projected = Parse(L"'theme @(if(theme.islight, '#fff', '#000'))'");
		assert(projected.syntax.kind == "interpolation");
		assert(!projected.syntax.literalString.has_value());
		assert(projected.syntax.children.size() >= 2);
		for(const auto& child : projected.syntax.children)
			assert(child.kind != "literal" || !child.literalString.has_value());
	}

	{
		auto projected = Parse(L"{foo() bar()}");
		assert(projected.syntax.kind == "statement");
		assert(projected.syntax.children.size() == 2);
		assert(Child(projected.syntax, 0).kind == "call");
		assert(Child(projected.syntax, 1).kind == "call");
	}

	{
		auto projected = Parse(L"[foo, 1]");
		assert(projected.syntax.kind == "array");
		assert(projected.syntax.children.size() == 2);
		assert(Child(projected.syntax, 0).kind == "identifier");
		assert(Child(projected.syntax, 1).kind == "literal");
	}

	{
		auto projected = Parse(L"a ? b : c");
		assert(projected.syntax.kind == "ternary");
		assert(projected.syntax.children.size() == 3);
	}

	{
		auto projected = Parse(L"-foo");
		assert(projected.syntax.kind == "unary");
		assert(Child(projected.syntax, 0).kind == "identifier");
	}

	{
		auto projected = Parse(L"for(i = 0, i < 3, i)");
		assert(projected.syntax.kind == "for");
		assert(projected.syntax.children.size() == 3);
		assert(Child(projected.syntax, 0).kind == "assignment");
		assert(Child(projected.syntax, 1).kind == "binary");
		assert(Child(projected.syntax, 2).kind == "identifier");
	}

	{
		auto projected = Parse(L"\"\xD83D\xDE00\" + foo");
		assert(projected.syntax.kind == "binary");
		assert(projected.syntax.start == 0);
		assert(projected.syntax.length == 10); // The native contract uses UTF-16 units.
		const std::string emoji = "\xF0\x9F\x98\x80";
		assert(projected.syntax.text == "\"" + emoji + "\" + foo");
	}

	// Every projected node is independently addressable for editor selection
	// and must have a bounded, unique identity.
	{
		auto projected = Parse(L"foo.bar([1, 2], (x + y))");
		AssertSpan(projected.syntax, projected.source);
		std::set<std::string> ids;
		AssertUniqueIds(projected.syntax, ids);
	}

	// The public source table must cover intermediate operator nodes even when
	// the allocator does not happen to reuse a destroyed import's address.
	{
		const std::wstring source = L"loc_path + sys.lang + \".nss\"";
		Parser parser(Parser::SyntaxInput{ source, L"<operator-source-test>" });
		auto native = parser.ParseExpression(source);
		const auto* root = dynamic_cast<const Nilesoft::Shell::BinaryExpression*>(native.get());
		assert(root);
		const auto* intermediate = dynamic_cast<const Nilesoft::Shell::BinaryExpression*>(root->Left);
		assert(intermediate);
		const auto record = parser.ExpressionSources.find(intermediate);
		assert(record != parser.ExpressionSources.end());
		assert(record->second.start == 0);
		assert(record->second.length == 19);
		auto syntax = ProjectNativeExpression(native.get(), source, parser.ExpressionSources);
		AssertExpressionContained(syntax, 0, static_cast<int>(source.size()));
		assert(Child(syntax, 0).text == "loc_path + sys.lang");
	}

	// Import expressions are destroyed after projection. A later allocation may
	// reuse their address; every intermediate operator node still needs its own
	// current source record. Exercise the same Parser::Load path as the DLL,
	// including multiline localized imports and UTF-16 offsets under both EOLs.
	for(const auto* newline : { L"\n", L"\r\n" })
	{
		const std::wstring eol = newline;
		const std::wstring source =
			L"$loc_path='imports/\u65E5\xD83D\xDE00/'" + eol +
			L"import lang loc_path + \"en.nss\"" + eol +
			L"import lang if(path.exists(loc_path + sys.lang + \".nss\")," + eol +
			L"               loc_path + sys.lang + \".nss\"," + eol +
			L"               loc_path + \"en.nss\")" + eol +
			L"import loc loc_path + sys.lang + \".nss\"" + eol +
			L"import lang if(path.exists(loc_path + \"extra.nss\"), loc_path + \"extra.nss\", loc_path + \"en.nss\")" + eol +
			L"menu(title=\"\u65E5\xD83D\xDE00\") { item(title=loc_path + sys.lang + \".nss\") }" + eol;
		Parser parser(Parser::SyntaxInput{ source, L"<dynamic-import-span-test>" });
		assert(parser.Load());
		const auto& document = parser.StudioSyntax();
		assert(document.nodes.size() == 6);
		for(const auto& diagnostic : document.diagnostics)
			assert(diagnostic.severity != "error");
		for(const auto& token : document.tokens)
			AssertContained(token.start, token.length, 0, static_cast<int>(source.size()));
		for(const auto& node : document.nodes)
			AssertNodeContained(node, 0, static_cast<int>(source.size()));
		assert(document.nodes[2].expression.kind == "if");
		assert(document.nodes[2].expression.children.size() == 3);
		const auto& condition = document.nodes[2].expression.children[0];
		assert(condition.kind == "call");
		assert(condition.children.size() == 1);
		const auto& path = condition.children[0];
		assert(path.kind == "binary");
		assert(path.text == "loc_path + sys.lang + \".nss\"");
		assert(Child(path, 0).kind == "binary");
		assert(Child(path, 0).text == "loc_path + sys.lang");
		const auto& title = document.nodes[5].properties[0].expression;
		assert(title.length == 5); // Quotes, one BMP unit, and a surrogate pair.
		assert(title.text == "\"\xE6\x97\xA5\xF0\x9F\x98\x80\"");
	}

	// Syntax-only imports must preserve a mutating expression without invoking
	// it. The target is task-owned and absent before and after parsing.
	{
		wchar_t temporaryDirectory[MAX_PATH]{};
		wchar_t marker[MAX_PATH]{};
		assert(GetTempPathW(MAX_PATH, temporaryDirectory) > 0);
		assert(GetTempFileNameW(temporaryDirectory, L"nsp", 0, marker) != 0);
		assert(DeleteFileW(marker));
		std::wstring path = marker;
		std::replace(path.begin(), path.end(), L'\\', L'/');
		const std::wstring source = L"import io.file.create(\"" + path + L"\")";
		Parser parser(Parser::SyntaxInput{ source, L"<nonexecuting-import-test>" });
		assert(parser.Load());
		assert(GetFileAttributesW(marker) == INVALID_FILE_ATTRIBUTES);
		assert(GetLastError() == ERROR_FILE_NOT_FOUND);
		const auto& document = parser.StudioSyntax();
		assert(document.nodes.size() == 1);
		assert(document.nodes[0].expression.kind == "call");
		assert(std::any_of(document.diagnostics.begin(), document.diagnostics.end(),
			[](const auto& diagnostic) { return diagnostic.code == "LANG_IMPORT_DYNAMIC"; }));
		AssertNodeContained(document.nodes[0], 0, static_cast<int>(source.size()));
	}

	std::cout << "Native expression syntax tests passed\n";
	return 0;
}
