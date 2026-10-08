
#include <pch.h>
#include <tuple>
#include "Expression\Variable.h"
#include "Include\Theme.h"
#include "RegistryConfig.h"

// Windows State (Hidden | Normal | Minimized | Maximized)
namespace Nilesoft
{
	namespace Shell
	{
		extern Logger &_log = Logger::Instance();

		class Parser::StudioNodeGuard final
		{
			Parser* owner_ = nullptr;
			bool active_ = false;

		public:
			StudioNodeGuard(Parser& owner, std::string kind, std::string name, std::size_t start)
				: owner_(&owner), active_(owner.studio_begin_node(std::move(kind), std::move(name), start))
			{
			}

			~StudioNodeGuard()
			{
				Finish();
			}

			StudioNodeGuard(const StudioNodeGuard&) = delete;
			StudioNodeGuard& operator=(const StudioNodeGuard&) = delete;

			explicit operator bool() const noexcept { return active_; }

			void SetExpression(const Expression* expression)
			{
				if(active_) owner_->studio_set_node_expression(expression);
			}

			void Finish()
			{
				if(active_)
				{
					owner_->studio_finish_node();
					active_ = false;
				}
			}
		};

		bool Parser::studio_source_active() const
		{
			if(!m_syntaxOnly || !l) return false;
			const auto root = m_syntaxPath.empty() ? L"<studio>" : m_syntaxPath.c_str();
			return l->path.equals(root);
		}

		std::size_t Parser::studio_end_position() const
		{
			if(!l) return 0;
			if(m_triviaLexer == l && m_triviaEnd == l->index)
				return m_triviaStart;
			return l->index;
		}

		std::string Parser::studio_source_text(std::size_t start, std::size_t end) const
		{
			if(!l || !l->buffer || start > l->length) return {};
			end = (std::min)(end, l->length);
			if(end < start) return {};
			return UTF8::Utf16ToUtf8(l->buffer + start, end - start);
		}

		bool Parser::studio_begin_node(std::string kind, std::string name, std::size_t start)
		{
			if(!studio_source_active()) return false;
			if(m_studioNodeStack.size() >= StudioLanguage::FrontendLimits::MaxRecursionDepth)
			{
				if(!m_studioEmissionLimited)
				{
					m_studioEmissionLimited = true;
					StudioLanguage::Diagnostic diagnostic;
					diagnostic.code = "LANG_LIMIT";
					diagnostic.message = "The native declaration nesting depth exceeds the language service limits.";
					diagnostic.severity = "error";
					diagnostic.start = static_cast<int>((std::min)(start, static_cast<std::size_t>((std::numeric_limits<int>::max)())));
					diagnostic.length = 1;
					diagnostic.remedy = "Reduce the declaration nesting depth.";
					m_studioSyntax.diagnostics.push_back(std::move(diagnostic));
				}
				error(TokenError::Unknown);
				return false;
			}
			StudioLanguage::Node node;
			node.id = "native-node-" + std::to_string(++m_studioNodeId);
			node.kind = std::move(kind);
			node.name = std::move(name);
			node.start = static_cast<int>((std::min)(start, static_cast<std::size_t>((std::numeric_limits<int>::max)())));
			m_studioNodeStack.push_back(std::move(node));
			return true;
		}

		void Parser::studio_finish_node()
		{
			if(m_studioNodeStack.empty()) return;
			auto node = std::move(m_studioNodeStack.back());
			m_studioNodeStack.pop_back();
			const auto end = (std::max)(static_cast<std::size_t>(node.start), studio_end_position());
			node.length = static_cast<int>((std::min)(end - static_cast<std::size_t>(node.start), static_cast<std::size_t>((std::numeric_limits<int>::max)())));
			if(m_studioNodeStack.empty()) m_studioSyntax.nodes.push_back(std::move(node));
			else m_studioNodeStack.back().children.push_back(std::move(node));
		}

		bool Parser::enter_expression_depth()
		{
			if(m_expressionDepth >= StudioLanguage::FrontendLimits::MaxRecursionDepth)
			{
				if(!m_expressionDepthLimited)
				{
					m_expressionDepthLimited = true;
					StudioLanguage::Diagnostic diagnostic;
					diagnostic.code = "LANG_LIMIT";
					diagnostic.message = "The native expression nesting depth exceeds the language service limits.";
					diagnostic.severity = "error";
					diagnostic.start = static_cast<int>((std::min)(l ? l->index : 0, static_cast<std::size_t>((std::numeric_limits<int>::max)())));
					diagnostic.length = l && l->index < l->length ? 1 : 0;
					diagnostic.remedy = "Reduce the expression nesting depth.";
					m_studioSyntax.diagnostics.push_back(std::move(diagnostic));
				}
				if(context.Preview) context.Preview->Fail("PREVIEW_LIMIT", L"The native expression nesting depth exceeds the preview limit.");
				if(!m_error)
				{
					m_error = true;
					error_code = TokenError::Unknown;
				}
				return false;
			}
			++m_expressionDepth;
			return true;
		}

		void Parser::leave_expression_depth()
		{
			if(m_expressionDepth > 0) --m_expressionDepth;
		}

		void Parser::studio_set_property_insert(std::size_t position)
		{
			if(!m_studioNodeStack.empty() && m_studioNodeStack.back().propertyInsert < 0)
				m_studioNodeStack.back().propertyInsert = static_cast<int>((std::min)(position, static_cast<std::size_t>((std::numeric_limits<int>::max)())));
		}

		void Parser::studio_set_child_insert(std::size_t position)
		{
			if(!m_studioNodeStack.empty() && m_studioNodeStack.back().childInsert < 0)
				m_studioNodeStack.back().childInsert = static_cast<int>((std::min)(position, static_cast<std::size_t>((std::numeric_limits<int>::max)())));
		}

		void Parser::studio_set_node_expression(const Expression* expression)
		{
			if(!expression || m_studioNodeStack.empty() || !l || !l->buffer) return;
			const auto projected = StudioLanguage::ProjectNativeExpression(expression,
				std::wstring_view(l->buffer, l->length), ExpressionSources);
			if(!projected.id.empty())
			{
				m_studioNodeStack.back().hasExpression = true;
				m_studioNodeStack.back().expression = projected;
			}
		}

		void Parser::studio_add_property(std::size_t start, std::size_t name_end,
			std::size_t value_start, std::size_t end, const Expression* expression)
		{
			if(!studio_source_active() || m_studioNodeStack.empty()) return;
			// Property parsers call skip() before looking for the next property or
			// closing delimiter.  That advances the lexer over source trivia, but
			// the trivia belongs outside the property's editable value.  Trim the
			// lexer-reported end here so replacing a value cannot remove the space
			// (or comment) that separates it from the next flag/property.
			end = studio_end_position();
			if(m_studioNodeStack.back().properties.size() >= StudioLanguage::FrontendLimits::MaxProperties)
			{
				if(!m_studioEmissionLimited)
				{
					m_studioEmissionLimited = true;
					StudioLanguage::Diagnostic diagnostic;
					diagnostic.code = "LANG_LIMIT";
					diagnostic.message = "The native property count exceeds the language service limits.";
					diagnostic.severity = "error";
					diagnostic.start = static_cast<int>((std::min)(start, static_cast<std::size_t>((std::numeric_limits<int>::max)())));
					diagnostic.length = 1;
					m_studioSyntax.diagnostics.push_back(std::move(diagnostic));
				}
				return;
			}
			end = (std::max)(end, start);
			name_end = (std::max)(name_end, start);
			value_start = (std::max)(value_start, name_end);
			// A bare flag has no value.  Its parser cursor may already have
			// advanced over the separating trivia to the next property, but the
			// zero-length value span must still belong to this property's span.
			value_start = (std::min)(value_start, end);
			StudioLanguage::Property property;
			property.name = studio_source_text(start, name_end);
			property.start = static_cast<int>((std::min)(start, static_cast<std::size_t>((std::numeric_limits<int>::max)())));
			property.length = static_cast<int>((std::min)(end - start, static_cast<std::size_t>((std::numeric_limits<int>::max)())));
			property.valueStart = static_cast<int>((std::min)(value_start, static_cast<std::size_t>((std::numeric_limits<int>::max)())));
			property.valueLength = static_cast<int>((std::min)(end >= value_start ? end - value_start : 0, static_cast<std::size_t>((std::numeric_limits<int>::max)())));
			if(expression && l->buffer)
			{
				property.expression = StudioLanguage::ProjectNativeExpression(expression,
					std::wstring_view(l->buffer, l->length), ExpressionSources);
				property.hasExpression = !property.expression.id.empty();
			}
			m_studioNodeStack.back().properties.push_back(std::move(property));
		}

