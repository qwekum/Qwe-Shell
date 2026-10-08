# Shell Studio native language service

The standalone `ShellStudio.Language.dll` exports a side effect free syntax
front end:

```text
char* shell_studio_parse(const wchar_t* utf16Text, size_t utf16Length);
char* shell_studio_parse_localization(const wchar_t* utf16Text, size_t utf16Length);
char* shell_studio_capabilities();
void  shell_studio_free(void* pointer);
```

The returned buffers are UTF-8 JSON and are owned by the DLL until passed to
`shell_studio_free`. `shell_studio_parse` consumes exactly `utf16Length`
UTF-16 code units. Every `SyntaxToken`, `SyntaxNode`, `SyntaxProperty`, and
`ExpressionNode` offset and length is a UTF-16 code-unit offset, so it can be
used directly with `System.String.Substring` on Windows.

## Lossless document contract

The token stream includes whitespace, line and block comments, punctuation,
operators, identifiers, numbers, strings, interpolated strings, colors,
character escapes, unknown characters, and a zero-length `eof` token. Joining
all token `text` values except `eof` reproduces the input exactly. Token text is
UTF-8; offsets remain UTF-16. Line and column are one-based and count CRLF as
one line break.

Nodes retain the source span of the declaration and the exact source text of
each expression. `propertyInsert` points immediately after a property-list
opening parenthesis; when a declaration has a body but no property list it
points immediately before the body brace. `childInsert` points immediately
before the closing body brace. A value replacement should use a property's
`valueStart` and `valueLength`; these spans exclude surrounding trivia.

The parser never opens an import, expands an environment variable, invokes a
command, evaluates an expression, or loads Explorer state. Literal and
runtime-dependent import handling belongs to the managed workspace. A dynamic
import can be represented by a warning, while the original expression remains
available for review.

Resolved `import lang` and `import loc` files use the localization entry point,
which validates with the runtime's localization grammar. Ordinary configuration
parsing remains unchanged. The workspace derives this role from the shared
frontend's tokens, retains it through edits and undo/redo, and diagnoses a file
imported in conflicting roles. Neither entry point evaluates expressions.

The front end bounds source and tree growth before returning a document. It
also preflights the complete JSON shape, including escaped text, before the
managed boundary serializes it. A result that exceeds the native limits gets
one `LANG_LIMIT` diagnostic and the JSON adapter returns a bounded diagnostic
document instead of attempting an oversized allocation.

## Visual construct inventory

The syntax tree has dedicated visual kinds for the configuration constructs
implemented by the runtime grammar:

| Source construct | Node kind | Visual representation |
| --- | --- | --- |
| `menu(...) { ... }` | `menu` | Menu container with properties and ordered children |
| `item(...) { ... }` | `item` | Command item with property editor and optional statements |
| `separator` / `sep` | `separator` | Separator item |
| `modify(...)` / `remove(...)` | `modify` / `remove` | Rule declaration |
| `settings { ... }` | `settings` | Nested setting declarations |
| `theme { ... }` | `theme` | Nested appearance declarations |
| `import ...` | `import` | Import declaration with source-linked path expression |
| `loc { ... }` / `lang { ... }` | `loc` / `lang` | Localization declarations |
| `@name = ...` | `image` | Image definition and expression |
| `$name = ...` | `variable` | Variable assignment |
| `if`, `for`, `foreach` | matching expression kinds | Ordered control-flow expression |

Expressions use ordered children to preserve evaluation order. The kinds are
`literal`, `interpolation`, `environment`, `identifier`, `variable`, `call`,
`if`, `for`, `foreach`, `while`, `unary`, `binary`, `assignment`, `ternary`,
`array`, `group`, `statement`, and `unknown`. Function calls contain argument
children; binary and assignment nodes contain left then right children;
ternaries contain condition, true branch, then false branch; statements and
arrays retain source order. The `text` span is always the exact original
source, including quotes and operators.

The generic node path intentionally retains genuinely unknown syntax for safe
inspection and reports `GRAPH_UNMAPPED`; it does not promote that syntax to
visual coverage. Data-defined identifiers are different: localization, image,
SVG, runtime menu-id, and imported-variable member families use the reviewed
typed identifier/value/call controls, so their names do not need a finite
enumeration. `shell_studio_capabilities()` reports visual coverage complete
only when every source-dispatched callable has an insertion, every dynamic
family probe parses, and no source hash is unmapped. Runtime semantic
equivalence remains an independent, explicitly incomplete gate. The current
catalogue is emitted in
[`function-coverage.json`](function-coverage.json). Its counts and exclusions
are machine-readable; every advertised insertion is checked by the exported
native parser and the managed test suite. The artifact records SHA-256 hashes for
`IdentHash.h`, `Verification.cpp`, `FuncExpression.cpp`, and the function
documentation used to build it. `extract_verifier_inventory.py` derives
candidate paths from nested verifier switches using hash-verified spellings.
`generate_function_inventory.py` combines those paths with documentation,
checks native acceptance, and records rejected candidates separately. Unknown
imported roots are not promoted to built-ins merely because syntax-only parsing
allows them. Default/dynamic identifier families require separate source review;
finite catalogue coverage is not a claim to enumerate every runtime string.

