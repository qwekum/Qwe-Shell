# Shell Studio menu capture protocol

Shell Studio hosts the named pipe. The native extension is a client and never
creates a listener in `DllMain`. This keeps capture opt-in and lets Studio
cancel by closing its own connection.

For installation and the user workflow, see
[Install and use](using-shell-studio.md#install-for-the-full-live-workflow). The protocol
requires the matching native extension; an unmodified upstream Shell build
cannot publish these snapshots.

## Endpoint and framing

The endpoint is scoped to the current Windows user and session:

```text
\\.\pipe\QweShell.Studio.Capture.<user-sid>.<session-id>
```

`<user-sid>` is the canonical string returned by `ConvertSidToStringSidW` for
the native process token. `<session-id>` is returned by
`ProcessIdToSessionId`. Studio uses the same values. The Studio server must
create the pipe with current-user-only access (`PipeOptions.CurrentUserOnly`)
and `PipeDirection.InOut`; the native client additionally verifies the server
process token SID before using the connection.

The endpoint permits exactly one server instance. Studio creates its first pipe
instance synchronously before reporting capture as active. If the endpoint is
already owned or cannot be created, the client reports `CAPTURE_LISTENER`, keeps
`IsListening` false, and leaves the UI at **Capture menu**. If an established
listener later loses its ability to create the next pipe instance, it reports
the same diagnostic and returns the UI to the inactive state. A cancellation
requested by the user is a normal stop and does not produce that failure.

Every message is one little-endian, unsigned 32-bit byte count followed by
that many UTF-8 JSON bytes. The count must be between 1 and 4,194,304
inclusive. A peer that sends an invalid count, invalid UTF-8/JSON, unsupported
version, wrong session, or an oversized request is disconnected. JSON property
names use camel case. The transport is a byte stream, so the length prefix is
the only message boundary.

## Handshake

Studio starts its server before asking the user to right-click and sends one
request as soon as the native client connects:

```json
{
  "version": 1,
  "type": "capture.start",
  "captureId": "a-studio-generated-id",
  "sessionId": 3,
  "includeOriginal": true
}
```

`captureId` is an opaque, printable identifier chosen by Studio. The native
side accepts only letters, digits, `-`, `_`, `.`, and `:` and limits it to 128
bytes. `sessionId` must match the endpoint. The request contains no command,
settings, or arbitrary path to execute; paths and context are reported by the
native snapshot captured from the actual Shell invocation.

After a valid request, native sends:

```json
{
  "version": 1,
  "type": "capture.ready",
  "captureId": "a-studio-generated-id",
  "sessionId": 3
}
```

The native extension does not wait for the client during menu construction.
When the original system tree, an automatically materialized final tree, or
an observed final popup is available, it queues a `menu.snapshot` message on
its worker thread. The queue is bounded; if a client stops reading, the native
menu path remains non-blocking and the pipe is eventually closed.

## Snapshot messages

Each snapshot has an outer envelope and a `snapshot` object matching the
managed `MenuSnapshot` contract:

```json
{
  "version": 1,
  "type": "menu.snapshot",
  "captureId": "a-studio-generated-id",
  "phase": "original",
  "snapshot": {
    "version": 1,
    "captureId": "a-studio-generated-id",
    "phase": "original",
    "configPath": "C:\\...\\config.nss",
    "runtimeGeneration": "a-published-generation-id",
    "context": "explorer.selection",
    "contextCategory": "file",
    "parentPath": "",
    "paths": ["C:\\...\\item.txt"],
    "original": [],
    "entries": [],
    "diagnostics": []
  }
}
```

The `original` phase contains the native tree available to enumeration before Shell
filtering and insertion. Retained definitions that still need semantic materialization are marked explicitly. The
`final` phase contains the exact entries after Shell rules are applied. Once capture is armed, the extension reuses the
same `construct_popup_entries` popup-construction path as the live menu to materialize retained submenu definitions into a bounded,
request-owned semantic tree. Studio therefore does not require the user to open, scroll through, or hover every
submenu. A provider that cannot be replayed safely, a rejected preview expression, a cycle, or a capture bound leaves
that branch explicitly incomplete instead of inventing children.

A later final message for an actually opened nested popup carries that popup's snapshot-level `parentPath` (and
repeats it on each entry); an empty string identifies the root popup. Studio merges this observed update and any
painted appearance into the automatically discovered tree without discarding already captured descendants.
Observed popup data is an enrichment of the retained semantic tree, not a prerequisite for publishing it.

Each entry contains a zero-based `index` in the array that contains it, a
stable, context-constrained `id`, optional `stableId`
when Shell has a known identifier, an optional normalized `matchTitle`,
`kind` (`item`, `menu`, or `separator`), `origin` (`system` or `custom`),
state flags, `parentPath`, `childrenCaptured`, `trace`, and `children`. Native
command IDs and `HMENU` values are deliberately not included because they are
transient process state.

