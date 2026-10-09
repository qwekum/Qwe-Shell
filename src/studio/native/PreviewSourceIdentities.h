#pragma once

#include "PreviewService.h"
#include <map>
#include <limits>

namespace Nilesoft::Shell::StudioPreview
{
    // Authoring identity is supplied alongside the exact unsaved source revision.
    // Offsets are lookup keys for this request only, never persistent UI identities.
    class PreviewSourceIdentities
    {
        std::map<std::pair<std::wstring, int>, std::wstring> values;
    public:
        explicit PreviewSourceIdentities(const Json::Value& request)
        {
            const auto entries = request.Find("sourceIdentities");
            if(!entries) return;
            if(entries->kind != Json::Kind::Array || entries->array.size() > 32768)
                throw std::invalid_argument("The preview source identity table exceeds its limit.");
            for(const auto& entry : entries->array)
            {
                const auto file = entry.Find("filePath"), start = entry.Find("start"), id = entry.Find("id");
                if(!file || file->kind != Json::Kind::String || file->text.empty() || file->text.size() > 32768 ||
                    !start || start->kind != Json::Kind::Number || !id || id->kind != Json::Kind::String ||
                    id->text.empty() || id->text.size() > 256 || file->text.find('\0') != std::string::npos || id->text.find('\0') != std::string::npos)
                    throw std::invalid_argument("A preview source identity is invalid.");
                const double offset = NumberMember(entry, "start", -1);
                if(offset < 0 || offset > (std::numeric_limits<int>::max)() || offset != std::floor(offset))
                    throw std::invalid_argument("A preview source identity offset is invalid.");
                auto key = std::make_pair(CanonicalPath(Wide(file->text)), static_cast<int>(offset));
                if(!values.emplace(std::move(key), Wide(id->text)).second)
                    throw std::invalid_argument("A preview source identity occurs more than once.");
            }
        }

        std::wstring Resolve(std::wstring_view file, int start) const
        {
            const auto found = values.find({CanonicalPath(std::wstring(file)), start});
            return found == values.end() ? std::wstring{} : found->second;
        }
    };
}
