#include <pch.h>

#include "NativeExpressionSyntax.h"

#include <algorithm>
#include <climits>
#include <cwctype>
#include <limits>
#include <string>
#include <utility>
#include <vector>

namespace Nilesoft::Shell::StudioLanguage
{
	namespace
	{
		using NativeExpression = ::Nilesoft::Shell::Expression;
		using NativeArray = ::Nilesoft::Shell::ArrayExpression;
		using NativeAssign = ::Nilesoft::Shell::AssignExpression;
		using NativeBinary = ::Nilesoft::Shell::BinaryExpression;
		using NativeFor = ::Nilesoft::Shell::ForStatement;
		using NativeFunc = ::Nilesoft::Shell::FuncExpression;
		using NativePreviewEnvironment = ::Nilesoft::Shell::PreviewEnvironmentExpression;
		using NativeStatement = ::Nilesoft::Shell::StatementExpression;
		using NativeTernary = ::Nilesoft::Shell::TernaryExpression;
		using NativeUnary = ::Nilesoft::Shell::UnaryExpression;
		using NativeVariable = ::Nilesoft::Shell::VariableExpression;

		constexpr std::size_t Invalid = (std::numeric_limits<std::size_t>::max)();
		constexpr std::size_t MaxProjectionNodes = FrontendLimits::MaxExpressions;
		constexpr std::size_t MaxProjectionDepth = FrontendLimits::MaxRecursionDepth;
		constexpr std::size_t MaxArrayElements = 4096;

		struct Span final
		{
			std::size_t start = 0;
			std::size_t end = 0;
			bool valid = false;

			static Span From(std::size_t begin, std::size_t finish)
			{
				return finish >= begin ? Span{ begin, finish, true } : Span{};
			}
		};

		Span Union(Span left, Span right)
		{
			if(!left.valid) return right;
			if(!right.valid) return left;
			return Span::From((std::min)(left.start, right.start), (std::max)(left.end, right.end));
		}

		bool IsIdentifierStart(wchar_t value)
		{
			return value == L'_' || std::iswalpha(static_cast<wint_t>(value)) != 0;
		}

		bool IsIdentifierPart(wchar_t value)
		{
			return value == L'_' || value == L'-' || std::iswalnum(static_cast<wint_t>(value)) != 0;
		}

		bool IsWhitespace(wchar_t value)
		{
			return std::iswspace(static_cast<wint_t>(value)) != 0;
		}

		bool IsOpen(wchar_t value)
		{
			return value == L'(' || value == L'[' || value == L'{';
		}

		bool IsClose(wchar_t value)
		{
			return value == L')' || value == L']' || value == L'}';
		}

		char32_t CodePoint(std::wstring_view source, std::size_t& position, std::size_t end)
		{
			const auto value = static_cast<std::uint32_t>(source[position++]);
			if(value >= 0xD800u && value <= 0xDBFFu && position < end)
			{
				const auto low = static_cast<std::uint32_t>(source[position]);
				if(low >= 0xDC00u && low <= 0xDFFFu)
				{
					++position;
					return static_cast<char32_t>(0x10000u + ((value - 0xD800u) << 10) + low - 0xDC00u);
				}
				return 0xFFFD;
			}
			if(value >= 0xDC00u && value <= 0xDFFFu) return 0xFFFD;
			return static_cast<char32_t>(value);
		}

		void AppendUtf8(std::string& output, char32_t value)
		{
			if(value <= 0x7Fu) output.push_back(static_cast<char>(value));
			else if(value <= 0x7FFu)
			{
				output.push_back(static_cast<char>(0xC0u | (value >> 6)));
				output.push_back(static_cast<char>(0x80u | (value & 0x3Fu)));
			}
			else if(value <= 0xFFFFu)
			{
				output.push_back(static_cast<char>(0xE0u | (value >> 12)));
				output.push_back(static_cast<char>(0x80u | ((value >> 6) & 0x3Fu)));
				output.push_back(static_cast<char>(0x80u | (value & 0x3Fu)));
			}
			else
			{
				output.push_back(static_cast<char>(0xF0u | (value >> 18)));
				output.push_back(static_cast<char>(0x80u | ((value >> 12) & 0x3Fu)));
				output.push_back(static_cast<char>(0x80u | ((value >> 6) & 0x3Fu)));
				output.push_back(static_cast<char>(0x80u | (value & 0x3Fu)));
			}
		}