Named localization, image, SVG, menu-ID, and imported function/member paths use
**Named member or imported function** in the same picker. It collects an
identifier, value/call shape, and ordered argument count, then validates the
result before adding nodes. The `dynamicFamilies` records distinguish syntax
checks from runtime symbol resolution. There is no evaluation during insertion.

Unsupported candidates remain recorded with reasons. `command.random` has no
command dispatch branch, and package aliases' `title` member has no runtime
result branch. The shared verifier now rejects both instead of accepting
constructs that cannot produce their advertised behavior. Existing source
remains inspectable with diagnostics.

To refresh the inventory, first build the canonical native DLL from the current
parser sources, run `python src/studio/tools/generate_function_inventory.py`,
then rebuild the DLL and run the managed native catalogue test. The generator
uses the existing DLL only for syntax checks; the second build embeds the
new metadata. Source hashes make subsequent drift visible to the tests.

The catalogue's templates use the minimum accepted arity. The picker offers
accepted argument counts through 32, including non-contiguous sets such as
`color`'s 1, 3, or 4 arguments. `probeLimit: 32` is the editor's insertion limit,
not a claim that variadic runtime functions stop at 32. Additional branches can
be added on the canvas and are checked by the native parser. Namespace entries such as `io.attribute` and
`io.datetime` carry a concrete member example for insertion and a separate
member pattern; a placeholder-only template is not treated as coverage.

Diagnostics carry a stable code, severity, message, UTF-16 source span, and an
optional node id and remedy. Common codes include
`LANG_COMMENT_UNTERMINATED`, `LANG_STRING_UNTERMINATED`, `LANG_STRING_NEWLINE`,
`LANG_PAREN`, `LANG_CURLY`, `LANG_BRACKET`, `LANG_PROPERTY_NAME`,
`LANG_PROPERTY_VALUE`, `LANG_IMPORT_PATH`, `LANG_IMPORT_DYNAMIC`, and
`LANG_EXPRESSION`.

## Source sharing and qualification boundary

`src/dll/src/Parser/LanguageFrontend.h/.cpp` supplies the lossless tree. The
standalone DLL also calls the shared `Parser::Load` syntax-only path with an
in-memory lexer, private cache, and disabled import/evaluation side effects.
The existing runtime parser remains authoritative for grammar and verifier
diagnostics. Resource-limit preflight runs before the recursive shared parser.
Native unit tests establish lexical and syntax-tree behavior only.
They do not establish runtime semantic equivalence, Explorer behavior,
installer behavior, CI, or human visual acceptance.

## Source association and imports

The managed workspace resolves configuration imports against the current
unsaved document set and retains each import occurrence's identity. A source
reference can carry the file path, declaration span, node ID, current-file
hash, and import-occurrence ID. The workspace validates all of those facts
before enabling an edit; a stale file, changed span, missing occurrence, or
ambiguous declaration remains inspectable as evidence and produces a
diagnostic instead of silently rebinding the edit.

Files opened explicitly or left dirty after an import is removed remain
available for source inspection. They are marked detached and are excluded
from effective preview evaluation until the root's current import graph
reaches them again. Configuration, localization, and language import roles
are retained through source edits and undo/redo; conflicting roles for one
file are diagnosed. None of these workspace operations evaluates a command.

## Preview evaluation and brokered reads

Native preview uses the runtime parser and expression objects with a
request-owned capability policy. Pure evaluation remains inside the worker.
Files, environment values, registry values, and image/font/icon resources are
available only after the user enables **Preview read scopes** for the open
workspace. The managed broker snapshots those exact reads by revision; a
configuration or template cannot enlarge the policy, and network or mutation
requests remain unavailable.

Broker entries are matched by canonical function identity and the complete
serialized argument list. One registry key scope supplies `reg.exists(key)`
and `reg.get(key)`; one named-value scope supplies `reg.exists(key, value)` and
`reg.get(key, value)`. The documented bare `reg(...)` read alias is normalized
to `reg.get` at the native broker boundary while retaining the exact arguments,
so it cannot broaden the granted hive, key, or value. Missing, disabled, stale,
or mismatched reads return an explicit unavailable result. These preview-read
rules are separate from automatic context-menu materialization, whose smaller
positive allowlist and completeness reporting are documented in
[capture-protocol.md](capture-protocol.md#read-only-materialization-policy-and-bounds).
