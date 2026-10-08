# Studio usability refinement

This pass refines the current native WPF workbench. It preserves the menu
preview, source-backed entry details, quick edits, expression editor, and
reviewed configuration workflow.

## Design direction

The interface follows the sequence of the work: open a configuration, choose
a capture context, inspect an entry, edit a draft, and review changes. Capture
belongs beside its context selector. Application commands belong in the header;
entry commands belong beside the editing surface.

```text
Shell Studio                         Open configuration   Review & apply   Theme
Context selector and target                                      Capture menu
Configuration location                                      Capture / draft state
Workspace | Expressions | Appearance | Tools | Templates | Native preview
Add entries   Move   Undo / Redo
Menu preview        | Entry details and expressions | Quick edit
Diagnostics
Operation status
```

The retained palette uses canvas `#F5F7FA`, panel `#FFFFFF`, text `#202630`,
secondary text `#596473`, action blue `#1764D8`, and divider `#E3E8EF`.
Segoe UI carries labels, values, and descriptions.
Content is left aligned, with wrapping values and resizable panes. Existing
dark and Windows high-contrast mappings remain authoritative.

The design review rejected adding decorative cards, metrics, or a navigation
rail: those would reduce space for the actual menu and its properties. The
distinctive element remains the menu itself. Spacing and type distinguish
sections, with borders reserved for controls and structural separation.

## Interaction changes

- Capture has one persistent action beside the context picker. The waiting
  pane explains capture and offers **Browse configuration**, which opens
  **Arrange entries** without editing source or substituting it for a capture.
- Entry creation, movement, and history commands are grouped. Short navigation
  labels leave room for all six pages at the minimum window width.
- **Quick edit** explains selection when empty and reveals entry actions once
  selected. **Source & capture details** retains provenance and explanation
  commands in an expandable section.
- The theme action names the destination theme. An unavailable review action
  explains that there are no pending changes.
- Templates provide an open-configuration entry point before enabling loading.
- Short windows hide the secondary application subtitle to preserve editing
  height. Context targeting, capture provenance, and draft state remain distinct.
- Exact capture targets remain visible at compact heights, with a full-path
  tooltip and an explicit clear action. Long paths occupy one line.
- Startup initializes guidance after the context choices are available.
  Capture guidance and empty arrangement results are mutually exclusive.

## Verification

The original baseline passed 58 Release WPF checks. The refined implementation
passes 63, including configuration browsing without edits, compact command
bounds, template prerequisites, exact-target visibility and clearing, and
startup without a configuration. Existing checks cover source-preserving
edits, undo/redo, capture boundaries, themes, accessible names, and dialogs.

The initial independent review passed 62 checks and scored the refinement
**8.5/10**. Its screenshot-labeling finding was corrected by explicitly selecting
the light palette before rendering the capture-start fixture. A subsequent
published-app smoke exposed overlapping startup guidance; the final startup
repair and added regression are tracked by the separate final source receipt.
The final independent pass ran **63 checks with zero failures**, verified all
six final source hashes, and scored the finished refinement **9/10**. It
confirmed the distinct startup/arrangement empty states and corrected light
render; this supersedes the earlier 8.5/10 assessment.

Local evidence is retained under
`src/studio/artifacts/checks/ux-professional-20260912/`, including the original
source snapshot, before/after renders, review report, logs, and versioned source
receipts. The 47-screen render matrix includes light, dark, high-contrast,
minimum-size, long-label, empty, diagnostic, and supporting-page states.

The standalone UI and ToolHost are built with Release, win-x64, and
self-contained publication into that evidence directory's `app/` subtree.
They reuse the existing native language and preview-worker binaries. This is a
local UI preview build; the installed extension and MSI are not replaced.
The refreshed executable also exited successfully after an offscreen startup
render without a configuration. Its rendered startup view was inspected, and
the final source hashes were rechecked after publication.

Offscreen renders and interaction checks cannot establish physical display,
assistive-technology, Explorer, installer, or human acceptance. At the minimum
window size, the narrow detail and inspector panes still require scrolling.
