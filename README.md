# Local Downloader

Local Downloader is a Windows Native Messaging download manager prototype for Chrome and Microsoft Edge. The browser extension sends direct HTTP/HTTPS download tasks to a local .NET host named `com.local.fastdownloader`, and the host saves files under `Downloads\LocalDownloader`.

## Build

From this repository root:

```powershell
dotnet restore
dotnet build
```

## Test

```powershell
dotnet test
```

If the solution file is present, this is equivalent:

```powershell
dotnet test NativeDownloadManager.sln
```

## Publish The Native Host

```powershell
dotnet publish apps/host/LocalDownloader.Host/LocalDownloader.Host.csproj -c Release -r win-x64 --self-contained false
```

The installer scripts expect the published executable here:

```text
apps\host\LocalDownloader.Host\bin\Release\net10.0\win-x64\publish\LocalDownloader.Host.exe
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

## Right-Click Demo

1. Publish the native host.
2. Load the unpacked extension in Chrome or Edge.
3. Copy the browser's extension ID.
4. Run the matching install script with `-ExtensionId`.
5. Restart the browser if it already had the extension open.
6. Open a page with a direct HTTP/HTTPS file link.
7. Right-click the link and choose Download with Local Downloader.
8. Confirm the file appears under `Downloads\LocalDownloader`.

If the browser cannot connect to the host, recheck the generated manifest in `installers\generated`, confirm the `path` points to the published `.exe`, and confirm `allowed_origins` contains the exact unpacked extension ID.
