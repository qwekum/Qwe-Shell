#pragma once

// The Studio language front end is deliberately independent of the Explorer
// extension.  It owns tokenization, source locations, and a syntax tree only;
// it never loads imports, reads files, or evaluates expressions.  The native
// Studio DLL and (eventually) the runtime parser consume this same front end.

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace Nilesoft::Shell::StudioLanguage
{
	// Syntax input is untrusted editor/IPC data.  Keep every native allocation
	// and recursive walk bounded; callers can present LANG_LIMIT as an ordinary
	// source diagnostic and decide how to continue editing.
	struct FrontendLimits final
	{
		static constexpr std::size_t MaxSourceCodeUnits = 32u * 1024u * 1024u;
		static constexpr std::size_t MaxTokenTextBytes = 1u * 1024u * 1024u;
		static constexpr std::size_t MaxTokens = 131072u;
		static constexpr std::size_t MaxNodes = 131072u;
		static constexpr std::size_t MaxProperties = 131072u;
		static constexpr std::size_t MaxExpressions = 131072u;
		static constexpr std::size_t MaxDiagnostics = 4096u;
		// Keep the recursive descent and JSON walks well below the default
		// Windows thread stack.  The parser's frames include several local
		// vectors and string views, so the public syntax depth must be
		// conservative even though the source nesting itself is bounded.
		static constexpr std::size_t MaxRecursionDepth = 64u;
		static constexpr std::size_t MaxJsonBytes = 8u * 1024u * 1024u;
	};

	struct Token
	{
		std::string kind;
		std::string text;
		int start = 0;
		int length = 0;
		int line = 1;
		int column = 1;
	};

	struct Diagnostic
	{
		std::string code;
		std::string message;
		std::string severity = "error";
		int start = 0;
		int length = 0;
		std::string nodeId;
		std::string remedy;
		std::vector<std::string> importChain;
	};

	struct ExpressionNode
	{
		std::string id;
		std::string kind;
		std::string text;
		int start = 0;
		int length = 0;
		std::vector<ExpressionNode> children;
	};

	struct Property
	{
		std::string name;
		int start = 0;
		int length = 0;
		int valueStart = 0;
		int valueLength = 0;
		bool hasExpression = false;
		ExpressionNode expression;
	};

	struct Node
	{
		std::string id;
		std::string kind;
		std::string name;
		int start = 0;
		int length = 0;
		int propertyInsert = -1;
		int childInsert = -1;
		std::vector<Property> properties;
		std::vector<Node> children;
		bool hasExpression = false;
		ExpressionNode expression;
	};

	struct Document
	{
		int version = 1;
		std::vector<Token> tokens;
		std::vector<Node> nodes;
		std::vector<Diagnostic> diagnostics;
	};

	class Frontend final
	{
	public:
		explicit Frontend(std::wstring_view source);

		// Parse is side-effect free.  All offsets are UTF-16 code-unit offsets,
		// matching System.String indices on Windows and the Studio DTO contract.
		Document Parse();

	private:
		struct Cursor;
		struct ExprResult;
		class RecursionGuard;

		std::wstring_view source_;
		std::vector<Token> tokens_;
		std::vector<std::size_t> significant_;
		std::vector<Diagnostic> diagnostics_;
		std::size_t cursor_ = 0;
		std::uint32_t nextNodeId_ = 1;
		std::uint32_t nextExpressionId_ = 1;
		std::size_t nodeCount_ = 0;
		std::size_t propertyCount_ = 0;
		std::size_t expressionCount_ = 0;
		std::size_t recursionDepth_ = 0;
		bool limitReached_ = false;
		bool limitDiagnosticAdded_ = false;

		void Lex();
		void ParseTopLevel(std::vector<Node>& into, std::size_t endToken = static_cast<std::size_t>(-1));
		Node ParseDeclaration(std::size_t keywordToken);
		Node ParseGenericDeclaration(std::size_t keywordToken, std::string kind);
		void ParseProperties(Node& node, std::size_t openToken, std::size_t closeToken);
		void ParseChildren(Node& node, std::size_t openToken, std::size_t closeToken);
		Property ParseProperty(std::size_t nameToken, std::size_t equalsToken, std::size_t boundaryToken);
		ExpressionNode ParseExpression(std::size_t first, std::size_t last, bool allowComma = false);
		ExpressionNode ParseExpressionRange(std::size_t& position, std::size_t last, int minimumPrecedence);
		ExpressionNode ParsePrimary(std::size_t& position, std::size_t last);
		ExpressionNode MakeExpression(std::string kind, std::size_t first, std::size_t last);
		ExpressionNode MakeExpressionSpan(std::string kind, std::size_t start, std::size_t length, std::string text);
		bool AppendProperty(Node& node, Property property, std::size_t token);
		bool AppendExpression(std::vector<ExpressionNode>& into, ExpressionNode expression, std::size_t token);
		bool AdoptExpressionTree(ExpressionNode const& expression, std::size_t token);
		bool EnterRecursion(std::size_t token);
		void LeaveRecursion();

		std::size_t NextSignificant(std::size_t token) const;
		std::size_t PreviousSignificant(std::size_t token) const;
		std::size_t FindMatching(std::size_t openToken, char close) const;
		std::size_t FindValueBoundary(std::size_t first, std::size_t limit) const;
		bool IsAssignment(std::size_t token) const;
		bool IsDeclaration(std::size_t token) const;
		bool IsIdentifier(std::size_t token) const;
		bool IsTrivia(std::size_t token) const;
		bool IsText(std::size_t token, std::string_view text) const;
		bool IsOpen(std::size_t token) const;
		bool IsClose(std::size_t token) const;
		int Precedence(std::string_view op) const;
		std::string SliceUtf8(std::size_t first, std::size_t last) const;
		std::string SliceUtf8Offsets(int start, int length) const;
		std::string TokenText(std::size_t token) const;
		void AddDiagnostic(std::string code, std::string message, std::size_t token,
			std::string severity = "error", int length = -1,
			std::string nodeId = {}, std::string remedy = {});
		void AddLimitDiagnostic(std::size_t token, std::string message);
		void AppendDiagnostic(Diagnostic diagnostic);
		std::string NewNodeId();
		std::string NewExpressionId();
	};

	// Metadata is intentionally explicit about coverage.  The parser accepts
	// unknown identifiers generically so handwritten configurations remain
	// inspectable, while the visual inventory reports the verified set.
	std::string CapabilitiesJson();
	std::string DocumentToJson(Document const& document);
}
