#pragma once

// A deliberately small JSON reader used only at the preview worker seam.
//
// The worker receives data from a managed process and passes a request to the
// native language DLL.  Keeping this reader local to the worker makes the
// limits and validation rules visible at that seam; it also avoids adding a
// general-purpose JSON dependency to the native Shell build.  Values retain
// their JSON number spelling and object member order.  The reader is strict:
// comments, trailing commas, invalid UTF-8, duplicate object members, and
// unpaired UTF-16 surrogates are rejected.

#include <cstddef>
#include <cstdint>
#include <new>
#include <string>
#include <string_view>
#include <unordered_set>
#include <utility>
#include <vector>

namespace ShellStudio::PreviewJson
{
	struct Limits
	{
		std::size_t maxBytes = 16u * 1024u * 1024u;
		std::size_t maxDepth = 48;
		std::size_t maxValues = 131072;
		std::size_t maxMembers = 32768;
		std::size_t maxStringBytes = 1024u * 1024u;
	};

	enum class Kind
	{
		Null,
		Boolean,
		Number,
		String,
		Array,
		Object,
	};

	struct Value
	{
		Kind kind = Kind::Null;
		bool boolean = false;
		// Strings contain decoded UTF-8. Numbers retain their validated JSON
		// spelling so the native language service can distinguish 1 from 1.0.
		std::string text;
		std::vector<Value> array;
		std::vector<std::pair<std::string, Value>> object;

		Value() = default;
		explicit Value(Kind valueKind) : kind(valueKind) {}

		Value(const Value&) = default;
		Value(Value&&) noexcept = default;
		Value& operator=(const Value&) = default;
		Value& operator=(Value&&) noexcept = default;

		const Value* Find(std::string_view name) const noexcept
		{
			if (kind != Kind::Object) return nullptr;
			for (const auto& member : object)
				if (member.first == name) return &member.second;
			return nullptr;
		}
	};

	class Parser
	{
	public:
		Parser(std::string_view input, Limits limits = {}) : input_(input), limits_(limits) {}

		bool Parse(Value& result, std::string& error) noexcept
		{
			try
			{
				if (input_.size() > limits_.maxBytes)
					return Fail(error, "JSON exceeds the worker message limit.");
				position_ = 0;
				values_ = 0;
				if (!ParseValue(result, 0, error)) return false;
				SkipWhitespace();
				if (position_ != input_.size()) return Fail(error, "JSON has trailing data.");
				return true;
			}
			catch (const std::bad_alloc&)
			{
				return Fail(error, "JSON exceeds the worker memory limit.");
			}
			catch (...)
			{
				return Fail(error, "JSON validation failed.");
			}
		}

	private:
		std::string_view input_;
		Limits limits_;
		std::size_t position_ = 0;
		std::size_t values_ = 0;

		static bool Fail(std::string& error, const char* message) noexcept
		{
			try { error = message; } catch (...) { }
			return false;
		}

		void SkipWhitespace() noexcept
		{
			while (position_ < input_.size())
			{
				const char c = input_[position_];
				if (c != ' ' && c != '\t' && c != '\r' && c != '\n') return;
				++position_;
			}
		}

		bool ParseValue(Value& result, std::size_t depth, std::string& error)
		{
			if (depth > limits_.maxDepth) return Fail(error, "JSON nesting exceeds the worker limit.");
			if (++values_ > limits_.maxValues) return Fail(error, "JSON contains too many values.");
			SkipWhitespace();
			if (position_ >= input_.size()) return Fail(error, "JSON ended before a value.");
			switch (input_[position_])
			{
			case 'n': return ParseLiteral(result, "null", Kind::Null, false, error);
			case 't': return ParseLiteral(result, "true", Kind::Boolean, true, error);
			case 'f': return ParseLiteral(result, "false", Kind::Boolean, false, error);
			case '"':
				result = Value(Kind::String);
				return ParseString(result.text, error);
			case '[': return ParseArray(result, depth, error);
			case '{': return ParseObject(result, depth, error);
			default:
				if (input_[position_] == '-' || (input_[position_] >= '0' && input_[position_] <= '9'))
				{
					result = Value(Kind::Number);
					return ParseNumber(result.text, error);
				}
				return Fail(error, "JSON contains an invalid value.");
			}
		}

