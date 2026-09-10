#pragma once

#include <cstddef>

#if defined(_MSC_VER) && defined(SHELL_STUDIO_LANGUAGE_BUILD)
#define SHELL_STUDIO_LANGUAGE_EXPORT __declspec(dllexport)
#define SHELL_STUDIO_LANGUAGE_CALL __cdecl
#elif defined(_MSC_VER)
#define SHELL_STUDIO_LANGUAGE_EXPORT __declspec(dllimport)
#define SHELL_STUDIO_LANGUAGE_CALL __cdecl
#else
#define SHELL_STUDIO_LANGUAGE_EXPORT
#define SHELL_STUDIO_LANGUAGE_CALL
#endif

extern "C"
{
	// The returned UTF-8 buffer is allocated by the native library and must be
	// released with shell_studio_free.  `length` is a UTF-16 code-unit count.
	SHELL_STUDIO_LANGUAGE_EXPORT char* SHELL_STUDIO_LANGUAGE_CALL shell_studio_parse(const wchar_t* text, std::size_t length) noexcept;
	SHELL_STUDIO_LANGUAGE_EXPORT char* SHELL_STUDIO_LANGUAGE_CALL shell_studio_capabilities() noexcept;
	// Returns the native Text::Encoding::EncodingType value for the supplied
	// bytes.  Studio uses this to keep source-file decoding in lockstep with
	// the runtime parser instead of guessing ANSI after a UTF-8 failure.
	SHELL_STUDIO_LANGUAGE_EXPORT int SHELL_STUDIO_LANGUAGE_CALL shell_studio_source_encoding(const unsigned char* bytes, std::size_t length) noexcept;
	SHELL_STUDIO_LANGUAGE_EXPORT unsigned int SHELL_STUDIO_LANGUAGE_CALL shell_studio_ansi_code_page() noexcept;
	SHELL_STUDIO_LANGUAGE_EXPORT void SHELL_STUDIO_LANGUAGE_CALL shell_studio_free(void* pointer) noexcept;
}

#undef SHELL_STUDIO_LANGUAGE_EXPORT
#undef SHELL_STUDIO_LANGUAGE_CALL