		Parser::Parser()
			: Parser(Initializer::instance ? Initializer::instance->cache : nullptr)
		{
		}

		Parser::Parser(CACHE *target_cache)
		{
			context.Application = &Initializer::instance->application;

			context.Cache = target_cache ? target_cache : Initializer::instance->cache;
			context.variables.global = &context.Cache->variables.global;
			context.variables.runtime = &context.Cache->variables.runtime;
			context.Selections = nullptr;

			string cfg;
			if(RegistryConfig::get(nullptr, L"config", cfg) && !cfg.empty())
			{
				//if(Path::IsFileExists(cfg))
				m_path = cfg.move();
			}

			if(m_path.empty() || !Path::IsFileExists(m_path))
			{
				if(Path::IsFileExists(context.Application->ConfigPortable))
					m_path = context.Application->ConfigPortable;
				else
				{
					/*string ss = L"config file ";
					if(!m_path.empty())
						ss.append_format(L"'%s' ", m_path.c_str());
					ss.append(L"not found.");
					_log.warning(ss);
					.*/
					string ss = L"config file ";
					if(!m_path.empty())
					{
						ss += L"'";
						ss += m_path;
						ss += L"' ";
					}
					ss += L"not found.";
					_log.warning(ss);
					return;
				}
			}

			context.Application->Config = m_path;

			_imports.emplace_back(new Lexer);
			l = _imports.front().get();
			
			if(!l->load_File(m_path.c_str()) || l->length < 5)
			{
				if(HasError())
				{
					error(l->length < 5 ? TokenError::InvalidConfigFile : l->error, m_path.c_str());
				}
			}
		}

		Parser::Parser(SyntaxInput input)
			: m_syntaxSource(input.source),
			  m_syntaxPath(input.path),
			  m_ownedCache(std::make_unique<CACHE>()),
			  m_syntaxOnly(true),
			  m_syntaxLocalization(input.localization)
		{
			// Syntax parsing gets an isolated context.  It can build the same
			// native expression/menu objects as the runtime parser, but it never
			// observes or mutates Initializer state.
			context.Application = &m_syntaxApplication;
			context.Cache = m_ownedCache.get();
			context.variables.global = &context.Cache->variables.global;
			context.variables.runtime = &context.Cache->variables.runtime;
			context.Selections = nullptr;
			context.Runtime = false;

			m_syntaxApplication.Config = m_syntaxPath.empty() ? L"<studio>" : m_syntaxPath;
			m_syntaxApplication.ConfigPortable = m_syntaxApplication.Config;
			m_path = m_syntaxApplication.Config;

			_imports.emplace_back(new Lexer);
			l = _imports.front().get();
			const wchar_t *source = m_syntaxSource.empty() ? L"" : m_syntaxSource.c_str();
			const wchar_t *path = m_syntaxPath.empty() ? L"<studio>" : m_syntaxPath.c_str();
			if(!l->load_buffer(source, m_syntaxSource.length(), path))
				error(l->error == TokenError::None ? TokenError::InvalidFile : l->error,
					  m_syntaxApplication.Config.c_str());
		}

		Parser::Parser(PreviewInput input) : Parser(input.syntax)
		{
			m_preview = true;
			m_previewImport = std::move(input.import);
			m_previewQuery = std::move(input.query);
			context.Preview = input.policy;
		}

		void Parser::preview_query_boundary()
		{
			if(m_previewQuery && m_previewQuery(std::wstring_view(l->path.c_str(), l->path.length()), l->index))
				throw PreviewQueryComplete{};
		}

		void Parser::record_expression_source(const Expression* expression, std::size_t start)
		{
			if(m_syntaxOnly && expression)
			{
				const auto end = m_triviaLexer == l && m_triviaEnd == l->index ? m_triviaStart : l->index;
				ExpressionSources[expression] = {std::wstring(l->path.c_str(), l->path.length()), start, (std::max)(start, end) - start};
			}
		}

		std::unique_ptr<Expression> Parser::ParseExpression(std::wstring_view source)
		{
			if(!m_syntaxOnly || source.size() > 4u * 1024u * 1024u) return {};
			StudioLanguage::Frontend limits(source);
			const auto shape = limits.Parse();
			if(std::any_of(shape.diagnostics.begin(), shape.diagnostics.end(),
				[](const auto& d) { return d.code == "LANG_LIMIT"; }))
			{
				if(context.Preview) context.Preview->Fail("PREVIEW_LIMIT", L"The expression exceeds the native structural limit.");
				return {};
			}
			Lexer expressionLexer;
			if(!expressionLexer.load_buffer(source.data(), source.size(), L"<expression>")) return {};
			auto previous = l;
			l = &expressionLexer;
			try
			{
				skip();
				std::unique_ptr<Expression> expression(parse_root_expression());
				skip();
				if(!l->eof) error(TokenError::ExpressionExpected);
				l = previous;
				return expression;
			}
			catch(...)
			{
				l = previous;
				if(context.Preview) context.Preview->Fail("PREVIEW_SYNTAX", L"The native parser rejected this expression.");
				return {};
			}
		}

		void Parser::capture_expression_syntax(const Expression* expression)
		{
			if(!m_syntaxOnly || !expression || !l || !l->buffer) return;
			auto found = ExpressionSources.find(expression);
			if(found == ExpressionSources.end()) return;
			m_nativeExpressions[{found->second.file, found->second.start}] =
				StudioLanguage::ProjectNativeExpression(expression, std::wstring_view(l->buffer, l->length), ExpressionSources);
		}

		void Parser::project_studio_expressions()
		{
			if(!m_syntaxOnly) return;
			auto project = [&](StudioLanguage::ExpressionNode& expression)
			{
				auto found = m_nativeExpressions.find({m_syntaxPath, static_cast<std::size_t>(expression.start)});
				if(found != m_nativeExpressions.end()) expression = found->second;
			};
			std::function<void(std::vector<StudioLanguage::Node>&)> visit = [&](auto& nodes)
			{
				for(auto& node : nodes)
				{
					if(node.hasExpression) project(node.expression);
					for(auto& property : node.properties) if(property.hasExpression) project(property.expression);
					visit(node.children);
				}
			};
			visit(m_studioSyntax.nodes);
		}

		Scope* Parser::ScopeAt(std::wstring_view path, std::size_t position)
		{
			Scope* found = &context.Cache->variables.global;
			std::function<void(NativeMenu*)> visit = [&](NativeMenu* menu)
			{
				if(menu->source_end && menu->source_file.equals(std::wstring(path).c_str()))
				{
					const auto start = std::wcstoull(menu->source_node_id.c_str() + 1, nullptr, 10);
					if(position >= start && position <= menu->source_end) found = &menu->variables;
				}
				for(auto child : menu->items) visit(child);
			};
			visit(&context.Cache->dynamic);
			return found;
		}