		bool ParseLiteral(Value& result, std::string_view literal, Kind kind, bool boolean, std::string& error)
		{
			if (input_.substr(position_, literal.size()) != literal)
				return Fail(error, "JSON contains an invalid literal.");
			position_ += literal.size();
			result = Value(kind);
			result.boolean = boolean;
			return true;
		}

		bool ParseArray(Value& result, std::size_t depth, std::string& error)
		{
			result = Value(Kind::Array);
			++position_; // [
			SkipWhitespace();
			if (position_ < input_.size() && input_[position_] == ']') { ++position_; return true; }
			for (std::size_t count = 0;; ++count)
			{
				if (count >= limits_.maxMembers) return Fail(error, "JSON array contains too many values.");
				Value value;
				if (!ParseValue(value, depth + 1, error)) return false;
				result.array.emplace_back(std::move(value));
				SkipWhitespace();
				if (position_ >= input_.size()) return Fail(error, "JSON array is not terminated.");
				if (input_[position_] == ']') { ++position_; return true; }
				if (input_[position_] != ',') return Fail(error, "JSON array requires a comma.");
				++position_;
				SkipWhitespace();
				if (position_ < input_.size() && input_[position_] == ']') return Fail(error, "JSON does not allow trailing commas.");
			}
		}

		bool ParseObject(Value& result, std::size_t depth, std::string& error)
		{
			result = Value(Kind::Object);
			std::unordered_set<std::string> names;
			++position_; // {
			SkipWhitespace();
			if (position_ < input_.size() && input_[position_] == '}') { ++position_; return true; }
			for (std::size_t count = 0;; ++count)
			{
				if (count >= limits_.maxMembers) return Fail(error, "JSON object contains too many members.");
				if (position_ >= input_.size() || input_[position_] != '"') return Fail(error, "JSON object requires a quoted member name.");
				std::string name;
				if (!ParseString(name, error)) return false;
				if (!names.emplace(name).second) return Fail(error, "JSON object contains a duplicate member.");
				SkipWhitespace();
				if (position_ >= input_.size() || input_[position_] != ':') return Fail(error, "JSON object requires a colon.");
				++position_;
				Value value;
				if (!ParseValue(value, depth + 1, error)) return false;
				result.object.emplace_back(std::move(name), std::move(value));
				SkipWhitespace();
				if (position_ >= input_.size()) return Fail(error, "JSON object is not terminated.");
				if (input_[position_] == '}') { ++position_; return true; }
				if (input_[position_] != ',') return Fail(error, "JSON object requires a comma.");
				++position_;
				SkipWhitespace();
				if (position_ < input_.size() && input_[position_] == '}') return Fail(error, "JSON does not allow trailing commas.");
			}
		}

		static int Hex(char c) noexcept
		{
			if (c >= '0' && c <= '9') return c - '0';
			if (c >= 'a' && c <= 'f') return c - 'a' + 10;
			if (c >= 'A' && c <= 'F') return c - 'A' + 10;
			return -1;
		}

		bool ParseHexQuad(std::uint32_t& result, std::string& error)
		{
			if (input_.size() - position_ < 4) return Fail(error, "JSON has an incomplete Unicode escape.");
			result = 0;
			for (int index = 0; index < 4; ++index)
			{
				const int value = Hex(input_[position_++]);
				if (value < 0) return Fail(error, "JSON contains an invalid Unicode escape.");
				result = (result << 4) | static_cast<std::uint32_t>(value);
			}
			return true;
		}

		bool AppendCodePoint(std::string& result, std::uint32_t codePoint, std::string& error)
		{
			if (codePoint > 0x10ffff || (codePoint >= 0xd800 && codePoint <= 0xdfff))
				return Fail(error, "JSON contains an invalid Unicode code point.");
			if (codePoint <= 0x7f) result.push_back(static_cast<char>(codePoint));
			else if (codePoint <= 0x7ff)
			{
				result.push_back(static_cast<char>(0xc0 | (codePoint >> 6)));
				result.push_back(static_cast<char>(0x80 | (codePoint & 0x3f)));
			}
			else if (codePoint <= 0xffff)
			{
				result.push_back(static_cast<char>(0xe0 | (codePoint >> 12)));
				result.push_back(static_cast<char>(0x80 | ((codePoint >> 6) & 0x3f)));
				result.push_back(static_cast<char>(0x80 | (codePoint & 0x3f)));
			}
			else
			{
				result.push_back(static_cast<char>(0xf0 | (codePoint >> 18)));
				result.push_back(static_cast<char>(0x80 | ((codePoint >> 12) & 0x3f)));
				result.push_back(static_cast<char>(0x80 | ((codePoint >> 6) & 0x3f)));
				result.push_back(static_cast<char>(0x80 | (codePoint & 0x3f)));
			}
			if (result.size() > limits_.maxStringBytes) return Fail(error, "JSON string exceeds the worker limit.");
			return true;
		}