		std::string NativeStringUtf8(const ::Nilesoft::Text::string& value)
		{
			const auto length = value.length<std::size_t>();
			const auto* data = value.c_str();
			if(!data || length == 0) return {};
			const std::wstring_view source(data, length);
			std::string result;
			result.reserve(length);
			std::size_t position = 0;
			while(position < source.size())
				AppendUtf8(result, CodePoint(source, position, source.size()));
			return result;
		}

		class Projector final
		{
			std::wstring_view source_;
			const ExpressionSources& sources_;
			std::size_t nextId_ = 1;
			std::size_t nodeCount_ = 0;
			std::size_t depth_ = 0;

		public:
			Projector(std::wstring_view source, const ExpressionSources& sources)
				: source_(source), sources_(sources) {}

			ExpressionNode Project(const NativeExpression* expression)
			{
				return Project(expression, Span{}, true);
			}

		private:
			std::string NewId()
			{
				return "native-expression-" + std::to_string(nextId_++);
			}

			Span Clamp(Span span) const
			{
				if(!span.valid || span.start > source_.size()) return {};
				span.end = (std::min)(span.end, source_.size());
				if(span.end < span.start) return {};
				return span;
			}

			Span Lookup(const NativeExpression* expression) const
			{
				if(!expression) return {};
				auto it = sources_.find(expression);
				if(it == sources_.end()) return {};
				if(it->second.start > (std::numeric_limits<std::size_t>::max)() - it->second.length) return {};
				return Clamp(Span::From(it->second.start, it->second.start + it->second.length));
			}

			Span ChildrenSpan(const NativeExpression* expression) const
			{
				Span result;
				if(auto value = dynamic_cast<const NativeBinary*>(expression))
					return Union(Lookup(value->Left), Lookup(value->Right));
				if(auto value = dynamic_cast<const NativeUnary*>(expression)) return Lookup(value->Operand);
				if(auto value = dynamic_cast<const NativeTernary*>(expression))
				{
					result = Union(result, Lookup(value->Condition));
					result = Union(result, Lookup(value->True));
					return Union(result, Lookup(value->False));
				}
				if(auto value = dynamic_cast<const NativeAssign*>(expression))
					return Lookup(value->Right);
				if(auto value = dynamic_cast<const NativeStatement*>(expression))
				{
					for(auto child : value->Body) result = Union(result, Lookup(child));
					return result;
				}
				if(auto value = dynamic_cast<const NativeArray*>(expression))
				{
					for(std::size_t index = 0; index < MaxArrayElements; ++index)
					{
						auto child = const_cast<NativeArray*>(value)->get(index);
						if(!child) break;
						result = Union(result, Lookup(child));
					}
					return result;
				}
				if(auto value = dynamic_cast<const NativeFunc*>(expression))
				{
					for(auto child : value->Arguments) result = Union(result, Lookup(child));
					result = Union(result, Lookup(value->Array));
					return Union(result, Lookup(value->Child));
				}
				if(auto value = dynamic_cast<const NativeFor*>(expression))
				{
					result = Union(result, Lookup(value->Condition));
					result = Union(result, Lookup(value->Iterator));
					result = Union(result, Lookup(value->Body1));
					return Union(result, Lookup(value->Body2));
				}
				return result;
			}

			Span SpanFor(const NativeExpression* expression, Span forced) const
			{
			if(forced.valid) return Clamp(forced);
			return Clamp(Union(Lookup(expression), ChildrenSpan(expression)));
			}

			std::string Utf8(Span span) const
			{
				span = Clamp(span);
				if(!span.valid || span.end <= span.start) return {};
				std::string result;
				result.reserve(span.end - span.start);
				std::size_t position = span.start;
				while(position < span.end) AppendUtf8(result, CodePoint(source_, position, span.end));
				return result;
			}

			ExpressionNode Make(std::string kind, Span span, std::string text = {})
			{
				span = Clamp(span);
				ExpressionNode result;
				result.id = NewId();
				result.kind = std::move(kind);
				result.text = text.empty() ? Utf8(span) : std::move(text);
				if(span.valid)
				{
					result.start = static_cast<int>((std::min)(span.start, static_cast<std::size_t>(INT_MAX)));
					const auto length = span.end - span.start;
					result.length = static_cast<int>((std::min)(length, static_cast<std::size_t>(INT_MAX)));
				}
				return result;
			}

			ExpressionNode LimitNode(Span span)
			{
				return Make("unknown", span, Utf8(span));
			}