		Parser::~Parser() { }
		size_t Parser::Line() const { return l ? l->line : 0; };
		size_t Parser::Column() const { return l ? l->column : 0; };
		const wchar_t *Parser::Path() const { return l ? l->path.c_str() : nullptr; };

		bool Parser::HasError() const
		{
			return error_code != TokenError::None;
		}

		TokenError Parser::Error() const
		{
			return error_code;
		}

		const StudioLanguage::Document &Parser::StudioSyntax() const
		{
			return m_studioSyntax;
		}

		void Parser::refresh_studio_syntax()
		{
			m_studioNodeStack.clear();
			m_nativeExpressions.clear();
			m_studioEmissionLimited = false;
			m_studioNodeId = 0;
			m_expressionDepth = 0;
			m_expressionDepthLimited = false;
			m_syntaxDiagnosticAdded = false;
			if(m_syntaxOnly)
			{
				StudioLanguage::Frontend frontend(
					std::wstring_view(m_syntaxSource.data(), m_syntaxSource.length()));
				m_studioSyntax = frontend.Tokenize();
				return;
			}

			if(l && l->buffer && l->length > 0)
			{
				StudioLanguage::Frontend frontend(
					std::wstring_view(l->buffer, l->length));
				m_studioSyntax = frontend.Parse();
			}
			else
			{
				m_studioSyntax = {};
			}
		}

		void Parser::append_studio_diagnostic()
		{
			if(!m_syntaxOnly || !m_error || m_syntaxDiagnosticAdded)
				return;
			if(m_expressionDepthLimited)
			{
				m_syntaxDiagnosticAdded = true;
				return;
			}

			m_syntaxDiagnosticAdded = true;
			const auto start = l ? (std::min)(l->index, m_syntaxSource.length()) : 0;
			const auto line = l ? l->line : 1;
			const auto column = l ? l->column : 1;
			const auto numeric_error = static_cast<unsigned>(error_code);
			StudioLanguage::Diagnostic diagnostic;
			diagnostic.code = "PARSER_" + std::to_string(numeric_error);
			diagnostic.message = "The runtime parser rejected this configuration (error " +
				std::to_string(numeric_error) + ").";
			diagnostic.severity = "error";
			diagnostic.start = static_cast<int>(start);
			diagnostic.length = start < m_syntaxSource.length() ? 1 : 0;
			diagnostic.remedy = "Correct the configuration syntax before applying it.";
			m_studioSyntax.diagnostics.push_back(std::move(diagnostic));

			// Keep the location visible in the native object for callers that inspect
			// Parser directly; the JSON protocol carries UTF-16 start/length while
			// line/column remain available through Parser::Line/Column.
			(void)line;
			(void)column;
		}

		void Parser::set_source_identity(NativeMenu *menu, size_t source_start)
		{
			if(!menu || !l)
				return;

			menu->source_file = l->path;
			menu->source_node_id = L"n";
			menu->source_node_id += std::to_wstring(source_start);
			menu->source_hash = l->source_hash;
			// The legacy native parser retains the imported file and local span,
			// but does not retain the import-site occurrence identity.  Mark that
			// fact at construction time so capture cannot publish an ambiguous,
			// edit-looking reference.  Root declarations remain fully identifiable.
			menu->source_occurrence_unavailable = _imports.size() >= 2;
		}

		bool Parser::error(TokenError tokenError)
		{
			if(!m_error)
			{
				error_code = tokenError;
				m_error = true;
			}

			if(m_syntaxOnly)
			{
				const auto line = l ? l->line : 0;
				const auto column = l ? l->column : 0;
				const string path = l ? l->path : m_syntaxApplication.Config;
				throw ParserException(tokenError, line, column, path, false);
			}

			bool log = true;
			auto le = &Initializer::LastError;
			if(le->code != TokenError::None)
				log = !(le->code == error_code && le->line == l->line && le->col == l->column);

			throw ParserException(tokenError, l->line, l->column, Path::Name(l->path).c_str(), log);
		}

		bool Parser::error(TokenError tokenError, const wchar_t *message)
		{
			if(!m_error)
			{
				error_code = tokenError;
				m_error = true;

				if(m_syntaxOnly)
					return false;

				auto le = &Initializer::LastError;
				if(le->code != TokenError::None && (le->code == error_code && le->line == l->line && le->col == l->column))
					return false;

				if(message)
					Logger::Error(error_format1, ParserException::errortostr(error_code), message, Path::Name(l->path).c_str());
				else
					Logger::Error(error_format0, ParserException::errortostr(error_code));
				//Logger::Error(L"error %s, %s file", ParserException::errortostr(error_code), message);
			}
			return false;
		}

		bool Parser::error(TokenError tokenError, size_t column)
		{
			l->column = column;
			return error(tokenError);
		}

		bool Parser::error(TokenError tokenError, size_t column, size_t line)
		{
			l->column = column;
			l->line = line;
			return error(tokenError);
		}

		void Parser::error_if(bool condition, TokenError tokenError)
		{
			if(condition) error(tokenError);
		}

		void Parser::error_if(bool condition, TokenError tokenError, size_t column)
		{
			if(condition) error(tokenError, column);
		}

		void Parser::error_if(bool condition, TokenError tokenError, size_t column, size_t line)
		{
			if(condition) error(tokenError, column, line);
		}

		bool Parser::skip_comment(bool singleLineComment)
		{
			auto col = start_col, line = start_line;

			if(l->eof) return false;

			auto single = (l->tok == L'/' && l->peek == L'/');	// start single-line comment
			auto multi = (l->tok == L'/' && l->peek == L'*');	// start multi-line comment
					
			if(single)
			{
				if(!singleLineComment) return false;
				//error_if(singleLineComment == 2, TokenError::CommentSinglelineUnexpected);
			}

			if(single || multi)
			{
				if(multi)
				{
					col = l->column, line = l->line;
				}

				//skip start comment and continue to end
				l->next(2);
				for(;;)
				{
					if(l->eof)
					{
						//error(TokenError::CloseCommentExpected, col, line);
						//return false;
						//break;
						//if(single) return false;
						error_if(multi, TokenError::CloseCommentExpected, col, line);
						return false;
					}
					// End single-line comment
					else if(l->tok == L'\n' && single) break;
					// End multi-line comment
					else if((l->tok == L'*' && l->peek == L'/') && multi) { l->next(); break; }
					l->next();
				}
				l->next(); //skip end comment '\n' or '/'
				return true;
			}
			return false;
		}

		bool Parser::skip(bool singleLineComment, bool eat_whitespace)
		{
			const auto triviaStart = l->index;
			auto rememberTrivia = [&]()
			{
				if(m_syntaxOnly && l->index > triviaStart)
				{
					if(m_triviaLexer != l || m_triviaEnd != triviaStart) m_triviaStart = triviaStart;
					m_triviaLexer = l; m_triviaEnd = l->index;
				}
			};
			for(; ;)
			{
				if(l->eof) { rememberTrivia(); return false; }
				if(l->is_space() && eat_whitespace) l->next();
				else if(l->tok == L'/' && l->is({ L'/', L'*' }, 1))
				{
					if(!skip_comment(singleLineComment))
						break;
				}
				else
				{
					break;
				}
			}
			rememberTrivia();
			return true;
		}

		Parser &Parser::eat(int i)
		{
			skip();
			if(i >= 0)
			{
				l->next(i);
				skip();
			}
			return *this;
		}

