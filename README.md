# Local Downloader

Local Downloader is an IDM-style download manager for Windows that takes over Chrome and Microsoft Edge downloads. A MV3 browser extension intercepts matching downloads (by file extension or MIME type), cancels the browser download, and hands the URL — together with the cookie header, referrer, and user agent — to a resident WPF desktop app that downloads it with a multi-connection segmented engine supporting pause, resume, and crash recovery.

Key features:

- **Dynamic segmentation (work stealing)**: downloads run on a pool of worker connections pulling from a shared segment queue instead of one static task per segment. An idle worker whose queue is empty splits the largest remaining in-flight segment (once it has more than 4MB left) so no connection sits idle just because the initial split was uneven.
- **Rate-limit self-throttling**: a worker that gets 403/418/429 or a refused connection backs off (2s/4s/8s… up to 30s) and puts its segment back on the queue instead of burning through retries — an automatic concurrency downgrade against throttled servers. The task only fails if every worker is simultaneously backed off with zero progress for 60 seconds straight.
- **Categorized save directories**: new downloads default to `Downloads\LocalDownloader\<分类>` (压缩包/程序/视频/音乐/文档/其他) based on file extension; toggle this off, or override the folder per download, in the confirmation popup or Settings.
- **Clipboard link watching**: copying an http/https link whose extension matches the intercept list pops the same IDM-style confirmation window used for browser downloads (source "clipboard"), with a 10-minute per-URL dedupe and permanent suppression once a link is canceled.

## Architecture

```
Browser (Chrome / Edge)
  └─ MV3 extension (extension/)
       │ chrome.runtime.connectNative long-lived port
       ▼
LocalDownloader.Host  — thin proxy, launched by the browser per port
       │ named pipe \\.\pipe\LocalDownloader.App
       │ (same 4-byte length-prefixed JSON framing as Native Messaging)
       ▼
LocalDownloader.App   — resident WPF app (tray icon, single instance)
  ├─ main window / confirmation popup / settings window
  └─ LocalDownloader.Core — shared segmented download engine
```

- The Host does no business parsing; it relays frames between stdio and the pipe, and launches `LocalDownloader.App.exe` from its own directory when the pipe is not listening (500ms retries, 5s timeout). If the App cannot be reached it returns `download.error` so the extension fails open to a normal browser download.
- The App is a single instance (`Local\LocalDownloader.App.SingleInstance` mutex). Closing the main window minimizes to the tray; only the tray Exit menu quits (pausing and persisting unfinished tasks first).
- The engine probes with `Range: bytes=0-0`. Range-capable downloads are split into up to 32 segments (default 8; segments never smaller than 256KB) and handed to a pool of worker connections that pull from a shared queue, written into a preallocated `.part` file, with per-segment progress persisted to `<file>.task.json` for resume. An idle worker with an empty queue steals half of the largest remaining in-flight segment instead of sitting idle; a worker that hits a rate limit backs off and requeues its segment rather than holding a connection open. Servers without Range support fall back to a single stream.
- Downloads land in `Downloads\LocalDownloader` by default, categorized into a subfolder by file type unless disabled in Settings; each task can also have its own save folder (set per download in the confirmation popup). Settings live in `%APPDATA%\LocalDownloader\settings.json`, the task registry in `%APPDATA%\LocalDownloader\tasks.json` (never contains cookies), and Host diagnostics in `%LOCALAPPDATA%\LocalDownloader\logs\`.

## Projects

| Path | Description |
|---|---|
| `apps/core/LocalDownloader.Core` | Class library: segmented download engine, IPC message contract, framing |
| `apps/host/LocalDownloader.Host` | Thin Native Messaging proxy (stdio ↔ named pipe) |
| `apps/app/LocalDownloader.App` | Resident WPF app: tray, three windows, pipe server, persistence |
| `apps/host/LocalDownloader.Tests` | Core + Host unit/integration tests (includes local ASP.NET Range test server) |
| `apps/app/LocalDownloader.App.Tests` | App service and ViewModel tests |
| `extension/` | MV3 extension: interception, cookie collection, settings sync, fail-open |
| `installers/` | Per-user Native Messaging registration scripts |

## Build

From this repository root:

```powershell
dotnet restore
dotnet build
```

## Test

```powershell
dotnet test NativeDownloadManager.sln
```

Extension tests (Node.js):

```powershell
node extension/background.test.js
```

## Publish

The Host launches `LocalDownloader.App.exe` from its own directory, so publish both projects into the shared `publish` directory:

```powershell
dotnet publish apps/host/LocalDownloader.Host/LocalDownloader.Host.csproj -c Release -r win-x64 --self-contained false -o publish
dotnet publish apps/app/LocalDownloader.App/LocalDownloader.App.csproj -c Release -r win-x64 --self-contained false -o publish
```

The installer scripts expect the executables here:

```text
publish\LocalDownloader.Host.exe
publish\LocalDownloader.App.exe
```

## Load The Browser Extension

Chrome and Edge use a generated unpacked extension ID. Install the native host after loading the extension, or reinstall it after you know the real ID.

For Chrome:

1. Open `chrome://extensions`.
2. Enable Developer mode.
3. Select Load unpacked.
4. Choose the `extension` directory.
5. Copy the 32-character extension ID shown on the extension card.

For Edge:

1. Open `edge://extensions`.
2. Enable Developer mode.
3. Select Load unpacked.
4. Choose the `extension` directory.
5. Copy the 32-character extension ID shown on the extension card.

Safe placeholder for examples:

```text
aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
```

Replace the placeholder with the actual unpacked extension ID before a real browser demo.

## Install Native Messaging

Preview the install without writing files or registry keys:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installers/install-chrome-host.ps1 -WhatIf
powershell -NoProfile -ExecutionPolicy Bypass -File installers/install-edge-host.ps1 -WhatIf
```

Install for Chrome:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installers/install-chrome-host.ps1 -ExtensionId <chrome-extension-id>
```

Install for Edge:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installers/install-edge-host.ps1 -ExtensionId <edge-extension-id>
```

The scripts generate manifests in `installers\generated` and register these per-user keys:

```text
HKCU\Software\Google\Chrome\NativeMessagingHosts\com.local.fastdownloader
HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.local.fastdownloader
```

To allow more than one unpacked extension ID, pass multiple IDs:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installers/install-chrome-host.ps1 -ExtensionId <id-one>,<id-two>
```

## Uninstall Native Messaging

Preview:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installers/uninstall-host.ps1 -WhatIf
```

Remove Chrome and Edge registrations:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installers/uninstall-host.ps1
```

## Manual Acceptance Checklist

Run through these in Chrome and Edge after publishing and installing:

1. Click a direct file link (e.g. a `.zip`) → the confirmation popup appears → Start Download → the main window shows multi-connection progress; the finished file matches the source.
2. Pause a running download, then resume it; the final file is complete and correct.
3. Close the browser mid-download; the download continues and completes in the App.
4. Choose "Return to Browser" in the confirmation popup; the browser downloads the file itself and is not intercepted a second time.
5. Download an attachment from a logged-in site (cookies required); the App downloads it successfully.
6. Exit the App from the tray, then click a download in the browser; the Host relaunches the App and the confirmation popup appears.
7. Kill the App process and remove/rename `LocalDownloader.App.exe`, then click a download; the browser fails open and downloads the file itself.
8. Add a new extension (e.g. `.xyz`) in the Settings window, reload the extension (or restart the browser); files of the new type are now intercepted.
9. Restart the App with unfinished downloads; they reappear in the list as Paused and can be resumed.
10. Incognito downloads and downloads started by other extensions are never intercepted.