			ExpressionNode Project(const NativeExpression* expression, Span forced, bool allowGroup)
			{
				if(!expression) return {};
				const auto span = SpanFor(expression, forced);
				// The native bracket parser returns the inner expression and records
				// the bracket span against that same object.  Reify the source
				// grouping at the DTO boundary, then project the same native object
				// against its inner span so its children retain their own locations.
				if(allowGroup && span.valid && span.start < span.end && source_[span.start] == L'(')
				{
					const auto close = Matching(span.start, span.end);
					if(close == span.end - 1)
					{
						if(depth_ >= MaxProjectionDepth || nodeCount_ >= MaxProjectionNodes)
							return LimitNode(span);
						++depth_;
						++nodeCount_;
						ExpressionNode group = Make("group", span);
						Append(group, Project(expression, Span::From(span.start + 1, close), false));
						--depth_;
						return group;
					}
				}
				if(depth_ >= MaxProjectionDepth || nodeCount_ >= MaxProjectionNodes)
					return LimitNode(span);
				++depth_;
				++nodeCount_;

				ExpressionNode result;
				if(auto environment = dynamic_cast<const NativePreviewEnvironment*>(expression))
					result = Make("environment", span);
				else if(auto string = dynamic_cast<const ::Nilesoft::Shell::StringExpression*>(expression))
				{
					result = Make("literal", span);
					// StringExpression owns the already-decoded literal produced by
					// the native lexer.  Reading Value.String is side-effect free;
					// leave runtime, interpolated, and non-string objects unset.
					if(string->Value.is_string())
						result.literalString = NativeStringUtf8(string->Value.Value.String);
				}
				else if(dynamic_cast<const ::Nilesoft::Shell::NumberExpression*>(expression) ||
					dynamic_cast<const ::Nilesoft::Shell::Array2Expression*>(expression))
					result = Make("literal", span);
				else if(auto ternary = dynamic_cast<const NativeTernary*>(expression))
				{
					result = Make("ternary", span);
					Append(result, Project(ternary->Condition, Span{}, true));
					Append(result, Project(ternary->True, Span{}, true));
					Append(result, Project(ternary->False, Span{}, true));
				}
				else if(auto binary = dynamic_cast<const NativeBinary*>(expression))
				{
					result = Make("binary", span);
					Append(result, Project(binary->Left, Span{}, true));
					Append(result, Project(binary->Right, Span{}, true));
				}
				else if(auto unary = dynamic_cast<const NativeUnary*>(expression))
				{
					result = Make("unary", span);
					Append(result, Project(unary->Operand, Span{}, true));
				}
				else if(auto assignment = dynamic_cast<const NativeAssign*>(expression))
					result = ProjectAssignment(assignment, span);
				else if(auto forStatement = dynamic_cast<const NativeFor*>(expression))
					result = ProjectFor(forStatement, span);
				else if(auto statement = dynamic_cast<const NativeStatement*>(expression))
					result = statement->HasReturn ? ProjectInterpolation(statement, span) : ProjectStatement(statement, span);
				else if(auto array = dynamic_cast<const NativeArray*>(expression))
				{
					result = Make("array", span);
						for(std::size_t index = 0; index < MaxArrayElements; ++index)
						{
							auto child = const_cast<NativeArray*>(array)->get(index);
						if(!child) break;
						Append(result, Project(child, Span{}, true));
					}
				}
				else if(auto function = dynamic_cast<const NativeFunc*>(expression))
					result = ProjectFunction(function, span);
				else if(dynamic_cast<const NativeVariable*>(expression))
					result = ProjectSourceChain(span, true);
				else
					result = Make("unknown", span);

				--depth_;
				return result;
			}

			void Append(ExpressionNode& parent, ExpressionNode child)
			{
				if(!child.id.empty()) parent.children.push_back(std::move(child));
			}

			std::size_t SkipTrivia(std::size_t position, std::size_t end) const
			{
				while(position < end)
				{
					if(IsWhitespace(source_[position])) { ++position; continue; }
					if(source_[position] == L'/' && position + 1 < end && source_[position + 1] == L'/')
					{
						position += 2;
						while(position < end && source_[position] != L'\r' && source_[position] != L'\n') ++position;
						continue;
					}
					if(source_[position] == L'/' && position + 1 < end && source_[position + 1] == L'*')
					{
						position += 2;
						while(position + 1 < end && !(source_[position] == L'*' && source_[position + 1] == L'/')) ++position;
						position = (std::min)(end, position + 2);
						continue;
					}
					break;
				}
				return position;
			}

