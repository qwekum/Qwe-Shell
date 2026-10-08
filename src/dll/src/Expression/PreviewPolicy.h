#pragma once

#include <functional>
#include <string>

namespace Nilesoft::Shell
{
    class Context;
    class FuncExpression;
    class Expression;

    // A request-owned policy. It is never installed in Explorer's live context.
    // Failure is sticky: callers cannot accidentally turn an unavailable value
    // into a plausible false/zero result through the runtime's catch paths.
    struct PreviewPolicy
    {
        enum class Dispatch { Native, Supplied, Unavailable };
        std::size_t steps = 0;
        std::size_t depth = 0;
        std::size_t maxSteps = 100000;
        std::size_t maxDepth = 128;
        unsigned long long deadline = 0;
        // Assignments are safe only when every variable scope belongs to this
        // request.  Preview adapters must opt in after establishing that
        // ownership; automatic capture therefore fails closed by default.
        bool allowAssignments = false;
        bool failed = false;
        std::string code;
        std::wstring message;
        std::function<Dispatch(FuncExpression&, Context&, Object&)> dispatch;
        std::function<bool(const std::wstring&, Object&)> environment;
        std::function<void(const Expression*, const Object&)> observed;
        std::function<void(const Expression*)> unavailable;

        void Fail(const char* reason, const wchar_t* detail)
        {
            if(failed) return;
            failed = true;
            code = reason;
            message = detail;
        }

        bool Enter()
        {
            if(failed) return false;
            if(++steps > maxSteps || depth >= maxDepth ||
               (deadline && ::GetTickCount64() > deadline))
            {
                Fail("PREVIEW_LIMIT", L"Evaluation exceeded its step, recursion, or time limit.");
                return false;
            }
            ++depth;
            return true;
        }
    };
}
