# Native Browser Download Manager Design

## Goal

Build a legal IDM-style download manager prototype for Windows that can receive download tasks from both Microsoft Edge and Google Chrome through Native Messaging. The first version focuses on browser handoff, reliable file downloads, and a clear path to a fuller desktop UI later.

## Non-Goals

- Do not copy Internet Download Manager code, assets, names, or private behavior.
- Do not bypass paid services, DRM, streaming encryption, browser security prompts, or software activation.
- Do not implement video site sniffing in the first version.
- Do not require system-wide administrator installation for the first version.

## Architecture

The project has three main parts:

1. Browser extension
2. Native Messaging host
3. Download engine

The browser extension is a Manifest V3 extension shared by Edge and Chrome. It detects user-initiated downloads through context menus and selected browser download events, then sends a JSON task to the native host.

The Native Messaging host is a local executable registered per user in the Windows registry. Chrome and Edge start it on demand and communicate over stdin/stdout using the browser native messaging framing protocol.

The download engine runs inside the host process for the first version. It receives tasks, resolves file names, checks whether the server supports byte ranges, downloads either in segments or as a single stream, writes temporary files, and commits completed files atomically.

## Repository Layout

```text
idm/
  apps/
    host/
      src/
      tests/
  extension/
    manifest.json
    background.js
    content.js
    icons/
  installers/
    chrome-host-manifest.json
    edge-host-manifest.json
    install-chrome-host.ps1
    install-edge-host.ps1
    uninstall-host.ps1
  docs/
    superpowers/
      specs/
```

## Browser Extension

The extension provides:

- A context menu item named "Download with Local Downloader".
- A background service worker that opens the native messaging port.
- A download event handler that can cancel and hand off supported direct HTTP/HTTPS file downloads.
- A small options page later, not required for the first implementation.

The extension sends this task shape:

```json
{
  "type": "download.create",
  "id": "browser-generated-id",
  "url": "https://example.com/file.zip",
  "suggestedFilename": "file.zip",
  "referrer": "https://example.com/",
  "cookies": [],
  "userAgent": "browser user agent",
  "source": "context-menu"
}
```

Cookies are optional in the first implementation. When included later, they should only be sent for the current target URL and only when browser APIs allow access.

## Native Messaging Host

The host executable must:

- Read 4-byte little-endian message length prefixes from stdin.
- Read JSON messages of that length.
- Write responses with the same native messaging framing.
- Never write logs to stdout, because stdout is reserved for protocol messages.
- Log diagnostics to files under the app data directory.

Host name:

```text
com.local.fastdownloader
```

Per-user registry keys:

```text
HKCU\Software\Google\Chrome\NativeMessagingHosts\com.local.fastdownloader
HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.local.fastdownloader
```

Each key points to a browser-specific native host manifest file.

## Download Engine

The first version supports:

- HTTP and HTTPS URLs.
- HEAD probing for file size, file name, and range support.
- Multi-segment download when `Accept-Ranges: bytes` and content length are available.
- Single-stream fallback when range requests are unavailable.
- `.part` files for incomplete data.
- `.task.json` metadata files for resume state.
- Pause, resume, cancel, and retry hooks in the internal API.
- Final atomic rename from `.part` to the completed file path.

Default save location:

```text
C:\Users\Administrator\Downloads\LocalDownloader
```

The implementation should sanitize file names, prevent path traversal, and avoid overwriting existing files unless the user explicitly chooses an overwrite mode later.

## Task State

Task states:

- `queued`
- `probing`
- `downloading`
- `paused`
- `completed`
- `failed`
- `canceled`

The host returns immediate acknowledgement to the browser after accepting a task, then keeps progress in local task metadata. A richer UI can read the same metadata later.

## Error Handling

The host should return structured errors:

```json
{
  "type": "download.error",
  "id": "browser-generated-id",
  "code": "unsupported_url",
  "message": "Only http and https URLs are supported."
}
```

Important error codes:

- `unsupported_url`
- `host_protocol_error`
- `network_error`
- `server_no_range`
- `file_exists`
- `disk_error`
- `permission_denied`

Server range absence is not fatal; it only disables segmented downloading.

## Security Boundaries

- Accept only `http://` and `https://` URLs.
- Reject local file paths and command-like inputs.
- Treat all browser-provided names as untrusted.
- Store files only under the configured download directory in version one.
- Keep native messaging manifests extension-allowlisted by extension ID after the unpacked extension ID is known.
- Do not expose a local HTTP API in the Native Messaging version.

## Testing Plan

Unit tests:

- Native messaging frame parser and writer.
- URL validation.
- File name sanitization.
- Segment range calculation.
- Resume metadata read/write.

Integration tests:

- Host accepts a framed `download.create` message and returns `download.accepted`.
- Download engine downloads a small local HTTP file.
- Download engine falls back when the server does not support ranges.
- Interrupted `.part` download resumes from metadata.

Manual verification:

- Load unpacked extension in Edge.
- Register Edge native host and send a context-menu download.
- Load unpacked extension in Chrome.
- Register Chrome native host and send the same download.
- Confirm stdout contains only native messaging frames and logs go to file.

## First Implementation Plan Boundary

The implementation plan should build the smallest working flow:

1. Scaffold host executable and native messaging protocol.
2. Scaffold shared browser extension.
3. Add install scripts for Edge and Chrome native host manifests.
4. Implement single-stream download.
5. Add segmented download after the handoff flow works.
6. Add basic tests for protocol, validation, and downloader behavior.

The first working demo is successful when a right-clicked direct file link in Edge or Chrome is downloaded by the local host into `Downloads\LocalDownloader`.