			Span Trim(Span span) const
			{
				span = Clamp(span);
				if(!span.valid) return {};
				span.start = SkipTrivia(span.start, span.end);
				while(span.end > span.start && IsWhitespace(source_[span.end - 1])) --span.end;
				return span.end >= span.start ? span : Span{};
			}

			std::size_t Matching(std::size_t open, std::size_t end) const
			{
				if(open >= end || !IsOpen(source_[open])) return Invalid;
				const wchar_t opener = source_[open];
				const wchar_t closer = opener == L'(' ? L')' : opener == L'[' ? L']' : L'}';
				std::vector<wchar_t> stack;
				stack.push_back(closer);
				for(std::size_t position = open + 1; position < end; ++position)
				{
					const wchar_t value = source_[position];
					if(value == L'"' || value == L'\'' || value == L'`')
					{
						const wchar_t quote = value;
						for(++position; position < end; ++position)
						{
							if(source_[position] == L'\\') { if(position + 1 < end) ++position; continue; }
							if(source_[position] == quote) break;
						}
						continue;
					}
					if(value == L'/' && position + 1 < end && source_[position + 1] == L'/')
					{
						while(position < end && source_[position] != L'\r' && source_[position] != L'\n') ++position;
						continue;
					}
					if(value == L'/' && position + 1 < end && source_[position + 1] == L'*')
					{
						position += 2;
						while(position + 1 < end && !(source_[position] == L'*' && source_[position + 1] == L'/')) ++position;
						if(position + 1 < end) ++position;
						continue;
					}
					if(IsOpen(value))
					{
						stack.push_back(value == L'(' ? L')' : value == L'[' ? L']' : L'}');
						continue;
					}
					if(IsClose(value))
					{
						if(stack.empty() || stack.back() != value) return Invalid;
						stack.pop_back();
						if(stack.empty()) return position;
					}
				}
				return Invalid;
			}

			std::size_t TopLevelOperator(Span span, std::size_t& length) const
			{
				length = 0;
				span = Trim(span);
				if(!span.valid) return Invalid;
				std::vector<wchar_t> stack;
				for(std::size_t position = span.start; position < span.end; ++position)
				{
					const wchar_t value = source_[position];
					if(value == L'"' || value == L'\'' || value == L'`')
					{
						const wchar_t quote = value;
						for(++position; position < span.end; ++position)
						{
							if(source_[position] == L'\\') { if(position + 1 < span.end) ++position; continue; }
							if(source_[position] == quote) break;
						}
						continue;
					}
					if(value == L'/' && position + 1 < span.end && source_[position + 1] == L'/')
					{
						while(position < span.end && source_[position] != L'\r' && source_[position] != L'\n') ++position;
						continue;
					}
					if(value == L'/' && position + 1 < span.end && source_[position + 1] == L'*')
					{
						position += 2;
						while(position + 1 < span.end && !(source_[position] == L'*' && source_[position + 1] == L'/')) ++position;
						if(position + 1 < span.end) ++position;
						continue;
					}
					if(IsOpen(value))
					{
						stack.push_back(value == L'(' ? L')' : value == L'[' ? L']' : L'}');
						continue;
					}
					if(IsClose(value))
					{
						if(!stack.empty() && stack.back() == value) stack.pop_back();
						continue;
					}
					if(!stack.empty()) continue;

					const auto two = position + 1 < span.end ? source_.substr(position, 2) : std::wstring_view{};
					if(two == L"+=" || two == L"-=" || two == L"==" || two == L"!=" ||
						two == L"<=" || two == L">=" || two == L"&&" || two == L"||")
					{
						length = 2;
						return position;
					}
					if(value == L'=')
					{
						length = 1;
						return position;
					}
					if(value == L'?' || value == L':' || value == L'+' || value == L'-' ||
						value == L'*' || value == L'/' || value == L'%')
					{
						length = 1;
						return position;
					}
				}
				return Invalid;
			}