		bool Parser::peek_char(wchar_t c, bool singleLineComment)
		{
			auto p = l->index;
			skip(singleLineComment);
			if(l->next_is(c))
				return true;
			l->previous(p);
			return false;
		}

		uint32_t Parser::peek_ident(const std::initializer_list<uint32_t> &ids, uint32_t *value)
		{
			auto s = l->buf();
			if(std::iswalpha(*s))
			{
				uint32_t count = 0;
				Hash h;
				h.hash(*s);
				do
				{
					h.hash(*s++); count++;
				} while(std::iswalnum(*s) || (*s == L'_'));

				for(auto &id : ids)
				{
					if(h == id)
					{
						if(value) *value = h;
						return count;
					}
				}
			}
			if(value) *value = 0;
			return 0;
		}

		bool Parser::expect(wchar_t c, bool singleLineComment)
		{
			skip(singleLineComment);
			if(l->next_is(c))
				return true;
			return false;
		}

		bool Parser::expect(std::initializer_list<wchar_t> chs, bool singleLineComment)
		{
			for(auto c : chs)
			{
				if(l->eof) return false;
				if(expect(c, singleLineComment)) return true;
			}
			return false;
		}


		uint32_t Parser::expect(std::initializer_list<const wchar_t *> words, bool singleLineComment)
		{
			for(auto word : words)
			{
				if(l->eof) return false;
				if(expect(word, singleLineComment)) return true;
			}
			return false;
		}

		bool Parser::expect(const wchar_t *word, bool singleLineComment)
		{
			skip(singleLineComment);

			bool ret = true;
			auto last_index = l->index;

			size_t i = 0;
			while(*word)
			{
				if(l->eof) return false;
				if(++i >= l->length) break;

				if(!l->is(*word++))
				{
					ret = false;
					break;
				}

				l->next();
			}

			if(ret && !l->is_alpha())
				return true;

			l->previous(last_index);
			return false;
		}

		bool Parser::expect_assign(bool expected)
		{
			skip();
			if(l->next_if(l->is_assign())) return true;
			error_if(expected, TokenError::AssignExpected);
			return false;
		}

		bool Parser::expect_openParen()
		{
			return expect(L'(') ? true : error(TokenError::OpenParenExpected);
		}

		bool Parser::expect_closeParen()
		{
			return expect(L')') ? true : error(TokenError::CloseParenExpected);
		}

		bool Parser::expect_openCurly()
		{
			return expect(L'{') ? true : error(TokenError::OpenCurlyExpected);
		}

		bool Parser::expect_closeCurly()
		{
			return expect(L'}') ? true : error(TokenError::CloseCurlyExpected);
		}

		// system environment variable
		//	::= %var%
		bool Parser::parse_environment(string &value)
		{
			if(l->tok != L'%') return false;
			wchar_t percent = l->next();// skip start '%'
			if(l->tok != L'%')
			{
				bool ret = false;
				for(;;)
				{
					error_if(l->eof, TokenError::StringUnterminated);

					if(l->next_is(percent))// skip end %
					{
						ret = true;
						break;
					}
					else if(!(l->is_iddigit() || l->tok == L'(' || l->tok == L')'))
						break;
					value.append(l->next());
				}

				if(ret)
				{
					if(m_syntaxOnly)
					{
						// Preserve the source spelling in the isolated parser.  The
						// runtime path below is the only path allowed to expand
						// process environment variables.
						value.append(percent);
						value = (percent + value).move();
						return true;
					}

					string var = Environment::Variable(value).move();
					if(!var.empty())
					{
						value = var.move();
						return true;
					}
					value.append(percent);
				}
			}
			value = (percent + value).move();
			return true;
		}

		uint32_t Parser::parse_ident(bool multiple)
		{
			skip();
			prevCol = l->column;
			Hash ident;
			if(multiple)
				while(l->is_iddigit() || l->is({ L'-', L'.' })) ident.hash(l->next());
			else
				while(l->is_iddigit()) ident.hash(l->next());
			//error_if(eof, TokenError::IdentifierExpected);
			return ident.value();
		}

		void Parser::parse_menu(NativeMenu *menu, bool imported)
		{
			struct RestoreScope { Context& context; Scope* old; ~RestoreScope() { context.variables.local = old; } } restore{context, context.variables.local};
			if(m_preview) context.variables.local = &menu->variables;
			if(!imported)
				expect_openCurly();

			while(true)
			{
				skip();
				preview_query_boundary();

				if(l->eof || l->tok == L'}')
					break;

				// local variables definition
				if(parse_variable(&menu->variables))
					continue;

				// local image definition
				if(parse_image())
					continue;

				const auto source_start = l->index;
				auto type = parse_ident(false);
				
				if(type == CONFIG_IMPORT)
				{
					auto ret = load_import(l->line, l->column, true, false, source_start);
					if(ret > 0)
					{
						skip();
						prevCol = l->column;
						parse_menu(menu, true);
						skip();
						pop_import();
					}
					continue;
				}
				else if(type == CONFIG_MODIFY || type == IDENT_REMOVE)
				{
					parse_modify_items(type, source_start);
					continue;
				}

				std::unique_ptr<NativeMenu> sub(new NativeMenu(menu));
				set_source_identity(sub.get(), source_start);
				if(parse_menu_item(sub.get(), type))
				{
					menu->items.push_back(sub.release());
					TotalMenuCount++;
				}
			//	else if(!imported)
			//		break;
			}

			if(!imported && !l->eof)
				expect_closeCurly();
		}

		bool Parser::parse_menu_item(NativeMenu *menu, uint32_t type)
		{
			struct RecordEnd { NativeMenu* menu; Lexer* lexer; ~RecordEnd() { menu->source_end = lexer->index; } } record{menu, l};
			const auto source_start = menu && menu->source_node_id.length() > 1
				? std::wcstoull(menu->source_node_id.c_str() + 1, nullptr, 10) : l->index;
			const auto kind = type == MENU_TYPE_MENU ? "menu" : type == MENU_TYPE_ITEM ? "item" :
				(type == MENU_TYPE_SEP || type == MENU_TYPE_SEPARATOR) ? "separator" : "";
			StudioNodeGuard studio_node(*this, kind, kind, source_start);
			switch(type)
			{
				case MENU_TYPE_MENU:
					menu->type = NativeMenuType::Menu;
					break;
				case MENU_TYPE_ITEM:
					menu->type = NativeMenuType::Item;
					break;
				case MENU_TYPE_SEP:
				case MENU_TYPE_SEPARATOR:
					menu->type = NativeMenuType::Separator;
					break;
				default:
					return error(TokenError::MenuTypeExpected, prevCol);
			}

			auto col_after_open_paren = l->column;

			// parse menu properties
			if(!parse_properties(menu))
				return false;

			if(menu->properties == 0 && menu->is_separator())
				return true;

			if(!menu->is_separator())
			{
				error_if(menu->properties <= 0, TokenError::PropertyExpected, l->column - 1);
				
				if(!menu->title && !menu->image.defined)
					error(TokenError::PropertyTitleOrImageExpected, col_after_open_paren);

				if(menu->is_item())
				{
					skip();
					if(l->tok == L'{')
					{
						// parse menu commands property
						if(!parse_properties_commands(menu))
							return false;
					}
					menu->properties += !menu->commands.empty();
				}

				/*
				// fixme
				if(!menu->cmd->admin.expr && menu->cmd->admin.value == Privileges::None)
				{
					if(menu->owner->cmd->admin.value != Privileges::None)
						menu->cmd->admin.value = menu->owner->cmd->admin.value;
					else
					{
						menu->cmd->admin.inherit = true;
						menu->cmd->admin.expr = menu->owner->cmd->admin.expr;
					}
				}*/

				if(menu->is_menu())
					parse_menu(menu, false);
			}
			return true;
		}