Optional presentation fields are `keys` (shortcut text), `radio` (a radio mark
instead of a check mark), and `isDefault` (the bold default entry). Older
snapshots omit these fields and retain the semantic defaults.

### Source, rule, settings, and completeness evidence

Structured evidence is optional and independently versioned with `evidenceVersion: 1`. Its absence means unknown;
Studio does not infer provenance or rule results from an entry title. Evidence may appear on the snapshot or on an
entry:

| Field | Meaning |
| --- | --- |
| `source` | Source file, UTF-16 node span/ID, whole-file SHA-256, and optional import-occurrence ID for the parsed definition that produced the entry or rule |
| `ruleOutcomes` | Ordered evaluated `modify`/`remove` results with `ruleId`, optional entry/source association, `outcome` (`matched`, `skipped`, `blocked`, `overwritten`, or `removed`), and an optional bounded reason |
| `propertyEffects` | Ordered affected property names with `effect` (`applied`, `skipped`, `blocked`, `overwritten`, or `removed`), optional proven value, and source association |
| `effectiveSettings` | Evaluated native gates. `modifyItems` represents `settings.modify` for existing entries; `modifyMenu` reports its separator/duplicate/disabled cleanup values; `modifyProperties` represents `settings.new` for custom definitions. Source references are included only when the parser can retain them. |
| `completeness` | `state` (`observed`, `materialized`, or `unavailable`), `childrenCaptured`, `complete`, optional numeric bounds that were reached, and warning diagnostics |

Source identity is evidence until Studio resolves the same file bytes, node, span, and import occurrence in the open
workspace. A stale hash, missing occurrence, duplicate marker, or ambiguous selector keeps the evidence readable but
disables source editing. When native provenance lacks an exact imported occurrence, the source payload is unavailable
for editing and carries `CAPTURE_SOURCE_OCCURRENCE_UNAVAILABLE` rather than a misleading partial identity. Studio-authored
native modifications carry an adjacent durable `shell-studio-rule:<id>` comment with a selector/scope fingerprint so
recapture and reopen update the same rule rather than appending a duplicate. A property edit is a scoped quick rule;
existing handwritten rules remain shared source, and **Open shared rule** identifies their broader effect before editing.

Captured custom definitions resolve to the native language document that owns them. Their `cmd`, `args`, conditions,
and other properties are parsed in declaration context and displayed as actual expression syntax nodes. Editing and
preview validation do not invoke commands. Native entries expose only representable Shell modification properties;
the Windows or extension command implementation remains opaque.

### Read-only materialization policy and bounds

Automatic submenu materialization uses a positive allowlist. It may inspect literal values and approved pure
control/math operations, plus read-only `str`, `sel`, `path`, `color`, `theme`, `view`, `this`, and closed mode/value
namespaces. The allowlist is intentionally smaller than the runtime function catalogue. `cmd` and `args` are retained
as syntax evidence; command dispatch is never invoked. Assignments, variable-mutating loops, unknown or unsupported
functions, and provider work that cannot be replayed safely fail closed and mark the affected branch unavailable.

The current native traversal bounds are depth 64, 4,096 items, 50,000 evaluation steps, and a 100 ms wall-clock
budget. Retained trace evidence is capped at 64 entries and 1,024 characters per trace; structured rule/property
evidence is capped at 128 records per kind. Serialization and queue limits remain independent gates. A cycle,
source/provider absence, rejected expression, or reached bound reports `state: unavailable`, `complete: false`, and a
specific diagnostic; omitted children are never represented as a complete subtree. If native provenance cannot retain
an exact import occurrence, it emits `CAPTURE_SOURCE_OCCURRENCE_UNAVAILABLE` and does not publish partial file/hash/span
identity as an editable source target.

### Rendered appearance

An optional `appearance` object has its own version inside the existing
version-1 snapshot envelope. New native publishers emit appearance version 2;
Studio also accepts legacy version 1 screen-captured imagery. A semantic
snapshot can arrive before painting; a subsequent final snapshot can carry
the native-rendered popup. Version 2 fields are:

| Field | Meaning |
| --- | --- |
| `version` | `2` for native-rendered imagery; legacy `1` remains readable |
| `source` | `native-renderer` for version 2 |
| `alphaMode` | `premultiplied` for version 2 |
| `desktopEffectsOmitted` | Desktop-dependent composition is not reproduced |
| `status` | `available` or `unavailable`; absence also means no captured pixels |
| `width`, `height` | Physical pixel dimensions of this popup image |
| `dpi` | Capture DPI, used to map physical pixels to desktop units |
| `pixels` | Base64, top-down premultiplied BGRA32, exactly `width * height * 4` decoded bytes; legacy version 1 is opaque BGRA32 |
| `rows` | `{ entryId, x, y, width, height }` hit targets in image coordinates |