			std::size_t TopLevelComma(Span span) const
			{
				Span scan = Trim(span);
				if(!scan.valid) return Invalid;
				std::vector<wchar_t> stack;
				for(std::size_t position = scan.start; position < scan.end; ++position)
				{
					const wchar_t value = source_[position];
					if(value == L'"' || value == L'\'' || value == L'`')
					{
						const wchar_t quote = value;
						for(++position; position < scan.end; ++position)
						{
							if(source_[position] == L'\\') { if(position + 1 < scan.end) ++position; continue; }
							if(source_[position] == quote) break;
						}
						continue;
					}
					if(IsOpen(value)) { stack.push_back(value == L'(' ? L')' : value == L'[' ? L']' : L'}'); continue; }
					if(IsClose(value)) { if(!stack.empty() && stack.back() == value) stack.pop_back(); continue; }
					if(stack.empty() && value == L',') return position;
				}
				return Invalid;
			}

			std::string LowerName(Span span) const
			{
				span = Trim(span);
				if(!span.valid) return {};
				std::size_t position = span.start;
				if(position < span.end && (source_[position] == L'@' || source_[position] == L'$')) ++position;
				if(position >= span.end || !IsIdentifierStart(source_[position])) return {};
				const auto begin = position++;
				while(position < span.end && IsIdentifierPart(source_[position])) ++position;
				std::string result = Utf8(Span::From(begin, position));
				std::transform(result.begin(), result.end(), result.begin(), [](unsigned char value) {
					return static_cast<char>(value >= 'A' && value <= 'Z' ? value - 'A' + 'a' : value);
				});
				return result;
			}

			Span FirstIdentifierSpan(Span span) const
			{
				span = Trim(span);
				if(!span.valid) return {};
				std::size_t position = span.start;
				if(position < span.end && (source_[position] == L'@' || source_[position] == L'$')) ++position;
				if(position >= span.end || !IsIdentifierStart(source_[position])) return {};
				const auto begin = position++;
				while(position < span.end && IsIdentifierPart(source_[position])) ++position;
				return Span::From(begin, position);
			}

			ExpressionNode ProjectSourceChain(Span span, bool variable)
			{
				span = Trim(span);
				if(!span.valid) return {};
				std::size_t position = SkipTrivia(span.start, span.end);
				const auto prefix = position < span.end && (source_[position] == L'$' || source_[position] == L'@') ? position++ : position;
				if(position >= span.end || !IsIdentifierStart(source_[position])) return Make("unknown", span);
				++position;
				while(position < span.end && IsIdentifierPart(source_[position])) ++position;
				Span firstSpan = Span::From(prefix, position);
				ExpressionNode base = Make(variable ? "variable" : "identifier", firstSpan);

				while(true)
				{
					const auto suffix = SkipTrivia(position, span.end);
					if(suffix >= span.end) break;
					if(source_[suffix] == L'.')
					{
						position = SkipTrivia(suffix + 1, span.end);
						if(position >= span.end || !IsIdentifierStart(source_[position])) break;
						const auto nameStart = position++;
						while(position < span.end && IsIdentifierPart(source_[position])) ++position;
						ExpressionNode member = Make("member", Span::From(base.start, position));
						Append(member, std::move(base));
						Append(member, Make("identifier", Span::From(nameStart, position)));
						base = std::move(member);
						continue;
					}
					if(source_[suffix] == L'[')
					{
						const auto close = Matching(suffix, span.end);
						if(close == Invalid) break;
						ExpressionNode index = Make("index", Span::From(base.start, close + 1));
						Append(index, std::move(base));
						const Span inner = Span::From(suffix + 1, close);
						Append(index, ProjectSourceExpression(inner));
						base = std::move(index);
						position = close + 1;
						continue;
					}
					if(source_[suffix] == L'(')
					{
						const auto close = Matching(suffix, span.end);
						if(close == Invalid) break;
						ExpressionNode call = Make("call", Span::From(base.start, close + 1));
						Append(call, SourceArguments(Span::From(suffix + 1, close)));
						base = std::move(call);
						position = close + 1;
						continue;
					}
					break;
				}
				return base;
			}

			ExpressionNode SourceArguments(Span span)
			{
				ExpressionNode result;
				Span remaining = Trim(span);
				while(remaining.valid && remaining.start < remaining.end)
				{
					const auto comma = TopLevelComma(remaining);
					const auto partEnd = comma == Invalid ? remaining.end : comma;
					Append(result, ProjectSourceExpression(Span::From(remaining.start, partEnd)));
					if(comma == Invalid) break;
					remaining = Trim(Span::From(comma + 1, remaining.end));
				}
				return result;
			}