		void Parser::parse_modify_items(uint32_t action, std::size_t source_start)
		{
			auto cache = context.Cache;
			if(source_start == static_cast<std::size_t>(-1)) source_start = l->index;
			const auto kind = action == IDENT_REMOVE ? "remove" : "modify";
			StudioNodeGuard studio_node(*this, kind, kind, source_start);
			std::unique_ptr<NativeMenu> item(new NativeMenu(true));
			set_source_identity(item.get(), source_start);
			if(eat().parse_modify_properties(item.get(), action))
			{
				item->source_end = l ? l->index : source_start;
				cache->statics.push_back(item.release());
			}
		}

		struct SETTING
		{
			uint32_t id = 0;
			auto_expr *value = nullptr;
			std::vector<SETTING> children;

			SETTING *find(const Ident &ident,  uint32_t i = 1)
			{
				SETTING *ret{};
				for(auto &setting : children)
				{
					if(setting.id == ident[i])
					{
						ret = (i == (ident.length() - 1)) ? &setting : setting.find(ident, ++i);
						break;
					}
				}
				return ret;
			}
		};

		void Parser::parse_settings(std::vector<SETTING> *settings, const Ident &id, bool imported)
		{
			if(!settings)
				return;

			auto ln = l->line;
			auto col = l->column;

			skip();

			Ident ident;
			if(id != 0)
				ident = id;
			else if(!parse_ident(ident, true))
				return;

			auto has_bracket = eat().l->next_is(L'{');
			
			if(!has_bracket && !l->is_assign())
			{
				if(l->eof && imported)
					return;
				error(TokenError::AssignExpected);
			}

			for(auto &setting : *settings)
			{
				if(ident == setting.id)
				{
					auto _setting = &setting;
					if(ident.length() > 1)
					{
						_setting = _setting->find(ident);
						if(!_setting) goto error_undefined;
					}

					if(has_bracket)
					{
						parse_settings(_setting, ident, imported);
						return;
					}

					if(!eat().l->next_if(l->is_assign()))
					{
						if(l->eof && imported)
							return;
						error(TokenError::AssignExpected);
					}

					*_setting->value = parse_expression();
					return;
				}
			}

		error_undefined:
			error(TokenError::IdentifierUndefined, col, ln);
		}

		void Parser::parse_settings(SETTING *setting, const Ident &id, bool imported)
		{
			auto cache = context.Cache;

			if(!setting)
				return;

			skip();

			auto ln = l->line;
			auto col = l->column;
			Ident _ident;
			prevCol = l->column;
			if(id != 0)
				_ident = id;
			else if(!parse_ident(_ident, true))
				return;
			
			skip();

			col = l->column;
			auto has_bracket = false;
			if(l->next_if(l->is_assign()))
			{
				if(_ident == setting->id)
				{
					*setting->value = parse_expression();
					return;
				}

				if(l->eof && imported)
					return;
				goto error_undefined;
			}
			else if(l->tok == L'{')
			{
				l->next();
				has_bracket = true;
			}
			
			while(true)
			{
				skip();

				if(l->eof || l->tok == L'}')
					break;

				skip();
				col = l->column;
				Ident ident;
				if(!parse_ident(ident, true))
				{
					if(l->next_is(L'{'))
						continue;

					if(l->is(L'}'))
						break;

					if(parse_variable(&cache->variables.global))
						continue;

					if(l->eof && imported)
						break;
					
					goto error_undefined;
				}
				
				if(ident[0] == IDENT_IMPORT)
				{
					if(ident.length() > 1)
						goto error_undefined;

					if(auto ret = load_import(l->line, l->column, true, false); ret > 0)
					{
						while(!l->eof) parse_settings(&setting->children, 0, true);
						pop_import();
						if(!has_bracket)
							return;
					}
					continue;
				}

				if(!ident.empty() && l->tok == L'}')
					break;
				parse_settings(&setting->children, ident);
			}

			expect_closeCurly();

			return;

		error_undefined:
			error(TokenError::IdentifierUndefined, col, ln);
		}

