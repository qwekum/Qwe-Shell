#include "../../dll/src/pch.h"
#include "../../dll/src/Include/Theme.h"

// The syntax host links the parser and expression object model so that the
// exported language entry point uses Parser::SyntaxInput.  Runtime-only theme
// discovery is intentionally unavailable in this host; syntax parsing never
// evaluates expressions, so these conservative implementations are sufficient
// for the shared object model without loading the Explorer hook runtime.
namespace Nilesoft::Shell
{
	uint32_t ImmersiveColor::GetColorByColorType(uint32_t)
	{
		return 0;
	}

	bool ImmersiveColor::IsSupported()
	{
		return false;
	}
}
