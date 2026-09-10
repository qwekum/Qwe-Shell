#define SHELL_STUDIO_LANGUAGE_BUILD
#include "../../dll/src/pch.h"
#include "ShellStudioLanguage.h"
#include "../../dll/src/Parser/Parser.h"
#include "../../shared/System/Text/Encoding.h"

#include <cstdlib>
#include <cstring>
#include <limits>
#include <new>
#include <string>

#if defined(_MSC_VER)
#define SHELL_STUDIO_EXPORT __declspec(dllexport)
#define SHELL_STUDIO_CALL __cdecl
#else
#define SHELL_STUDIO_EXPORT
#define SHELL_STUDIO_CALL
#endif

namespace
{
	constexpr std::size_t MaxNativeInputCodeUnits = 4u * 1024u * 1024u;

	char* CopyResult(std::string const& value) noexcept
	{
		void* memory = std::malloc(value.size() + 1);
		if (!memory) return nullptr;
		std::memcpy(memory, value.data(), value.size());
		static_cast<char*>(memory)[value.size()] = '\0';
		return static_cast<char*>(memory);
	}

	std::string Failure(std::string const& code, std::string const& message)
	{
		return "{\"version\":1,\"tokens\":[],\"nodes\":[],\"diagnostics\":[{\"code\":\"" + code +
			"\",\"message\":\"" + message + "\",\"severity\":\"error\",\"file\":null,\"start\":0,\"length\":0,\"nodeId\":null,\"remedy\":null,\"importChain\":[]}]}";
	}
}

extern "C"
{
	SHELL_STUDIO_EXPORT char* SHELL_STUDIO_CALL shell_studio_parse(const wchar_t* text, std::size_t length) noexcept
	{
		if (text == nullptr && length != 0)
			return CopyResult(Failure("LANG_INPUT", "The source pointer is null for a non-empty document."));
		if (length > MaxNativeInputCodeUnits)
			return CopyResult(Failure("LANG_SIZE", "The source exceeds the native language service input limit."));
		try
		{
			using SyntaxInput = Nilesoft::Shell::Parser::SyntaxInput;
			Nilesoft::Shell::Parser parser(SyntaxInput{
				std::wstring_view(text ? text : L"", length),
				L"<studio>"});
			parser.Load();
			return CopyResult(Nilesoft::Shell::StudioLanguage::DocumentToJson(parser.StudioSyntax()));
		}
		catch (const std::bad_alloc&)
		{
			return CopyResult(Failure("LANG_MEMORY", "The language service could not allocate a result."));
		}
		catch (...)
		{
			return CopyResult(Failure("LANG_INTERNAL", "The language service failed without executing configuration content."));
		}
	}

	SHELL_STUDIO_EXPORT char* SHELL_STUDIO_CALL shell_studio_capabilities() noexcept
	{
		try { return CopyResult(Nilesoft::Shell::StudioLanguage::CapabilitiesJson()); }
		catch (const std::bad_alloc&) { return nullptr; }
		catch (...) { return nullptr; }
	}

	SHELL_STUDIO_EXPORT int SHELL_STUDIO_CALL shell_studio_source_encoding(const unsigned char* bytes, std::size_t length) noexcept
	{
		try { return static_cast<int>(Nilesoft::Text::Encoding::GetType(const_cast<unsigned char*>(bytes), length)); }
		catch (...) { return static_cast<int>(Nilesoft::Text::EncodingType::Unknown); }
	}

	SHELL_STUDIO_EXPORT unsigned int SHELL_STUDIO_CALL shell_studio_ansi_code_page() noexcept
	{
		try { return static_cast<unsigned int>(::GetACP()); }
		catch (...) { return 0; }
	}

	SHELL_STUDIO_EXPORT void SHELL_STUDIO_CALL shell_studio_free(void* pointer) noexcept
	{
		std::free(pointer);
	}
}