			ExpressionNode ProjectSourceExpression(Span span)
			{
				span = Trim(span);
				if(!span.valid || span.start >= span.end) return {};
				const auto first = source_[span.start];
				if(first == L'"' || first == L'\'' || first == L'`' ||
					(first >= L'0' && first <= L'9')) return Make("literal", span);
				if(first == L'[')
				{
					const auto close = Matching(span.start, span.end);
					ExpressionNode array = Make("array", close == Invalid ? span : Span::From(span.start, close + 1));
					if(close != Invalid)
					{
						Span remaining = Span::From(span.start + 1, close);
						while(remaining.valid && remaining.start < remaining.end)
						{
							const auto comma = TopLevelComma(remaining);
							const auto partEnd = comma == Invalid ? remaining.end : comma;
							Append(array, ProjectSourceExpression(Span::From(remaining.start, partEnd)));
							if(comma == Invalid) break;
							remaining = Trim(Span::From(comma + 1, remaining.end));
						}
					}
					return array;
				}
				if(first == L'(')
				{
					const auto close = Matching(span.start, span.end);
					if(close == span.end - 1)
					{
						ExpressionNode group = Make("group", span);
						Append(group, ProjectSourceExpression(Span::From(span.start + 1, close)));
						return group;
					}
				}
				std::size_t operatorLength = 0;
				if(TopLevelOperator(span, operatorLength) != Invalid)
					return Make("unknown", span);
			return ProjectSourceChain(span, first == L'$');
			}

			ExpressionNode ProjectAssignment(const NativeAssign* value, Span span)
			{
				ExpressionNode result = Make("assignment", span);
				std::size_t operatorLength = 0;
				const auto operatorPosition = TopLevelOperator(span, operatorLength);
			Span target;
			Span rhs;
			if(operatorPosition != Invalid)
			{
				 target = Trim(Span::From(span.start, operatorPosition));
				rhs = Trim(Span::From(operatorPosition + operatorLength, span.end));
			}
			if(!target.valid) target = FirstIdentifierSpan(span);
			if(target.valid) Append(result, ProjectSourceChain(target, true));

			const auto right = value->Right;
			const auto compound = operatorLength == 2 && operatorPosition != Invalid &&
				(operatorPosition < span.end && (source_[operatorPosition] == L'+' || source_[operatorPosition] == L'-')) &&
				dynamic_cast<const NativeBinary*>(right) != nullptr;
			if(compound)
			{
				const auto binary = dynamic_cast<const NativeBinary*>(right);
				Append(result, Project(binary->Right, rhs, false));
			}
			else
				Append(result, Project(right, rhs, true));
			return result;
		}

		ExpressionNode ProjectFor(const NativeFor* value, Span span)
		{
			ExpressionNode result = Make("for", span);
			const auto open = source_.find(L'(', span.start);
			const auto close = open == std::wstring_view::npos || open >= span.end ? Invalid : Matching(open, span.end);
			if(open != Invalid && close != Invalid)
			{
				Span remaining = Span::From(open + 1, close);
				const auto comma = TopLevelComma(remaining);
				Span init = comma == Invalid ? remaining : Span::From(remaining.start, comma);
				std::size_t equalsLength = 0;
				const auto equals = TopLevelOperator(init, equalsLength);
				if(equals != Invalid && equalsLength == 1)
				{
					ExpressionNode assignment = Make("assignment", init);
					Append(assignment, ProjectSourceChain(Trim(Span::From(init.start, equals)), true));
					Append(assignment, Project(const_cast<NativeFor*>(value)->Scope.at(value->Init), Trim(Span::From(equals + 1, init.end)), true));
					Append(result, std::move(assignment));
				}
				else if(value->Init != 0)
					Append(result, Make("variable", Trim(init)));

				std::vector<Span> parts;
				remaining = comma == Invalid ? Span{} : Trim(Span::From(comma + 1, close));
				while(remaining.valid)
				{
					const auto next = TopLevelComma(remaining);
					const auto end = next == Invalid ? remaining.end : next;
					parts.push_back(Trim(Span::From(remaining.start, end)));
					if(next == Invalid) break;
					remaining = Trim(Span::From(next + 1, close));
				}
				std::size_t child = 0;
				const NativeExpression* expressions[] = { value->Condition, value->Iterator, value->Body1, value->Body2 };
				for(const auto expression : expressions)
				{
					if(!expression) continue;
					Span forced = child < parts.size() ? parts[child++] : Span{};
					Append(result, Project(expression, forced, true));
				}
			}
			return result;
		}

