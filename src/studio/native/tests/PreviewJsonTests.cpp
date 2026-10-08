#ifdef NDEBUG
#undef NDEBUG
#endif
#include "../PreviewJson.h"

#include <cassert>
#include <iostream>
#include <string>

using ShellStudio::PreviewJson::Kind;
using ShellStudio::PreviewJson::Value;

int main()
{
    Value value;
    std::string error;
    assert(ShellStudio::PreviewJson::Parse(R"({"name":"\u732b \ud83d\ude00","number":1.25e+3,"ok":true})", value, error));
    assert(value.kind == Kind::Object);
    assert(value.Find("name") && value.Find("name")->text == "猫 😀");
    assert(value.Find("number") && value.Find("number")->text == "1.25e+3");
    assert(ShellStudio::PreviewJson::ToJson(value).find("1.25e+3") != std::string::npos);

    for (const auto input : {
        std::string(R"({"a":1,"a":2})"),
        std::string(R"([1,])"),
        std::string(R"({"a":01})"),
        std::string(R"({"a":"\ud800"})"),
        std::string(R"({"a":"\xc0\x80"})") })
    {
        value = {};
        error.clear();
        assert(!ShellStudio::PreviewJson::Parse(input, value, error));
        assert(!error.empty());
    }

    std::string nested = "{";
    for (int index = 0; index < 64; ++index) nested += "\"x\":{";
    nested += "\"value\":true";
    for (int index = 0; index < 64; ++index) nested += "}";
    nested += "}";
    assert(!ShellStudio::PreviewJson::Parse(nested, value, error));

    const auto quoted = ShellStudio::PreviewJson::Quote("quote \" and line\n");
    assert(quoted == "\"quote \\\" and line\\n\"");
    std::cout << "ShellStudio preview JSON tests passed\n";
    return 0;
}
