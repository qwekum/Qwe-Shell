#pragma once

#include "PreviewJson.h"
#include "PreviewBuiltins.h"
#include "PreviewSelection.h"
#include <cmath>
#include <filesystem>
#include <iomanip>
#include <sstream>
#include <optional>
#include <bcrypt.h>
#pragma comment(lib, "bcrypt.lib")

namespace Nilesoft::Shell::StudioPreview
{
    namespace Json = ::ShellStudio::PreviewJson;

    inline std::string Utf8(std::wstring_view value)
    {
        if(value.empty()) return {};
        const int size = ::WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
            static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
        if(size <= 0) throw std::invalid_argument("Invalid UTF-16 input.");
        std::string result(size, '\0');
        ::WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
            static_cast<int>(value.size()), result.data(), size, nullptr, nullptr);
        return result;
    }

    inline std::wstring Wide(std::string_view value)
    {
        if(value.empty()) return {};
        const int size = ::MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
        if(size <= 0) throw std::invalid_argument("Invalid UTF-8 input.");
        std::wstring result(size, L'\0');
        ::MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), size);
        return result;
    }

    inline std::string DiagnosticJson(std::string_view code, std::string_view message, std::string_view file = {}, int start = 0)
    {
        return "{\"code\":" + Json::Quote(code) + ",\"message\":" + Json::Quote(message) +
            ",\"severity\":\"warning\",\"file\":" + Json::Quote(file) + ",\"start\":" + std::to_string(start) + ",\"length\":0}";
    }

    inline std::string FailureJson(std::string_view code, std::string_view message)
    {
        return "{\"version\":1,\"available\":false,\"diagnostics\":[" + DiagnosticJson(code, message) + "]}";
    }

    inline std::wstring CanonicalPath(std::wstring value)
    {
        value = std::filesystem::path(value).lexically_normal().wstring();
        for(auto& ch : value) { if(ch == L'/') ch = L'\\'; ch = std::towlower(ch); }
        return value;
    }

    inline std::string Fingerprint(std::string_view source)
    {
        UCHAR digest[32]{};
        if(BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0,
            reinterpret_cast<PUCHAR>(const_cast<char*>(source.data())), static_cast<ULONG>(source.size()), digest, sizeof(digest)) < 0)
            throw std::runtime_error("The dependency fingerprint could not be computed.");
        static constexpr char hex[] = "0123456789abcdef";
        std::string result;
        for(const auto byte : digest) { result += hex[byte >> 4]; result += hex[byte & 15]; }
        return result;
    }

    inline double NumberMember(const Json::Value& value, std::string_view name, double fallback)
    {
        auto field = value.Find(name);
        if(!field || field->kind != Json::Kind::Number) return fallback;
        const auto number = std::stod(field->text);
        if(!std::isfinite(number)) throw std::invalid_argument("A context number is not finite.");
        return number;
    }

    inline Object NativeValue(const Json::Value& value)
    {
        switch(value.kind)
        {
        case Json::Kind::String: return Object(Wide(value.text));
        case Json::Kind::Boolean: return Object(value.boolean);
        case Json::Kind::Number:
        {
            const auto number = std::stod(value.text);
            if(!std::isfinite(number)) throw std::invalid_argument("A supplied number is not finite.");
            return Object(number);
        }
        case Json::Kind::Null: return nullptr;
        case Json::Kind::Array:
        {
            if(value.array.size() > 4096) throw std::invalid_argument("A supplied array exceeds the preview limit.");
            auto elements = std::make_unique<Object[]>(value.array.size() + 1);
            elements[0] = static_cast<uint32_t>(value.array.size());
            for(std::size_t i = 0; i < value.array.size(); ++i) elements[i + 1] = NativeValue(value.array[i]).move();
            return Object(elements.release(), true).move();
        }
        default: throw std::invalid_argument("Supplied values must be primitive values or arrays.");
        }
    }

    inline std::string ValueJson(const Object& value, unsigned depth = 0)
    {
        if(depth > 32) throw std::invalid_argument("The evaluated value exceeds its depth limit.");
        if(value.is_null()) return "null";
        if(value.is_array(true))
        {
            auto values = value.get_pointer();
            const uint32_t count = values[0];
            if(count > 4096) throw std::invalid_argument("The evaluated array exceeds its limit.");
            std::string result = "[";
            for(uint32_t i = 0; i < count; ++i) { if(i) result += ','; result += ValueJson(values[i + 1], depth + 1); }
            return result + ']';
        }
        if(value.is_number())
        {
            const auto number = value.to_number<double>();
            if(!std::isfinite(number)) throw std::invalid_argument("The evaluated number is not finite.");
            std::ostringstream stream;
            stream.imbue(std::locale::classic());
            stream << std::setprecision(17) << number;
            return stream.str();
        }
        const auto text = value.to_string();
        return Json::Quote(text.empty() ? std::string{} : Utf8(std::wstring_view(text.c_str(), text.length())));
    }

    inline bool MatchesIdent(const Ident& id, std::string_view name)
    {
        std::size_t start = 0;
        unsigned index = 0;
        while(start < name.size())
        {
            const auto end = name.find('.', start);
            const auto part = name.substr(start, end == name.npos ? name.size() - start : end - start);
            if(index >= id.length() || string(Wide(part)).hash() != id[index++]) return false;
            if(end == name.npos) break;
            start = end + 1;
        }
        return index == id.length();
    }

    inline bool MatchesReadFunction(const Ident& id, std::string_view name)
    {
        if(MatchesIdent(id, name)) return true;
        // The language defines bare reg(...) as the read alias of reg.get(...).
        // Broker payloads use the canonical name so one exact snapshot serves
        // both spellings without broadening the granted registry scope.
        return id[0] == IDENT_REG && id[1] == 0 && name == "reg.get";
    }

    class Session
    {
    public:
        struct Source { std::wstring path, text; };
        const Json::Value& request;
        PreviewPolicy policy;
        PreviewSelection selection;
        std::map<std::wstring, Source> documents;
        std::vector<std::string> diagnostics;
        std::unique_ptr<Parser> parser;
        DPI dpi;
        Theme theme;
        std::wstring root;
        std::string queryResult;
        std::size_t queryOccurrence = 0;
        std::vector<std::string> trace;
        std::optional<Parser::ExpressionSource> unavailableSource;

        explicit Session(const Json::Value& input) : request(input)
        {
            // The parser and all of its scopes are owned by this request, so
            // language-level assignments may preserve native evaluation order
            // without touching Explorer or another preview session.
            policy.allowAssignments = true;
            if(Json::StringMember(request, "mode") == "captured")
            {
                if(const auto capture = request.Find("capture"))
                    if(const auto snapshot = capture->Find("selection"); snapshot && snapshot->kind == Json::Kind::Object)
                        selection.LoadCaptured(*snapshot);
            }
            else if(const auto snapshot = request.Find("selection"); snapshot && snapshot->kind == Json::Kind::Object)
                selection.LoadSample(*snapshot);
            policy.deadline = ::GetTickCount64() + 2000;
            policy.unavailable = [&](const Expression* expression)
            {
                if(unavailableSource || !parser) return;
                if(auto span = parser->ExpressionSources.find(expression); span != parser->ExpressionSources.end()) unavailableSource = span->second;
            };
            if(const auto enabled = request.Find("trace"); enabled && enabled->kind == Json::Kind::Boolean && enabled->boolean)
                policy.observed = [&](const Expression* expression, const Object& value)
                {
                    if(trace.size() >= 1024 || !parser) return;
                    auto span = parser->ExpressionSources.find(expression);
                    if(span == parser->ExpressionSources.end()) return;
                    auto resolved = ValueJson(value);
                    // Trace stores the result of this evaluation, never invokes it again.
                    if(resolved.size() > 4096) resolved = "null";
                    trace.push_back("{\"file\":" + Json::Quote(Utf8(span->second.file)) +
                        ",\"start\":" + std::to_string(span->second.start) + ",\"length\":" + std::to_string(span->second.length) +
                        ",\"expressionType\":" + std::to_string(static_cast<int>(expression->Type())) + ",\"value\":" + resolved + '}');
                };
            policy.dispatch = [&](FuncExpression& function, Context& context, Object& supplied)
            {
                if(PureNativeFunction(function, context)) return PreviewPolicy::Dispatch::Native;
                if(const auto facts = request.Find("facts"); facts && facts->kind == Json::Kind::Object && SuppliedNativeFact(function))
                    for(const auto& fact : facts->object)
                        if(MatchesIdent(function.Id, fact.first)) { supplied = NativeValue(fact.second).move(); return PreviewPolicy::Dispatch::Supplied; }
                if((function.Id[0] == IDENT_SYS || function.Id[0] == IDENT_SYSTEM) && function.Id[1] == IDENT_VAR && function.Arguments.size() == 1)
                {
                    const auto name = context.Eval(function.Arguments[0]).to_string();
                    if(!policy.failed && Environment(std::wstring(name.c_str()), supplied)) return PreviewPolicy::Dispatch::Supplied;
                }
                // Only specific read operations can receive broker responses.
                const bool read = (function.Id[0] == IDENT_IO && function.Id[1] == IDENT_FILE &&
                    (function.Id[2] == IDENT_EXISTS || function.Id[2] == IDENT_READ) && function.Id[3] == 0) ||
                    (function.Id[0] == IDENT_REG && (function.Id[1] == IDENT_GET || function.Id[1] == IDENT_EXISTS || function.Id[1] == 0));
                if(read)
                {
                    std::string arguments = "[";
                    for(std::size_t i = 0; i < function.Arguments.size(); ++i)
                    { if(i) arguments += ','; arguments += ValueJson(context.Eval(function.Arguments[i])); }
                    arguments += ']';
                    if(!policy.failed)
                        if(const auto reads = request.Find("reads"); reads && reads->kind == Json::Kind::Array)
                            for(const auto& item : reads->array)
                                if(MatchesReadFunction(function.Id, Json::StringMember(item, "function")))
                                    if(const auto args = item.Find("arguments"); args && Json::ToJson(*args) == arguments)
                                        if(const auto value = item.Find("value")) { supplied = NativeValue(*value).move(); return PreviewPolicy::Dispatch::Supplied; }
                }
                return PreviewPolicy::Dispatch::Unavailable;
            };
            policy.environment = [&](const std::wstring& name, Object& value) { return Environment(name, value); };
            root = Wide(Json::StringMember(request, "rootPath"));
            if(root.empty()) root = L"<studio>";
            if(const auto files = request.Find("documents"); files && files->kind == Json::Kind::Array)
            {
                if(files->array.size() > 256) throw std::invalid_argument("Too many workspace documents.");
                for(const auto& file : files->array)
                {
                    Source source{Wide(Json::StringMember(file, "path")), Wide(Json::StringMember(file, "text"))};
                    if(source.path.empty() || source.text.find(L'\0') != std::wstring::npos) throw std::invalid_argument("Invalid workspace document.");
                    if(!documents.emplace(CanonicalPath(source.path), source).second) throw std::invalid_argument("Duplicate workspace document.");
                }
            }
            if(documents.empty()) documents.emplace(CanonicalPath(root), Source{root, Wide(Json::StringMember(request, "source"))});
            auto source = documents.find(CanonicalPath(root));
            if(source == documents.end()) throw std::invalid_argument("The root document is absent from the workspace snapshot.");
            parser = std::make_unique<Parser>(Parser::PreviewInput{
                {source->second.text, source->second.path, false}, &policy,
                [&](const std::wstring& parent, const std::wstring& imported, std::wstring& path, std::wstring& text)
                {
                    auto candidate = std::filesystem::path(imported);
                    if(!candidate.is_absolute()) candidate = std::filesystem::path(parent).parent_path() / candidate;
                    auto found = documents.find(CanonicalPath(candidate.wstring()));
                    if(found == documents.end()) return false;
                    path = found->second.path; text = found->second.text; return true;
                },
                [&](std::wstring_view file, std::size_t position)
                {
                    const auto at = request.Find("resolveAt");
                    if(!at || at->kind != Json::Kind::Object ||
                        CanonicalPath(std::wstring(file)) != CanonicalPath(Wide(Json::StringMember(*at, "filePath"))) ||
                        position != static_cast<std::size_t>(NumberMember(*at, "position", -1))) return false;
                    const auto occurrence = static_cast<std::size_t>(NumberMember(*at, "occurrenceIndex", 0));
                    if(queryOccurrence++ != occurrence) return false;
                    queryResult = EvaluateCurrent();
                    return true;
                }});
            if(const auto facts = request.Find("context"); facts && facts->kind == Json::Kind::Object)
            {
                const auto requestedDpi = NumberMember(*facts, "dpi", 96);
                if(requestedDpi < 48 || requestedDpi > 768) throw std::invalid_argument("Preview DPI is outside its supported range.");
                dpi.val = static_cast<uint32_t>(requestedDpi);
                theme.mode = static_cast<uint8_t>(NumberMember(*facts, "themeMode", 0));
                if(facts->Find("dpi")) parser->context.dpi = &dpi;
                if(facts->Find("themeMode")) parser->context.theme = &theme;
            }
            theme.dpi = &dpi;
            if(selection.available) parser->context.Selections = &selection.value;
            theme.systemUsesLightTheme = theme.mode == 0;
            theme.appsUseLightTheme = theme.mode == 0;
        }

        bool Environment(const std::wstring& name, Object& value)
        {
            if(const auto supplied = request.Find("environment"); supplied && supplied->kind == Json::Kind::Object)
                for(const auto& entry : supplied->object)
                    if(string(name).equals(Wide(entry.first).c_str())) { value = NativeValue(entry.second).move(); return true; }
            return false;
        }

        bool Load()
        {
            const auto loaded = parser->Load();
            for(const auto& diagnostic : parser->StudioSyntax().diagnostics)
                // The lossless tree's dynamic-import warning is superseded by
                // actual restricted import resolution in this session.
                if(diagnostic.severity == "error" && queryResult.empty() && (!loaded || diagnostic.code == "LANG_LIMIT"))
                    diagnostics.push_back(DiagnosticJson(diagnostic.code, diagnostic.message));
            RecordFailure();
            return loaded && !policy.failed && diagnostics.empty();
        }

        void RecordFailure(std::string_view file = {}, int position = 0)
        {
            if(policy.failed) diagnostics.push_back(DiagnosticJson(policy.code, Utf8(policy.message),
                unavailableSource ? Utf8(unavailableSource->file) : std::string(file),
                unavailableSource ? static_cast<int>(unavailableSource->start) : position));
        }

        std::string Diagnostics() const
        {
            std::string result = "[";
            for(std::size_t i = 0; i < diagnostics.size(); ++i) { if(i) result += ','; result += diagnostics[i]; }
            return result + ']';
        }

        std::string Dependencies() const
        {
            std::string result = "[";
            for(const auto& [key, source] : documents)
            {
                if(result.size() > 1) result += ',';
                result += "{\"path\":" + Json::Quote(Utf8(source.path)) + ",\"sha256Utf8\":" + Json::Quote(Fingerprint(Utf8(source.text))) + '}';
            }
            return result + ']';
        }

        std::string Trace() const
        {
            std::string result = "[";
            for(std::size_t i = 0; i < trace.size(); ++i) { if(i) result += ','; result += trace[i]; }
            return result + ']';
        }

        std::string Evaluate()
        {
            if(!Load()) return "{\"version\":1,\"available\":false,\"diagnostics\":" + Diagnostics() + '}';
            if(request.Find("resolveAt"))
                return queryResult.empty() ? FailureJson("PREVIEW_SCOPE", "The requested declaration occurrence was not reached.") : queryResult;
            const auto file = Wide(Json::StringMember(request, "filePath"));
            const auto position = static_cast<std::size_t>((std::max)(0.0, NumberMember(request, "position", 0)));
            parser->context.variables.local = parser->ScopeAt(file.empty() ? root : file, position);
            return EvaluateCurrent();
        }

        std::string EvaluateCurrent()
        {
            const auto file = Json::StringMember(request, "filePath");
            const auto position = static_cast<int>((std::max)(0.0, NumberMember(request, "position", 0)));
            auto expression = parser->ParseExpression(Wide(Json::StringMember(request, "expression")));
            if(!expression)
            {
                diagnostics.push_back(DiagnosticJson("PREVIEW_PARSE", "The preview expression could not be parsed.", file, position));
                return "{\"version\":1,\"available\":false,\"diagnostics\":" + Diagnostics() + '}';
            }
            Object value = parser->context.Eval(expression.get()).move();
            RecordFailure(file, position);
            if(policy.failed) return "{\"version\":1,\"available\":false,\"diagnostics\":" + Diagnostics() + '}';
            const char* type = value.is_color() ? "color" : value.is_array(true) ? "array" : value.is_number() ? "number" : value.is_null() ? "null" : "string";
            return "{\"version\":1,\"available\":true,\"valueType\":" + Json::Quote(type) +
                ",\"value\":" + ValueJson(value) + ",\"dependencies\":" + Dependencies() + ",\"trace\":" + Trace() + ",\"diagnostics\":" + Diagnostics() + '}';
        }
    };

    std::string Render(Session& session, bool composed);

    inline std::string Process(std::wstring_view json)
    {
        Json::Value request;
        std::string error;
        Json::Limits limits;
        limits.maxStringBytes = 12u * 1024u * 1024u;
        if(!Json::Parse(Utf8(json), request, error, limits)) return FailureJson("PREVIEW_INPUT", error);
        const auto operation = Json::StringMember(request, "operation");
        if(operation == "capabilities")
            return "{\"version\":1,\"available\":true,\"analysis\":true,\"evaluation\":true,\"network\":false,\"mutation\":false,\"diagnostics\":[]}";
        Session session(request);
        if(operation == "render" || operation == "compose") return Render(session, operation == "compose");
        if(operation == "evaluate" || operation == "resolve") return session.Evaluate();
        if(operation == "analyze")
        {
            const bool valid = session.Load();
            return "{\"version\":1,\"available\":" + std::string(valid ? "true" : "false") + ",\"dependencies\":" + session.Dependencies() + ",\"diagnostics\":" + session.Diagnostics() + '}';
        }
        return FailureJson("PREVIEW_OPERATION", "This preview operation is not available in this native build.");
    }
}
