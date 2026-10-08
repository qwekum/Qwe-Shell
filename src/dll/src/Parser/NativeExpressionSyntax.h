#pragma once

#include "LanguageFrontend.h"

#include <cstddef>
#include <map>
#include <string>
#include <string_view>

namespace Nilesoft::Shell
{
	class Expression;
}

namespace Nilesoft::Shell::StudioLanguage
{
	// The runtime parser owns Expression objects, while the Studio front end
	// owns the source-backed syntax DTO.  This small record is the seam between
	// them: offsets are UTF-16 code-unit offsets, just like the native Lexer and
	// System.String on Windows.
	struct ExpressionSource final
	{
		std::wstring file;
		std::size_t start = 0;
		std::size_t length = 0;
	};

	using ExpressionSources = std::map<const ::Nilesoft::Shell::Expression*, ExpressionSource>;

	// Projects one expression produced by the native parser into the shared
	// lossless Studio expression model.  The source and span table are supplied
	// by the caller so this function never reads files, evaluates an expression,
	// or consults runtime state.
	ExpressionNode ProjectNativeExpression(const ::Nilesoft::Shell::Expression* expression,
		std::wstring_view source, const ExpressionSources& sources);
}