		bool ParseString(std::string& result, std::string& error)
		{
			if (position_ >= input_.size() || input_[position_] != '"') return Fail(error, "JSON requires a quoted string.");
			++position_;
			result.clear();
			while (position_ < input_.size())
			{
				const unsigned char c = static_cast<unsigned char>(input_[position_++]);
				if (c == '"') return true;
				if (c == '\\')
				{
					if (position_ >= input_.size()) return Fail(error, "JSON has an incomplete string escape.");
					const char escape = input_[position_++];
					switch (escape)
					{
					case '"': result.push_back('"'); break;
					case '\\': result.push_back('\\'); break;
					case '/': result.push_back('/'); break;
					case 'b': result.push_back('\b'); break;
					case 'f': result.push_back('\f'); break;
					case 'n': result.push_back('\n'); break;
					case 'r': result.push_back('\r'); break;
					case 't': result.push_back('\t'); break;
					case 'u':
					{
						std::uint32_t codePoint = 0;
						if (!ParseHexQuad(codePoint, error)) return false;
						if (codePoint >= 0xd800 && codePoint <= 0xdbff)
						{
							if (input_.size() - position_ < 6 || input_[position_] != '\\' || input_[position_ + 1] != 'u')
								return Fail(error, "JSON contains an unpaired Unicode surrogate.");
							position_ += 2;
							std::uint32_t low = 0;
							if (!ParseHexQuad(low, error) || low < 0xdc00 || low > 0xdfff)
								return Fail(error, "JSON contains an invalid Unicode surrogate pair.");
							codePoint = 0x10000 + ((codePoint - 0xd800) << 10) + (low - 0xdc00);
						}
						else if (codePoint >= 0xdc00 && codePoint <= 0xdfff)
							return Fail(error, "JSON contains an unpaired Unicode surrogate.");
						if (!AppendCodePoint(result, codePoint, error)) return false;
						break;
					}
					default: return Fail(error, "JSON contains an invalid string escape.");
					}
					if (result.size() > limits_.maxStringBytes) return Fail(error, "JSON string exceeds the worker limit.");
					continue;
				}
				if (c < 0x20) return Fail(error, "JSON string contains an unescaped control character.");
				if (c < 0x80) result.push_back(static_cast<char>(c));
				else
				{
					std::uint32_t codePoint = 0;
					std::size_t needed = 0;
					if (c >= 0xc2 && c <= 0xdf) { codePoint = c & 0x1f; needed = 1; }
					else if (c >= 0xe0 && c <= 0xef) { codePoint = c & 0x0f; needed = 2; }
					else if (c >= 0xf0 && c <= 0xf4) { codePoint = c & 0x07; needed = 3; }
					else return Fail(error, "JSON string contains invalid UTF-8.");
					if (input_.size() - position_ < needed) return Fail(error, "JSON string ends in invalid UTF-8.");
					for (std::size_t index = 0; index < needed; ++index)
					{
						const unsigned char continuation = static_cast<unsigned char>(input_[position_++]);
						if ((continuation & 0xc0) != 0x80) return Fail(error, "JSON string contains invalid UTF-8.");
						codePoint = (codePoint << 6) | (continuation & 0x3f);
					}
					if ((needed == 1 && codePoint < 0x80) || (needed == 2 && codePoint < 0x800) ||
						(needed == 3 && codePoint < 0x10000) || (codePoint >= 0xd800 && codePoint <= 0xdfff) || codePoint > 0x10ffff)
						return Fail(error, "JSON string contains a non-canonical UTF-8 code point.");
					if (!AppendCodePoint(result, codePoint, error)) return false;
				}
				if (result.size() > limits_.maxStringBytes) return Fail(error, "JSON string exceeds the worker limit.");
			}
			return Fail(error, "JSON string is not terminated.");
		}