		void Parser::parse_theme()
		{
			const auto source_start = l ? l->index : 0;
			StudioNodeGuard studio_node(*this, "theme", "theme", source_start);
			auto st = &context.Cache->settings.theme;

			SETTING _item = { IDENT_ITEM, nullptr, {
				{ IDENT_TEXT, &st->item.text.color.value, {
					{ IDENT_NORMAL, &st->item.text.color.normal, {
						{ IDENT_DISABLED, &st->item.text.color.normal_disabled }
					}},
					{ IDENT_SELECT, &st->item.text.color.select, {
						{ IDENT_DISABLED, &st->item.text.color.select_disabled }
					}},
					{ IDENT_ALIGN, &st->item.text.align }
				}},
				{ IDENT_BACK, &st->item.back.value, {
					{ IDENT_NORMAL, &st->item.back.normal, {
					{ IDENT_DISABLED, &st->item.back.normal_disabled }
					}},
					{ IDENT_SELECT, &st->item.back.select, {
					{ IDENT_DISABLED, &st->item.back.select_disabled }
					}}
				}},
				{ IDENT_BORDER, &st->item.border.value, {
					{ IDENT_NORMAL, &st->item.border.normal, {
						{ IDENT_DISABLED, &st->item.border.normal_disabled }
					}},
					{ IDENT_SELECT, &st->item.border.select, {
						{ IDENT_DISABLED, &st->item.border.select_disabled }
					}}
				}},
				{ IDENT_OPACITY, &st->item.opacity},
				{ IDENT_RADIUS, &st->item.radius},
				{ IDENT_PREFIX, &st->item.prefix},
				{ IDENT_PADDING, &st->item.padding.value, {
					{ IDENT_LEFT, &st->item.padding.left },
					{ IDENT_TOP, &st->item.padding.top },
					{ IDENT_RIGHT, &st->item.padding.right},
					{ IDENT_BOTTOM, &st->item.padding.bottom }
				}},
				{ IDENT_MARGIN, &st->item.margin.value, {
					{ IDENT_LEFT, &st->item.margin.left },
					{ IDENT_TOP, &st->item.margin.top },
					{ IDENT_RIGHT, &st->item.margin.right},
					{ IDENT_BOTTOM, &st->item.margin.bottom }
				}}
			} };

			SETTING _shadow = { IDENT_SHADOW, &st->shadow.enabled, {
				{IDENT_ENABLED, &st->shadow.enabled},
				{IDENT_SIZE , &st->shadow.size},
				{IDENT_COLOR , &st->shadow.color},
				{IDENT_OPACITY, &st->shadow.opacity},
				{IDENT_OFFSET , &st->shadow.offset}
			} };

			SETTING _border = { IDENT_BORDER, &st->border.size, {
				{IDENT_ENABLED, &st->border.enabled},
				{IDENT_SIZE, &st->border.size},
				{IDENT_COLOR, &st->border.color},
				{IDENT_OPACITY, &st->border.opacity},
				{IDENT_RADIUS, &st->border.radius},
				{IDENT_PADDING, &st->border.padding.value, {
					{ IDENT_LEFT, &st->border.padding.left },
					{ IDENT_TOP, &st->border.padding.top },
					{ IDENT_RIGHT, &st->border.padding.right},
					{ IDENT_BOTTOM, &st->border.padding.bottom }
				}},
			} };

			SETTING _separator = { IDENT_SEPARATOR, &st->separator.color, {
				{IDENT_COLOR, &st->separator.color},
				{IDENT_OPACITY, &st->separator.opacity},
				{IDENT_SIZE, &st->separator.size},
				{IDENT_MARGIN, &st->separator.margin.value, {
					{ IDENT_LEFT, &st->separator.margin.left },
					{ IDENT_TOP, &st->separator.margin.top },
					{ IDENT_RIGHT, &st->separator.margin.right},
					{ IDENT_BOTTOM, &st->separator.margin.bottom }
				}}
			} };

			SETTING _symbol = { IDENT_SYMBOL, &st->symbol.color.value, {
				{IDENT_NORMAL, &st->symbol.color.normal, {
					{ IDENT_DISABLED, &st->symbol.color.normal_disabled }
				}},
				{IDENT_SELECT, &st->symbol.color.select, {
					{ IDENT_DISABLED, &st->symbol.color.select_disabled }
				}},
				{IDENT_CHECKMARK, &st->symbol.checkmark.value, {
					{ IDENT_NORMAL, &st->symbol.checkmark.normal, {
						{ IDENT_DISABLED, &st->symbol.checkmark.normal_disabled }
					}},
					{ IDENT_SELECT, &st->symbol.checkmark.select, {
						{ IDENT_DISABLED, &st->symbol.checkmark.select_disabled }
					}}
				}},
				{IDENT_BULLET, &st->symbol.bullet.value, {
					{ IDENT_NORMAL, &st->symbol.bullet.normal, {
					{ IDENT_DISABLED, &st->symbol.bullet.normal_disabled }
				}},
					{ IDENT_SELECT, &st->symbol.bullet.select, {
						{ IDENT_DISABLED, &st->symbol.bullet.select_disabled }
					}},
				}},
				{IDENT_CHEVRON, &st->symbol.chevron.value, {
					{ IDENT_NORMAL, &st->symbol.chevron.normal, {
					{ IDENT_DISABLED, &st->symbol.chevron.normal_disabled }
				}},
					{ IDENT_SELECT, &st->symbol.chevron.select, {
						{ IDENT_DISABLED, &st->symbol.chevron.select_disabled }
					}},
				}}
			} };

			SETTING _image = { IDENT_IMAGE, &st->image.enabled, {
				{IDENT_ENABLED, &st->image.enabled},
				{IDENT_COLOR, &st->image.color},
				{IDENT_GAP, &st->image.gap},
				{IDENT_SIZE, &st->image.size},
				{IDENT_GLYPH, &st->image.glyph},
				{IDENT_SCALE, &st->image.scale},
				{IDENT_ALIGN, &st->image.display},
				{IDENT_DISPLAY, &st->image.display}
			} };

			SETTING  _layout = { IDENT_LAYOUT, nullptr, {
				{IDENT_WIDTH, &st->layout.width},
				{IDENT_RTL, &st->layout.rtl},
				{IDENT_POPUP, &st->layout.popup.align, {
					{IDENT_ALIGN, &st->layout.popup.align}
				}}
			} };

			SETTING _gradient = { IDENT_GRADIENT, nullptr, {
				{IDENT_ENABLED, &st->gradient.enabled},
				{IDENT_GRADIENT_LINEAR, &st->gradient.linear},
				{IDENT_GRADIENT_RADIAL, &st->gradient.radial},
				{IDENT_GRADIENT_STOP, &st->gradient.stop},
			} };

			SETTING _font = { IDENT_FONT, &st->font.value, {
				{ IDENT_SIZE, &st->font.size },
				{ IDENT_NAME, &st->font.name },
				{ IDENT_FONT_WEIGHT, &st->font.weight },
				{ IDENT_FONT_ITALIC, &st->font.italic }
			} };

			auto sets = &context.Cache->settings;
			SETTING _tip = { IDENT_TIP, &sets->tip.enabled, {
				{IDENT_ENABLED, &sets->tip.enabled},
				{IDENT_DEFAULT, &sets->tip.normal},
				{IDENT_PRIMARY, &sets->tip.primary},
				{IDENT_INFO, &sets->tip.info},
				{IDENT_SUCCESS, &sets->tip.success},
				{IDENT_WARNING, &sets->tip.warning},
				{IDENT_DANGER, &sets->tip.danger},
				{IDENT_BORDER, &sets->tip.border},
				{IDENT_WIDTH, &sets->tip.width},
				{IDENT_OPACITY, &sets->tip.opacity},
				{IDENT_RADIUS, &sets->tip.radius},
				{IDENT_TIME, &sets->tip.time},
				{IDENT_PADDING, &sets->tip.padding.value, {
					{ IDENT_LEFT, &sets->tip.padding.left },
					{ IDENT_TOP, &sets->tip.padding.top },
					{ IDENT_RIGHT, &sets->tip.padding.right},
					{ IDENT_BOTTOM, &sets->tip.padding.bottom }
				}}
			} };

			SETTING _theme = { IDENT_THEME, &st->name, {
				{IDENT_NAME, &st->name},
				{IDENT_DARK, &st->dark},
				{IDENT_VIEW, &st->view},
				{IDENT_BACKGROUND, &st->background.color, {
					{IDENT_COLOR, &st->background.color},
					{IDENT_OPACITY, &st->background.opacity},
					{IDENT_EFFECT, &st->background.effect},
					{IDENT_TINTCOLOR, &st->background.tintcolor},
					{IDENT_IMAGE, &st->background.image},
					_gradient
				}},
				_font, _item, _shadow, _border, _separator, _symbol, _layout, _image, _tip
			} };

			auto _setting = &_theme;
			Ident ident;
			parse_ident(ident, true);

			if(ident.length() > 1)
			{
				_setting = _setting->find(ident);
				ident = ident.back();
			}
			parse_settings(_setting, ident, false);
		}

		void Parser::parse_settings()
		{
			const auto source_start = l ? l->index : 0;
			StudioNodeGuard studio_node(*this, "settings", "settings", source_start);
			auto sets = &context.Cache->settings;

			SETTING _modify = { IDENT_MODIFY, &sets->modify_items.enabled, {
				{IDENT_ENABLED, &sets->modify_items.enabled},
				{MENU_IMAGE, &sets->modify_items.image},
				{MENU_TITLE, &sets->modify_items.title},
				{MENU_VISIBILITY, &sets->modify_items.visibility},
				{MENU_PARENT, &sets->modify_items.parent},
				{MENU_POSITION, &sets->modify_items.position},
				{MENU_SEPARATOR, &sets->modify_items.separator},
				{IDENT_AUTO, &sets->modify_items.auto_image_group},
				{IDENT_REMOVE, nullptr, {
					{IDENT_DUPLICATE, &sets->modify_items.remove.duplicate },
					{IDENT_DISABLED, &sets->modify_items.remove.disabled },
					{IDENT_SEPARATOR, &sets->modify_items.remove.separator }
				}}
			} };

			SETTING _new = { CONFIG_NEW, &sets->new_items.enabled, {
				{IDENT_ENABLED, &sets->new_items.enabled},
				{MENU_IMAGE, &sets->new_items.image},
			} };

			SETTING _tip = { IDENT_TIP, &sets->tip.enabled, {
				{IDENT_ENABLED, &sets->tip.enabled},
				{IDENT_DEFAULT, &sets->tip.normal},
				{IDENT_PRIMARY, &sets->tip.primary},
				{IDENT_INFO, &sets->tip.info},
				{IDENT_SUCCESS, &sets->tip.success},
				{IDENT_WARNING, &sets->tip.warning},
				{IDENT_DANGER, &sets->tip.danger},
				{IDENT_BORDER, &sets->tip.border},
				{IDENT_WIDTH, &sets->tip.width},
				{IDENT_OPACITY, &sets->tip.opacity},
				{IDENT_RADIUS, &sets->tip.radius},
				{IDENT_TIME, &sets->tip.time},
				{IDENT_PADDING, &sets->tip.padding.value, {
					{ IDENT_LEFT, &sets->tip.padding.left },
					{ IDENT_TOP, &sets->tip.padding.top },
					{ IDENT_RIGHT, &sets->tip.padding.right},
					{ IDENT_BOTTOM, &sets->tip.padding.bottom }
				}}
			}};

			SETTING _settings = { CONFIG_SETTINGS, &sets->priority, {
				{ IDENT_PRIORITY, &sets->priority },
				{ IDENT_SHOWDELAY, &sets->showdelay },
				{ IDENT_SCREENSHOT, &sets->screenshot.enabled,{
					{ IDENT_ENABLED, &sets->screenshot.enabled },
					{ IDENT_DIRECTORY, &sets->screenshot.directory }
				}},
				{ IDENT_EXCLUDE, &sets->exclude.value,{
					{ IDENT_WHERE, &sets->exclude.value },
					{ IDENT_WINDOW, &sets->exclude.window },
					{ IDENT_PROCESS, &sets->exclude.process }
				}},
				_tip, _modify, _new,
			} };

			auto _setting = &_settings;
			Ident ident;
			parse_ident(ident, true);
			if(ident.length() > 1)
			{
				_setting = _setting->find(ident);
				ident = ident.back();
			}
			parse_settings(_setting, ident, false);
		}