		ExpressionNode ProjectStatement(const NativeStatement* value, Span span)
		{
			ExpressionNode result = Make("statement", span);
			for(auto child : value->Body) Append(result, Project(child, Span{}, true));
			return result;
		}

		std::size_t DirectInterpolationEnd(std::size_t start, std::size_t end) const
		{
			std::size_t position = start;
			if(position >= end) return start;
			if(source_[position] == L'(')
			{
				const auto close = Matching(position, end);
				return close == Invalid ? end : close + 1;
			}
			if(source_[position] == L'"' || source_[position] == L'\'' || source_[position] == L'`')
			{
				const auto quote = source_[position++];
				while(position < end)
				{
					if(source_[position] == L'\\') { position += (std::min)(2u, static_cast<unsigned>(end - position)); continue; }
					if(source_[position++] == quote) break;
				}
				return position;
			}
			if(!IsIdentifierStart(source_[position])) return position + 1;
			++position;
			while(position < end && IsIdentifierPart(source_[position])) ++position;
			while(position < end)
			{
				const auto next = SkipTrivia(position, end);
				if(next >= end || source_[next] == L'.')
				{
					if(next >= end) return next;
					position = SkipTrivia(next + 1, end);
					if(position >= end || !IsIdentifierStart(source_[position])) return next;
					while(position < end && IsIdentifierPart(source_[position])) ++position;
					continue;
				}
				if(source_[next] == L'[' || source_[next] == L'(')
				{
					const auto close = Matching(next, end);
					if(close == Invalid) return end;
					position = close + 1;
					continue;
				}
				return position;
			}
			return position;
		}

		const NativeExpression* NextInterpolationExpression(const NativeStatement* statement, std::size_t& index) const
		{
			while(index < statement->Body.size())
			{
				const auto expression = statement->Body[index++];
				if(dynamic_cast<const ::Nilesoft::Shell::StringExpression*>(expression) ||
					dynamic_cast<const NativePreviewEnvironment*>(expression)) continue;
				return expression;
			}
			return nullptr;
		}

		const NativeExpression* NextEnvironmentExpression(const NativeStatement* statement, std::size_t& index) const
		{
			while(index < statement->Body.size())
			{
				const auto expression = statement->Body[index++];
				if(dynamic_cast<const NativePreviewEnvironment*>(expression)) return expression;
				if(dynamic_cast<const ::Nilesoft::Shell::StringExpression*>(expression)) continue;
				return expression;
			}
			return nullptr;
		}