		bool ParseNumber(std::string& result, std::string& error)
		{
			const std::size_t start = position_;
			if (position_ < input_.size() && input_[position_] == '-') ++position_;
			if (position_ >= input_.size()) return Fail(error, "JSON number is incomplete.");
			if (input_[position_] == '0') ++position_;
			else if (input_[position_] >= '1' && input_[position_] <= '9')
			{
				while (position_ < input_.size() && input_[position_] >= '0' && input_[position_] <= '9') ++position_;
			}
			else return Fail(error, "JSON number has an invalid integer part.");
			if (position_ < input_.size() && input_[position_] == '.')
			{
				++position_;
				const std::size_t fraction = position_;
				while (position_ < input_.size() && input_[position_] >= '0' && input_[position_] <= '9') ++position_;
				if (fraction == position_) return Fail(error, "JSON number has an empty fraction.");
			}
			if (position_ < input_.size() && (input_[position_] == 'e' || input_[position_] == 'E'))
			{
				++position_;
				if (position_ < input_.size() && (input_[position_] == '+' || input_[position_] == '-')) ++position_;
				const std::size_t exponent = position_;
				while (position_ < input_.size() && input_[position_] >= '0' && input_[position_] <= '9') ++position_;
				if (exponent == position_) return Fail(error, "JSON number has an empty exponent.");
			}
			result.assign(input_.substr(start, position_ - start));
			return true;
		}
	};

	inline bool Parse(std::string_view input, Value& result, std::string& error, Limits limits = {}) noexcept
	{
		return Parser(input, limits).Parse(result, error);
	}

	inline std::string Quote(std::string_view value)
	{
		std::string result;
		result.reserve(value.size() + 2);
		result.push_back('"');
		for (unsigned char c : value)
		{
			switch (c)
			{
			case '"': result += "\\\""; break;
			case '\\': result += "\\\\"; break;
			case '\b': result += "\\b"; break;
			case '\f': result += "\\f"; break;
			case '\n': result += "\\n"; break;
			case '\r': result += "\\r"; break;
			case '\t': result += "\\t"; break;
			default:
				if (c < 0x20)
				{
					static constexpr char digits[] = "0123456789abcdef";
					result += "\\u00";
					result.push_back(digits[c >> 4]);
					result.push_back(digits[c & 0x0f]);
				}
				else result.push_back(static_cast<char>(c));
				break;
			}
		}
		result.push_back('"');
		return result;
	}

	inline std::string StringMember(const Value& object, std::string_view name)
	{
		const auto* value = object.Find(name);
		return value && value->kind == Kind::String ? value->text : std::string();
	}

	inline bool IsString(const Value& object, std::string_view name) noexcept
	{
		const auto* value = object.Find(name);
		return value && value->kind == Kind::String;
	}

	inline bool IsObject(const Value& object, std::string_view name) noexcept
	{
		const auto* value = object.Find(name);
		return value && value->kind == Kind::Object;
	}

	inline void AppendJson(std::string& result, const Value& value)
	{
		switch (value.kind)
		{
		case Kind::Null: result += "null"; break;
		case Kind::Boolean: result += value.boolean ? "true" : "false"; break;
		case Kind::Number: result += value.text; break;
		case Kind::String: result += Quote(value.text); break;
		case Kind::Array:
			result.push_back('[');
			for (std::size_t index = 0; index < value.array.size(); ++index)
			{
				if (index != 0) result.push_back(',');
				AppendJson(result, value.array[index]);
			}
			result.push_back(']');
			break;
		case Kind::Object:
			result.push_back('{');
			for (std::size_t index = 0; index < value.object.size(); ++index)
			{
				if (index != 0) result.push_back(',');
				result += Quote(value.object[index].first);
				result.push_back(':');
				AppendJson(result, value.object[index].second);
			}
			result.push_back('}');
			break;
		}
	}

	inline std::string ToJson(const Value& value)
	{
		std::string result;
		AppendJson(result, value);
		return result;
	}
}
