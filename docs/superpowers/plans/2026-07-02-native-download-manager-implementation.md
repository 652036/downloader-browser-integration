# Native Download Manager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a working Edge/Chrome Native Messaging download manager MVP in `C:\Users\Administrator\Desktop\idm`.

**Architecture:** A Manifest V3 browser extension sends framed JSON tasks to a .NET native host executable. The host validates tasks, downloads HTTP/HTTPS files into `Downloads\LocalDownloader`, and keeps protocol logs off stdout.

**Tech Stack:** .NET 10 console app and xUnit tests for the host/download engine; plain JavaScript Manifest V3 extension; PowerShell install scripts for Chrome and Edge native messaging registry keys.

---

## File Structure

- `NativeDownloadManager.sln`: .NET solution.
- `apps/host/LocalDownloader.Host/LocalDownloader.Host.csproj`: console native host executable.
- `apps/host/LocalDownloader.Host/Program.cs`: native messaging main loop.
- `apps/host/LocalDownloader.Host/NativeMessaging.cs`: native messaging frame reader/writer.
- `apps/host/LocalDownloader.Host/DownloadRequest.cs`: browser task DTO.
- `apps/host/LocalDownloader.Host/DownloadEngine.cs`: HTTP downloader.
- `apps/host/LocalDownloader.Host/FileNameSanitizer.cs`: safe output file naming.
- `apps/host/LocalDownloader.Host/TaskStore.cs`: task metadata persistence.
- `apps/host/LocalDownloader.Tests/LocalDownloader.Tests.csproj`: xUnit test project.
- `apps/host/LocalDownloader.Tests/*.cs`: unit and integration tests.
- `extension/manifest.json`: shared Edge/Chrome extension manifest.
- `extension/background.js`: context menu and native messaging logic.
- `extension/content.js`: small helper for selected links.
- `extension/icons/*.svg`: simple local icons.
- `installers/native-host-manifest.template.json`: native host manifest template.
- `installers/install-chrome-host.ps1`: per-user Chrome native host registration.
- `installers/install-edge-host.ps1`: per-user Edge native host registration.
- `installers/uninstall-host.ps1`: removes both registrations.
- `README.md`: build, test, install, and browser loading instructions.

### Task 1: Scaffold .NET Solution and Tests

**Files:**
- Create: `NativeDownloadManager.sln`
- Create: `apps/host/LocalDownloader.Host/LocalDownloader.Host.csproj`
- Create: `apps/host/LocalDownloader.Tests/LocalDownloader.Tests.csproj`

- [ ] **Step 1: Create solution and projects**

Run:

```powershell
dotnet new sln -n NativeDownloadManager
dotnet new console -n LocalDownloader.Host -o apps/host/LocalDownloader.Host
dotnet new xunit -n LocalDownloader.Tests -o apps/host/LocalDownloader.Tests
dotnet sln NativeDownloadManager.sln add apps/host/LocalDownloader.Host/LocalDownloader.Host.csproj
dotnet sln NativeDownloadManager.sln add apps/host/LocalDownloader.Tests/LocalDownloader.Tests.csproj
dotnet add apps/host/LocalDownloader.Tests/LocalDownloader.Tests.csproj reference apps/host/LocalDownloader.Host/LocalDownloader.Host.csproj
```

Expected: solution contains host and test projects.

- [ ] **Step 2: Make host internals testable**

Modify `apps/host/LocalDownloader.Host/LocalDownloader.Host.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>LocalDownloader.Host</AssemblyName>
    <RootNamespace>LocalDownloader.Host</RootNamespace>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Run baseline tests**

Run:

```powershell
dotnet test NativeDownloadManager.sln
```

Expected: default xUnit test passes.

### Task 2: Native Messaging Protocol

**Files:**
- Create: `apps/host/LocalDownloader.Tests/NativeMessagingTests.cs`
- Create: `apps/host/LocalDownloader.Host/NativeMessaging.cs`
- Modify: `apps/host/LocalDownloader.Host/Program.cs`

- [ ] **Step 1: Write failing frame tests**

Test cases:

```csharp
using System.Text;
using LocalDownloader.Host;

namespace LocalDownloader.Tests;

public sealed class NativeMessagingTests
{
    [Fact]
    public async Task ReadMessageAsync_reads_little_endian_length_prefixed_json()
    {
        await using var stream = new MemoryStream();
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");
        stream.Write(BitConverter.GetBytes(payload.Length));
        stream.Write(payload);
        stream.Position = 0;

        var message = await NativeMessaging.ReadMessageAsync(stream, CancellationToken.None);

        Assert.Equal("{\"type\":\"ping\"}", message);
    }

    [Fact]
    public async Task WriteMessageAsync_writes_little_endian_length_prefixed_json()
    {
        await using var stream = new MemoryStream();

        await NativeMessaging.WriteMessageAsync(stream, "{\"type\":\"pong\"}", CancellationToken.None);

        var bytes = stream.ToArray();
        Assert.Equal(15, BitConverter.ToInt32(bytes.AsSpan(0, 4)));
        Assert.Equal("{\"type\":\"pong\"}", Encoding.UTF8.GetString(bytes, 4, 15));
    }
}
```

Run:

```powershell
dotnet test apps/host/LocalDownloader.Tests/LocalDownloader.Tests.csproj --filter NativeMessagingTests
```

Expected: fails because `NativeMessaging` does not exist.

- [ ] **Step 2: Implement native messaging framing**

Create `NativeMessaging.cs` with async read/write using 4-byte little-endian prefixes, UTF-8 JSON, and max message size validation.

- [ ] **Step 3: Verify protocol tests pass**

Run:

```powershell
dotnet test apps/host/LocalDownloader.Tests/LocalDownloader.Tests.csproj --filter NativeMessagingTests
```

Expected: pass.

### Task 3: Request Validation and File Name Safety

**Files:**
- Create: `apps/host/LocalDownloader.Tests/DownloadRequestTests.cs`
- Create: `apps/host/LocalDownloader.Host/DownloadRequest.cs`
- Create: `apps/host/LocalDownloader.Host/FileNameSanitizer.cs`

- [ ] **Step 1: Write failing validation tests**

Test cases cover accepting HTTPS, rejecting `file://`, stripping path traversal, and assigning fallback names.

