# Studio interface design

The [2026-09-12 usability refinement](ux-refinement-2026-09-12.md) records the
current command hierarchy, capture entry point, lighter property sections,
empty states, and local verification for this interface pass.

Studio is a Windows editing workbench with a menu preview, entry details, and an adjacent inspector. The context picker selects a category, file type group, extension, or concrete target for capture. Expressions, settings, tools, and templates retain their navigation and reviewed operations. Native window chrome, source preservation, and review gates remain authoritative.

Entry details now replace the passive property relationship map. They describe actual
configuration values and provide explicit expression-edit actions. Captured native
entries explain that Windows or the owning extension supplies the command, and
offer scoped label/visibility rule editing rather than invented command nodes.
Values and descriptions wrap at the available width without graph zoom or connectors.
The separate expression editor continues to show the real syntax nodes.

When a captured popup image covers only part of its semantic entry list, Studio
retains those pixels and lists the remaining entries below an explicit
"outside the captured image" notice. Inspecting those entries does not require
scrolling Explorer or manufacture pixels for uncaptured appearance.

The [current workspace redesign](redesign-2026-09-10.md) supersedes the earlier
layout and its scores recorded below. Its independent gates and remaining
qualification limits are tracked separately.

## Shared system

- Typography: Windows-provided Segoe UI, 13 DIP body, 12 secondary, 16 section, 20 page title; regular and semibold. No bundled fonts or icon assets.
- Spacing: 4, 8, 12, 16, 24 DIP. Controls have a 32 DIP minimum height; compact toolbar actions retain text labels and keyboard focus.
- Geometry: 4 DIP control radius, 6 DIP floating surface radius, 1 DIP borders, 2 DIP keyboard focus. Inline sections use alignment and spacing rather than nested cards.
- Surfaces: light gray canvas and white panels by default, with deliberately authored dark equivalents. The editor chrome has no blur, gradients, or persistent animation; captured popup pixels retain the actual menu's own appearance.
- Color: neutral foreground and secondary text, one blue accent, separate error, warning, and success tokens. Semantic text accompanies color. High contrast uses live Windows system colors.
- Layout: compact application commands, context picker and capture phase, popup-shaped menu preview, entry details, resizable inspector, and diagnostics disclosed when needed. An explicit Arrange entries view retains structural drag-and-drop. Short windows move supporting instructions into tooltips to preserve working space.

## Workflows and interaction states

The menu editor retains capture, rule scope, source/property editing, history,
drag ordering, templates, diagnostics, and the exact review/apply workflow.
Full-width selection, named controls, visible focus, explicit unavailable
actions, and a keyboard destination chooser make the dense editing surface
easier to scan and operate. Capture, edited preview, and verified states remain
separate.

The expression canvas uses focusable node controls, ordered connections, a
node selector, root/parent navigation, bounded zoom, and an exact read-only
source preview. Theme changes and layout-only operations retain inspector
drafts. Invalid expressions retain their source and a recovery path; very
large graphs fail with a specific visual-limit diagnostic. Source validation
does not execute expressions.

Entry details are source-backed. For a custom definition, the inspector shows
the parsed properties and their UTF-16 spans and opens the same native
expression nodes used by the expression editor. For a captured system entry,
the details view shows only evidence that the capture supplied: source and
import occurrence, rule outcomes, property effects, effective
`settings.modify` gates, and completeness diagnostics. A title or command
label alone never invents a source association or a command node.

Quick edits to a native entry use the selected context's managed rule. A
durable marker lets a later recapture or reopen update that rule; **Open shared
rule** is the explicit action for a broader handwritten rule. A stale hash,
missing import occurrence, duplicate marker, or ambiguous match leaves the
evidence readable and disables editing. A disabled effective setting disables
the affected edit action and explains which gate caused it.

Capture discovers retained submenu semantics through the native popup
constructor without invoking commands. Each branch reports `observed`,
`materialized`, or `unavailable` completeness and keeps limit, provider, and
policy diagnostics visible. Semantic children can therefore remain available
when no popup pixels were captured. Native-rendered pixels are a separate
appearance layer; an unavailable or partial image never removes the semantic
entry list.

Settings group definitions by source file and preserve unchanged field drafts
and disclosure state. Templates separate loading from saving. The Tools page
uses a resizable catalog/form layout, retains per-operation field drafts while
filtering, locks inputs during work, exposes cancellation, and reports real
operation counts only when available. Preview, review, elevation, typed
execution, and recovery remain distinct steps.

Choice, input, review, and captured-image dialogs share the same palette and
control states. Review source stays read-only, and an execution approval is
never the default Enter action. Native window chrome, resizing, cancellation,
and file dialogs are retained. Custom controls use standard WPF keyboard
behavior, with no persistent animation or ornamental effects.

## Earlier interface reviews

1. Shared components and representative menu editor: inspect real WPF renders and interaction checks before extending the design.
2. Expression canvas, settings, tools, templates, and dialogs: inspect the combined implementation and render matrix.

Each gate receives an independent scored critique against the user brief. A score below 8 requires revision, with at most three evaluations per gate. Scores and remaining limitations will be recorded here after review.

Gate 1, evaluation 1: **7/10**. The critic identified expanding long menu rows, inconsistent selection width, empty diagnostics left open after clearing, an original-menu action enabled without captured evidence, and inefficient minimum-size layout. The revision constrains rows to the viewport, gives selection a full row surface, groups history commands, collapses cleared diagnostics, disables unavailable capture inspection, and replaces unthemed scroll/disclosure chrome.

Gate 1, evaluation 2: **9/10**, passed. The critic inspected the corrected `ui-design-r2b` Window renders. The prior blockers were resolved; at 900 × 600 DIP the history commands wrap together and the inspector scrolls, with no overlapping content. The Release WPF harness passed 22 checks against the packaged native language library, including row sizing, command state, source-preserving edits, diagnostic disclosure, semantic contrast, accessible field names, and 500-entry lists. The initial review used the verifier's Debug checks; the revision was rebuilt and rerun in Release by the main agent.

Gate 2, evaluation 1: **9/10**, passed. Before the formal score, integration
tests and image inspection corrected a missing dialog namescope, review text
alignment, initial/resized canvas framing, and tool selection visibility. The
render harness now settles the WPF dispatcher before capture. The verifier
passed 30 Release checks and produced 34 PNGs in `ui-stage2-final-r3`; the
independent critic rebuilt and ran the same 30 checks, all passing, and
generated `ui-critic-final` evidence. Both runs used the packaged native
language library. The critic inspected the actual source and settled renders,
including all five pages in dark, light, and high-contrast mappings, minimum
canvas/tools views, and choice/input/review/capture-preview dialogs.

Remaining refinements: the 900 × 600 canvas retains the selected root but
requires scrolling to inspect more of the graph; its toolbar could be more
compact. Arrow/glyph commands could receive a more uniform icon treatment.
Elevation/configuration-writer status windows received source review only;
their real execution flows and visual states require a separately authorized
Windows qualification pass. The 144/192 DPI images validate render resolution,
not physical display or OS text scaling.

Offscreen WPF rendering and local interaction tests provide layout and behavior evidence; they do not establish physical-monitor DPI, assistive-technology, Explorer, installer, or human acceptance.
