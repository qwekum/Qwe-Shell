# Shell Studio menu capture protocol

Shell Studio hosts the named pipe. The native extension is a client and never
creates a listener in `DllMain`. This keeps capture opt-in and lets Studio
cancel by closing its own connection.

For installation and the user workflow, see
[Build and run](build-and-run.md#install-and-use-live-capture). The protocol
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
When the original system tree or a final popup is available, it queues a
`menu.snapshot` message on its worker thread. The queue is bounded; if a
client stops reading, the native menu path remains non-blocking and the pipe
is eventually closed.

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
filtering and insertion, with lazy content marked explicitly. The `final` phase contains the exact entries for the
popup being constructed after Shell rules are applied. A final message for a
nested popup carries that popup's snapshot-level `parentPath` (and repeats it
on each entry); an empty string identifies the root popup. An unopened dynamic submenu
has `childrenCaptured: false` until its popup is opened and published.

Each entry contains a zero-based `index` in the array that contains it, a
stable, context-constrained `id`, optional `stableId`
when Shell has a known identifier, an optional normalized `matchTitle`,
`kind` (`item`, `menu`, or `separator`), `origin` (`system` or `custom`),
state flags, `parentPath`, `childrenCaptured`, `trace`, and `children`. Native
command IDs and `HMENU` values are deliberately not included because they are
transient process state.

`trace` is an ordered list recorded while the native menu is evaluated.  It
contains the condition result and action for matched rules (for example,
renamed, moved, disabled, hidden, or displayed) as well as failed conditions
that explain why a candidate was skipped.  The original phase keeps entries
that the active remove rules would discard and records those outcomes as
`would remove; retained for original evidence`; it is therefore safe for
Studio's Explain and Original inspectors to use the trace without evaluating
the configuration a second time.

Trace collection is opt-in. When capture arms after a static rule has already
been evaluated, the snapshot reports `capture.static unavailable; capture armed
after evaluation` rather than reconstructing the result. Serialization, node,
depth, and queue limits terminate capture with diagnostics instead of reporting
a silently truncated hierarchy as complete.

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