		uint32_t Parser::parse_image_ident()
		{
			prevCol = l->column - 1;
			Hash h;
			
			auto isq = l->is_quote();
			wchar_t c = 0;

			auto is_normalize = [](const wchar_t &c)->bool
			{
				return !std::iswpunct(c) and !std::iswcntrl(c) and !iswblank(c);
			};

			if(isq) 
			{
				bool last_punct = false;
				auto q = l->tok;
				l->next(); // eat quote
				while(l->tok != q)
				{
					c = l->next();

					if(l->tok == 0)
						break;

					if(c == L'&')
					{
						if(l->tok != L'&') continue;
						l->next();
					}

					if(c == L'_')
					{
						if(last_punct)
							continue;
						last_punct = true;
						h.hash(c);
					}
					else if(is_normalize(c))
					{
						h.hash(c);
						last_punct = false;
					}
					else if(!last_punct)
					{
						if(!l->peek_is(q))
							h.hash(L'_');
						last_punct = true;
					}
				}
				error_if(!l->next_is(q), TokenError::CloseQuoteExpected);
			}
			else if(l->is_alpha())
			{
				while(l->is_iddigit())
				{
					h.hash(l->next());
				}
			}
			if(h.zero()) l->column = prevCol;
			return h;
		}

		//predefined constant variable
		bool Parser::parse_variable(Scope *variables, bool has_sign)
		{
			const auto source_start = l ? l->index : 0;
			skip();

			if(has_sign)
			{
				if(l->tok != L'$')
					return false;

				skip();
				prevCol = l->column;
				l->next(); // skip $
			}

			error_if(!l->is_ident(0), TokenError::VariableExpected, prevCol);
			Ident id;
			id.push_back(parse_ident(true));
			const auto name_end = l->index;
			// Localization files use the same assignment grammar as the runtime's
			// `loc` scope, but their declarations are labels rather than ordinary
			// configuration variables.  Emit the role-specific `setting` node so
			// source consumers can build the label table without re-parsing the
			// document or guessing from the file path.
			const auto node_kind = (&context.Cache->variables.loc == variables)
				? "setting" : "variable";
			StudioNodeGuard studio_node(*this, node_kind,
				studio_source_text(source_start, name_end), source_start);
			prevCol = l->column;
			expect_assign(true);
			prevCol = l->column;

			std::unique_ptr<Expression> expression(parse_expression());
			studio_node.SetExpression(expression.get());
			variables->set(id, expression.release());

			return true;
		}

		bool Parser::parse_image()
		{
			auto cache = context.Cache;
			const auto source_start = l ? l->index : 0;

			skip();

			if(l->tok != L'@')
				return false;
			
			l->next(); // skip @
			skip();

			prevCol = l->column;

			std::vector<uint32_t> ids;
			int i = 0;
			while(l->is_alpha() || l->is_quote())
			{
				if(auto ident = parse_image_ident(); ident)
				{
					i++;
					ids.push_back(ident);
				}
				skip();
				if(l->next_is(L',')) 
				{
					skip();
					if(l->tok == L'@')
					{
						l->next(); // skip @
						skip();
					}
				}
			}

			if(i == 0)
			{
				error_if(!l->eof, TokenError::IdentifierExpected, prevCol);
				return false;
			}

			StudioNodeGuard studio_node(*this, "image", "image", source_start);
			expect_assign(true);
			std::unique_ptr<Expression> expression(parse_root_expression());
			studio_node.SetExpression(expression.get());
			cache->add_image(std::move(ids), expression.release());
			return true;
		}