Dimensions cannot exceed 2048 by 4096 pixels or 600,000 pixels in total; DPI
must be between 48 and 768. The complete envelope still fits the 4 MiB transport
limit. Unavailable appearances contain no pixels or rows. Studio rejects invalid
base64, invalid provenance/alpha metadata, invalid premultiplication (a color
channel greater than alpha), duplicate/unknown row identities, and rectangles
outside the bitmap. Version 1 still requires opaque pixels. Studio also bounds
retained decoded appearances to 16 MiB.

The native publisher composes already rendered rows with Shell's own background
and frame drawing into an offscreen image. It does not copy desktop pixels or
wait for DWM. The popup's existing evaluated entries and measured geometry are
authoritative; rendering never evaluates configuration, invokes commands, or
replays third-party owner-draw callbacks. Such row imagery is retained from the
original live drawing callback. Missing imagery produces a specific unavailable
diagnostic while preserving the semantic snapshot.

Menu interiors use the resolved theme's opaque background color, while native
frame/shadow alpha is retained. Desktop-dependent blur is omitted and identified
in the payload/UI. The result is a native-rendered preview, not verification of
the composited Explorer screen. Images must have complete visible-row coverage
and fit the existing bounds. Popup identities, cached resources, and deferred
publication are retired when the popup is destroyed or capture ends.
An internal capture epoch binds cached rows and publication to the accepted
request; reconnecting cannot reuse pixels from the previous capture, even if
the client repeats its capture ID. Selection redraws update the cached row
before publication. Scrolling clips row hit targets to the content viewport
and uses the same native arrow painter as the live menu. Row placement includes
the measured client-to-window origin, including nonclient scroll space.
If geometry changes
after painting, the publisher reports unavailable imagery rather than mixing
pixels from different viewport states.

Each appearance belongs to the entries in that popup's final snapshot. Studio
aggregates opened submenu appearances by `parentPath` in `submenuAppearances`;
the native wire update remains a single popup. Automatic semantic discovery does not paint hidden popup windows.
Opening or hovering a submenu can enrich appearance evidence, but it cannot make an unavailable semantic branch
complete or discard descendants already captured. Semantic completeness and pixel availability are independent:
`appearance: unavailable` removes no semantic entries. Native-rendered images are shown only for captured/recorded
states and labeled separately from legacy screen captures. An edited or configuration-only preview is explicitly a
structural preview and does not imply that runtime-dependent appearance or visibility has been evaluated. Capturing
does not apply edits.
Later root appearance updates retain captured children only when their unique
entry identity and path still match; submenu pixels are retained only with that
same child evidence.

`trace` is an ordered list recorded while the native menu is evaluated.  It
contains the condition result and action for matched rules (for example,
renamed, moved, disabled, hidden, or displayed) as well as failed conditions
that explain why a candidate was skipped.  The original phase keeps entries
that the active remove rules would discard and records those outcomes as
`would remove; retained for original evidence`; it is therefore safe for
Studio's Explain and Original inspectors to use the trace without evaluating
the configuration a second time.

Trace and structured-evidence collection are opt-in. When capture arms after a static rule has already
been evaluated, the snapshot reports `capture.static unavailable; capture armed
after evaluation` rather than reconstructing the result. Assignments, variable-mutating loops, command execution,
and unsupported preview capabilities fail automatic materialization closed. Serialization, node, item, evaluation,
time, and queue limits produce branch or capture diagnostics instead of reporting a silently truncated hierarchy as
complete. A synchronous third-party provider callback cannot be forcibly interrupted; elapsed-time checks apply at
the next boundary after it returns.

## Configuration generations

`configPath` and `runtimeGeneration` identify the retained native cache that
produced the snapshot, even when a newer cache has subsequently been published.
An empty generation is valid for a configuration without a Studio generation
marker, but cannot verify a newly applied Studio generation.

Studio publishes a `.studio-transaction.json` marker while replacing files and
a `.studio-generation` marker with the committed generation. The native
initializer defers loading while the transaction marker exists, checks the
root timestamp and generation marker, and builds a replacement cache before
publication. A failed replacement retains the previous valid cache. Old caches
remain retained while menu instances may reference them. This is the implemented
coordination contract; interruption/reload behavior still needs the live cases
in [acceptance.md](acceptance.md).

## Completion and cancellation

Studio may send either of these on the same connection:

```json
{
  "version": 1,
  "type": "capture.cancel",
  "captureId": "a-studio-generated-id",
  "sessionId": 3
}
```

```json
{
  "version": 1,
  "type": "capture.end",
  "captureId": "a-studio-generated-id",
  "sessionId": 3
}
```

Native replies with `capture.end`, using `reason` `cancelled` or
`client-ended`, then closes its side of the connection. Closing the Studio
pipe is also a cancellation boundary; native treats a broken pipe as the end
of capture and reconnects only while the current context remains alive.

Invalid initial requests receive a bounded `capture.error` object and the
connection is closed. A missing Studio server is normal: native retries in the
background and emits no snapshots until a valid `capture.start` has been
received.