		ExpressionNode ProjectInterpolation(const NativeStatement* statement, Span span)
		{
			ExpressionNode result = Make("interpolation", span);
			// Single-quoted strings use the runtime interpolation parser even when
			// they contain no interpolation.  Preserve the native decoded value in
			// that case so managed source resolution can distinguish a constant path
			// from an environment reference or runtime expression.  An empty quoted
			// string has no body node, but is still an unambiguous constant.
			const bool quotedSpan = span.valid && span.start < span.end &&
				(source_[span.start] == L'\'' || source_[span.start] == L'`') &&
				span.end > span.start + 1 && source_[span.end - 1] == source_[span.start];
			if(quotedSpan && statement->Body.empty())
			{
				result.kind = "literal";
				result.literalString = std::string{};
				return result;
			}
			if(quotedSpan && !statement->Body.empty())
			{
				// The runtime interpolation lexer may split ordinary text at an
				// unmatched `%` or an escaped `@@`.  When every body part is still a
				// decoded string, concatenate those parts into one native literal;
				// an environment or embedded expression keeps the structured form.
				bool allStrings = true;
				std::string decoded;
				for(const auto* part : statement->Body)
				{
					auto string = dynamic_cast<const ::Nilesoft::Shell::StringExpression*>(part);
					if(!string || !string->Value.is_string())
					{
						allStrings = false;
						break;
					}
					decoded += NativeStringUtf8(string->Value.Value.String);
				}
				if(allStrings)
				{
					result.kind = "literal";
					result.literalString = std::move(decoded);
					return result;
				}
			}
			std::size_t begin = span.valid ? span.start : 0;
			std::size_t end = span.valid ? span.end : begin;
			if(begin < end && (source_[begin] == L'\'' || source_[begin] == L'`'))
			{
				++begin;
				if(end > begin && source_[end - 1] == source_[begin - 1]) --end;
			}
			std::size_t textStart = begin;
			std::size_t bodyIndex = 0;
			auto appendText = [&](std::size_t textEnd)
			{
				if(textEnd > textStart) Append(result, Make("interpolationText", Span::From(textStart, textEnd)));
			};
			for(std::size_t position = begin; position < end; ++position)
			{
				if(source_[position] == L'@')
				{
					if(position + 1 < end && source_[position + 1] == L'@') { ++position; continue; }
					const auto expressionStart = position + 1;
					const auto expressionEnd = DirectInterpolationEnd(expressionStart, end);
					if(expressionEnd <= expressionStart) continue;
					appendText(position);
					ExpressionNode embedded = Make("interpolatedExpression", Span::From(position, expressionEnd));
					const NativeExpression* native = NextInterpolationExpression(statement, bodyIndex);
					if(expressionStart < end && source_[expressionStart] == L'(')
					{
						const auto close = expressionEnd > expressionStart && source_[expressionEnd - 1] == L')' ? expressionEnd - 1 : expressionEnd;
						ExpressionNode group = Make("group", Span::From(expressionStart, expressionEnd));
						const auto inner = Span::From(expressionStart + 1, close);
						Append(group, native ? Project(native, inner, false) : ProjectSourceExpression(inner));
						Append(embedded, std::move(group));
					}
					else
						Append(embedded, native ? Project(native, Span::From(expressionStart, expressionEnd), false) : ProjectSourceExpression(Span::From(expressionStart, expressionEnd)));
					Append(result, std::move(embedded));
					position = expressionEnd - 1;
					textStart = expressionEnd;
					continue;
				}
				if(source_[position] == L'%')
				{
					const auto close = source_.find(L'%', position + 1);
					if(close != std::wstring_view::npos && close < end)
					{
						appendText(position);
						const auto native = NextEnvironmentExpression(statement, bodyIndex);
						Append(result, native ? Project(native, Span::From(position, close + 1), false) : Make("environment", Span::From(position, close + 1)));
						position = close;
						textStart = close + 1;
					}
				}
			}
			appendText(end);
			return result;
		}

		ExpressionNode ProjectFunction(const NativeFunc* function, Span span)
		{
			// The native parser stores a dotted call chain as one FuncExpression
			// with the later member in `Child`.  Its source span therefore covers
			// the whole chain (for example `foo.bar(1).baz`).  Project the
			// function itself against the portion before the child so the call
			// remains independently editable and the enclosing member retains the
			// complete span.
			const auto childSpan = function->Child ? SpanFor(function->Child, Span{}) : Span{};
			Span functionSpan = span;
			if(childSpan.valid && childSpan.start > span.start)
			{
				std::size_t end = childSpan.start;
				while(end > span.start && IsWhitespace(source_[end - 1])) --end;
				if(end > span.start && source_[end - 1] == L'.') --end;
				functionSpan = Span::From(span.start, end);
			}
			const auto name = LowerName(functionSpan);
			const bool hasCall = function->Brackets || !function->Arguments.empty() ||
				// `source_.find` must be bounded by this expression.  A bare
				// identifier before a later declaration (for example `import
				// menu_file` followed by `item(...)`) is a function object in the
				// runtime, but it is not a call in the source model.  Treating a
				// later parenthesis as belonging to it changes the projected kind
				// to `call` and prevents safe literal/variable resolution.
				(functionSpan.valid && source_.find(L'(', functionSpan.start) < functionSpan.end);
			ExpressionNode result;
			if(!hasCall)
				result = ProjectSourceChain(functionSpan, false);
			else
			{
				const auto kind = name == "if" ? "if" : name == "for" ? "for" : name == "foreach" ? "foreach" : name == "while" ? "while" : "call";
				result = Make(kind, functionSpan);
				std::size_t firstArgument = function->extented && !function->Arguments.empty() ? 1 : 0;
				for(std::size_t index = firstArgument; index < function->Arguments.size(); ++index)
					Append(result, Project(function->Arguments[index], Span{}, true));
			}
			if(function->Child)
			{
				ExpressionNode child = Project(function->Child, Span{}, true);
				ExpressionNode member = Make("member", Union(span, childSpan));
				Append(member, std::move(result));
				Append(member, std::move(child));
				return member;
			}
			return result;
		}
	};
	}

	ExpressionNode ProjectNativeExpression(const ::Nilesoft::Shell::Expression* expression,
		std::wstring_view source, const ExpressionSources& sources)
	{
		return Projector(source, sources).Project(expression);
	}
}