		// ... load data from disk and populate
		int Parser::load_import(size_t line, size_t col, bool ignore_failed, bool parse_import, std::size_t source_start)
		{
			skip();
			if(source_start == static_cast<std::size_t>(-1)) source_start = l ? l->index : 0;
			StudioNodeGuard studio_node(*this, "import", "import", source_start);
			
			if(parse_import)
			{
				if(!l->skip_import())
					return -1;
			}

			std::unique_ptr<Expression> epath(parse_root_expression());
			studio_node.SetExpression(epath.get());
			error_if(!epath, TokenError::ImportPathExpected, prevCol);

			if(m_syntaxOnly && !m_preview && epath && l && l->buffer)
			{
				// Keep an unresolved runtime import visible to managed callers at the
				// declaration span.  The native parser does not evaluate import paths
				// during source inspection, so only expressions whose projected shape
				// is plainly dynamic receive this warning; literal/interpolation and
				// concatenation shapes remain eligible for the managed literal resolver.
				const auto projected = StudioLanguage::ProjectNativeExpression(epath.get(),
					std::wstring_view(l->buffer, l->length), ExpressionSources);
				std::function<bool(const StudioLanguage::ExpressionNode&)> dynamic =
					[&](const StudioLanguage::ExpressionNode& expression)
				{
					if(expression.kind == "literal" || expression.kind == "identifier" ||
						expression.kind == "variable" || expression.kind == "interpolationText")
						return false;
					if(expression.kind == "interpolation" || expression.kind == "group" ||
						expression.kind == "binary")
						return std::any_of(expression.children.begin(), expression.children.end(),
							[&](const auto& child) { return dynamic(child); });
					return true;
				};
				if(dynamic(projected))
				{
					StudioLanguage::Diagnostic diagnostic;
					diagnostic.code = "LANG_IMPORT_DYNAMIC";
					diagnostic.message = "The import path depends on runtime values and was not resolved during source inspection.";
					diagnostic.severity = "warning";
					diagnostic.start = static_cast<int>((std::min)(source_start,
						static_cast<std::size_t>((std::numeric_limits<int>::max)())));
					const auto end = studio_end_position();
					diagnostic.length = static_cast<int>((std::min)(end >= source_start ? end - source_start : 0,
						static_cast<std::size_t>((std::numeric_limits<int>::max)())));
					diagnostic.remedy = "Open the resolved file explicitly or provide a restricted native preview context.";
					m_studioSyntax.diagnostics.push_back(std::move(diagnostic));
				}
			}

			if(m_syntaxOnly && !m_preview)
				return 0;

			if(m_preview)
			{
				studio_node.Finish();
				if(!context.Preview || !m_previewImport) return 0;
				auto value = context.Eval(epath.get()).move();
				if(context.Preview->failed || !value.is_string()) return 0;
				if(_imports.size() >= 32 || ++m_previewImportCount > 256)
				{
					context.Preview->Fail("IMPORT_LIMIT", L"The preview import graph exceeds its limit.");
					return 0;
				}
				std::wstring path, source;
				if(!m_previewImport(std::wstring(l->path.c_str()), std::wstring(value.to_string().c_str()), path, source))
				{
					context.Preview->Fail("IMPORT_UNAVAILABLE", L"This import is not available in the supplied workspace snapshot.");
					return 0;
				}
				for(const auto& active : _imports)
					if(active->path.equals(path.c_str()))
					{
						context.Preview->Fail("IMPORT_CYCLE", L"The preview import graph contains a cycle.");
						return 0;
					}
				StudioLanguage::Frontend limits(source);
				const auto shape = limits.Parse();
				if(std::any_of(shape.diagnostics.begin(), shape.diagnostics.end(), [](const auto& d) { return d.code == "LANG_LIMIT"; }))
				{
					context.Preview->Fail("IMPORT_LIMIT", L"The imported document exceeds its structural limit.");
					return 0;
				}
				auto lex = import_push();
				if(!lex->load_buffer(source.data(), source.size(), path.c_str()))
				{
					pop_import();
					context.Preview->Fail("IMPORT_INPUT", L"The supplied import could not be parsed.");
					return 0;
				}
				l = lex;
				skip();
				prevCol = l->column;
				return 1;
			}

			Object obj = context.Eval(epath.get()).move();

			if(obj.is_null())
				return 0;

			string path = obj.to_string().trim().move();

			if(path.empty())
				return 0;
			
			if(path.length() > 2)
			{
				if(!((path[1] == L':' && path[2] == L'\\') || (path[0] == L'\\' && path[1] == L'\\')))
					path = Path::Combine(l->location, path).move();
			}

			path = Path::FixSeparator(path).move();

			auto hash = path.hash();
			
			if(hash)
			{
				for(auto &h : m_imports)
				{
					if(h == hash)
					{
						//if (!ignore_duplicated)
						{
							__trace(L"line[%d] column[%d] already imported '%s'",
											line, col, path.c_str());
							//return false;
						}
						break;
					}
				}
				m_imports.push_back(hash);
			}

			auto lex = import_push();

			if(hash && lex->load_File(path, ignore_failed))
			{
				__trace(L"import '%s'", path.c_str());

				l = lex;
				skip();
				prevCol = l->column;
				return 1;
			}

			if(lex->error != TokenError::None)
			{
				Logger::Error(L"line[%d] column[%d], %s '%s'",
							  line, col, ParserException::errortostr(lex->error), l->path.c_str());
			}
			else
			{
				Logger::Warning(L"line[%d] column[%d] invalid import file, '%s'",
								line, col, path.c_str());
			}

			pop_import();

			return 0;
		}

		void Parser::parse_loc(bool has_curly, std::size_t source_start)
		{
			if(source_start == static_cast<std::size_t>(-1)) source_start = l ? l->index : 0;
			StudioNodeGuard studio_node(*this, "localization", "localization", source_start);
			if(has_curly)
			{
				expect_openCurly();
				studio_set_child_insert(l->index);
			}
			while(!l->eof)
			{
				if(has_curly && l->tok == L'}')
					break;
				parse_variable(&context.Cache->variables.loc, false);
			}
			if(has_curly)
				expect_closeCurly();
		}

		// load data from disk and populate
		void Parser::parse_config()
		{
			auto cache = context.Cache;
			while(!l->eof)
			{
				skip();
				preview_query_boundary();
				prevCol = l->column;
				
				if(l->peek_ident(IDENT_THEME))
				{
					parse_theme();
					continue;
				}
				else if(l->peek_ident(CONFIG_SETTINGS))
				{
					parse_settings();
					continue;
				}

				const auto source_start = l->index;
				Hash id = parse_ident();
				switch(id)
				{
					case IDENT_THEME:
						parse_theme();
						break;
					case CONFIG_SETTINGS:
						parse_settings();
						break;
					case CONFIG_SET:
					case CONFIG_MODIFY:
					case CONFIG_UPDATE:
					case CONFIG_CHANGE:
					case IDENT_REMOVE:
						parse_modify_items(id, source_start);
						break;
					case CONFIG_MENU:
					case CONFIG_ITEM:
					case CONFIG_SEP:
					case CONFIG_SEPARATOR:
					{
						std::unique_ptr<NativeMenu> item(new NativeMenu(&cache->dynamic));
						set_source_identity(item.get(), source_start);
						if(parse_menu_item(item.get(), id))
						{
							cache->dynamic.items.push_back(item.release());
							TotalMenuCount++;
						}
						break;
					}
					case IDENT_IMPORT:
					{
						skip();
						bool bloc = false;
						if(l->peek_ident(IDENT_LOC))
						{
							bloc = true;
							l->next(3);
						}
						else if(l->peek_ident(IDENT_LANG))
						{
							bloc = true;
							l->next(4);
						}

						auto ret = load_import(l->line, l->column, true, false, source_start);
						if(ret == 1)
						{
							if(bloc)
							parse_loc(false, source_start);
							else
								parse_config();
							pop_import();
						}
						break;
					}
					case IDENT_LANG:
					case IDENT_LOC:
					{
						parse_loc(true, source_start);
						break;
					}
					default:
					{
						if(id == 0)
						{
							if(l->eof)
								break;
							if(parse_variable(&cache->variables.global))
								break;
							if(parse_image())
								break;
						}
						error(TokenError::IdentifierConfigUnexpected, prevCol);
						break;
					}
				}
			}
		}

		//expected a ')'
		//params
		bool Parser::Load()
		{
			bool result = false;
			try
			{
				_theme = { 
					0, IDENT_BACKGROUND, {
						{ 0, IDENT_OPACITY },
						{ 0, IDENT_EFFECT }
					}
				};

				refresh_studio_syntax();
				// The lossless front end enforces structural limits before the
				// shared recursive runtime grammar runs.  A limit diagnostic must
				// stop syntax-only validation here; serializing it after parse_config
				// would be too late to protect the editor's thread stack.
				if(m_syntaxOnly && std::any_of(m_studioSyntax.diagnostics.begin(),
					m_studioSyntax.diagnostics.end(), [](const auto &diagnostic)
					{ return diagnostic.code == "LANG_LIMIT"; }))
					return false;

				if(_imports.empty() || (l->length == 0 && !m_error))
					return true;
			
				context.Runtime = false;

				auto cache = context.Cache;
				cache->dynamic.type = NativeMenuType::Menu;
				cache->dynamic.fso.set(TRUE);

				location = Path::Parent(l->path);
				if(m_syntaxLocalization)
					parse_loc(false, 0);
				else
					parse_config();

				result = /*parse_config() && */!m_error;
				if(result)
				{
				}
			}
			catch(const PreviewQueryComplete&)
			{
				result = !m_error;
			}
			catch(const ParserException&)
			{
				append_studio_diagnostic();
			}
			catch(...)
			{
				if(m_syntaxOnly)
				{
					if(!m_error)
					{
						m_error = true;
						error_code = TokenError::Unknown;
					}
					append_studio_diagnostic();
				}
				else
				{
		#ifdef _DEBUG
					Logger::Exception(__func__);
		#endif
				}
			}
			append_studio_diagnostic();
			project_studio_expressions();
			return result;
		}
	}
}
