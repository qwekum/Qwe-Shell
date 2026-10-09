# Studio stack integration — 2026-10-08

The owner authorized addressing [PR4](https://github.com/qwekum/Qwe-Shell/pull/4)
and merging it with [PR1](https://github.com/qwekum/Qwe-Shell/pull/1) into current
main. The stack includes the ten original Studio fixes, the additional
qualification changes, and PR4's two review corrections. Missing icon sizes use
the selected view mode's defaults during normalization, preview and execution;
explicit sizes remain unchanged. Readiness failures preserve their status even
without diagnostics. Regression tests exercise both contracts.

The integration retains main's branding and the required feature ancestor
`65df0e4f2ad4590c9964b9ab810bc094797b81af`. PR4 is merged into PR1 first; PR1 is
then merged into main. Exact reviewed source, commands, outcomes, package hashes
and resulting merge revisions belong in the PR descriptions and external
handoff. Merge execution is not asserted by this source document.

[PR2](https://github.com/qwekum/Qwe-Shell/pull/2) remains separate. Its README-LF
and independent staged-index checks are implemented locally but outside this
stack. No cloud or clean pinned Linux qualification is claimed for this stack.

The [historical candidate](release-candidate-1.9.20.md) identifies its own
uncommitted combined overlay and exact packages. Its guest results cannot
qualify newly built stack packages. Independent checks must use this revision's
outputs, with sequential restore/builds and audit enabled.

Release qualification remains incomplete: donor source omissions, automatic
unopened-submenu capture, full language/Explorer/transaction/template/installer
matrices, ARM64 prerequisites and clean pinned combined Linux setup remain open.
Persistent reboot/upgrade/recovery VM scenarios and recorded human acceptance
remain held by the owner. Merge approval does not close these gates or authorize
signing, public package distribution, Actions enablement or active-desktop changes.

Preserve configuration/import/asset trees and their hashes, recovery journals
and original backups before any install or apply. Follow the historical
candidate's reviewed backup/rollback route and conflict/integrity checks; retain
incompatible journals for reviewed restoration rather than fabricating state.