- [ ] **Step 2: Implement DTO and sanitizer**

Implement `DownloadRequest`, `DownloadRequestValidator`, and `FileNameSanitizer` with `http`/`https` URL restriction and Windows-invalid character replacement.

- [ ] **Step 3: Verify validation tests pass**

Run:

```powershell
dotnet test apps/host/LocalDownloader.Tests/LocalDownloader.Tests.csproj --filter "DownloadRequestTests|FileNameSanitizerTests"
```

Expected: pass.

### Task 4: Single Stream Download Engine

**Files:**
- Create: `apps/host/LocalDownloader.Tests/DownloadEngineTests.cs`
- Create: `apps/host/LocalDownloader.Host/DownloadEngine.cs`
- Create: `apps/host/LocalDownloader.Host/TaskStore.cs`

- [ ] **Step 1: Write failing download test**

Use an in-process `HttpListener` serving a small file. Assert the engine writes the completed file under a temporary output directory and creates metadata.

- [ ] **Step 2: Implement single-stream download**

Implement `DownloadEngine.DownloadAsync` with `HttpClient`, `.part` writes, metadata update, and atomic move to final filename.

- [ ] **Step 3: Verify download test passes**

Run:

```powershell
dotnet test apps/host/LocalDownloader.Tests/LocalDownloader.Tests.csproj --filter DownloadEngineTests
```

Expected: pass.

### Task 5: Host Main Loop

**Files:**
- Create: `apps/host/LocalDownloader.Tests/HostMessageHandlerTests.cs`
- Create: `apps/host/LocalDownloader.Host/HostMessageHandler.cs`
- Modify: `apps/host/LocalDownloader.Host/Program.cs`

- [ ] **Step 1: Write failing handler tests**

Test `download.create` returns `download.accepted`, unsupported URLs return `download.error`, and invalid JSON returns `host_protocol_error`.

- [ ] **Step 2: Implement host handler**

Implement message dispatch separately from `Program.cs`, so tests call the handler directly. `Program.cs` only loops over stdin/stdout.

- [ ] **Step 3: Verify handler tests pass**

Run:

```powershell
dotnet test apps/host/LocalDownloader.Tests/LocalDownloader.Tests.csproj --filter HostMessageHandlerTests
```

Expected: pass.

### Task 6: Browser Extension

**Files:**
- Create: `extension/manifest.json`
- Create: `extension/background.js`
- Create: `extension/content.js`
- Create: `extension/icons/icon.svg`

- [ ] **Step 1: Create extension manifest**

Use Manifest V3 with permissions: `contextMenus`, `nativeMessaging`, `downloads`, `activeTab`, `scripting`; host permissions for `http://*/*` and `https://*/*`.

- [ ] **Step 2: Create background service worker**

Create context menu item, send `download.create` messages to `com.local.fastdownloader`, and surface host acknowledgement with browser notifications or console logs.

- [ ] **Step 3: Validate extension JSON**

Run:

```powershell
node -e "JSON.parse(require('fs').readFileSync('extension/manifest.json','utf8')); console.log('manifest ok')"
```

Expected: `manifest ok`.

### Task 7: Native Host Installers

**Files:**
- Create: `installers/native-host-manifest.template.json`
- Create: `installers/install-chrome-host.ps1`
- Create: `installers/install-edge-host.ps1`
- Create: `installers/uninstall-host.ps1`

- [ ] **Step 1: Build host**

Run:

```powershell
dotnet publish apps/host/LocalDownloader.Host/LocalDownloader.Host.csproj -c Release -r win-x64 --self-contained false
```

Expected: `LocalDownloader.Host.exe` exists under `bin\Release\net10.0\win-x64\publish`.

- [ ] **Step 2: Create install scripts**

Scripts write native host manifest JSON into `installers/generated` and register the manifest path under Chrome and Edge `HKCU` native messaging keys.

- [ ] **Step 3: Verify scripts parse**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installers/install-chrome-host.ps1 -WhatIf
powershell -NoProfile -ExecutionPolicy Bypass -File installers/install-edge-host.ps1 -WhatIf
```

Expected: scripts report intended registry paths without writing when `-WhatIf` is used.

### Task 8: README and Final Verification

**Files:**
- Create: `README.md`

- [ ] **Step 1: Document local build and install**

Include commands to test, publish, install native host for Chrome/Edge, load unpacked extension, and right-click a direct file link.

- [ ] **Step 2: Run full verification**

Run:

```powershell
dotnet test NativeDownloadManager.sln
dotnet publish apps/host/LocalDownloader.Host/LocalDownloader.Host.csproj -c Release -r win-x64 --self-contained false
node -e "JSON.parse(require('fs').readFileSync('extension/manifest.json','utf8')); console.log('manifest ok')"
```

Expected: tests pass, publish succeeds, manifest parses.

## Self-Review

- Spec coverage: extension, host, downloader, native manifests, validation, tests, and manual verification are covered.
- Placeholder scan: no TBD/TODO/FIXME placeholders are intended.
- Scope boundary: first demo is a right-click direct HTTP/HTTPS download into `Downloads\LocalDownloader`; segmented downloads are designed but can follow the handoff MVP if time runs short.
