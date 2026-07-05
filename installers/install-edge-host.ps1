[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter()]
    [ValidatePattern('^[a-p]{32}$')]
    [string[]] $ExtensionId = @('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$hostName = 'com.local.fastdownloader'
$repoRoot = Split-Path -Parent $PSScriptRoot
$templatePath = Join-Path $PSScriptRoot 'native-host-manifest.template.json'
$generatedDir = Join-Path $PSScriptRoot 'generated'
$manifestPath = Join-Path $generatedDir 'edge-native-host-manifest.json'
$hostExePath = Join-Path $repoRoot 'apps\host\LocalDownloader.Host\bin\Release\net10.0\win-x64\publish\LocalDownloader.Host.exe'
$registryPath = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$hostName"

if (-not (Test-Path -LiteralPath $templatePath -PathType Leaf)) {
    throw "Native host manifest template was not found: $templatePath"
}

if (-not (Test-Path -LiteralPath $hostExePath -PathType Leaf)) {
    $message = "Published host executable was not found: $hostExePath. Run dotnet publish before installing."
    if ($WhatIfPreference) {
        Write-Warning $message
    }
    else {
        throw $message
    }
}

$allowedOrigins = @($ExtensionId | ForEach-Object { "chrome-extension://$_/" })
$allowedOriginsJson = ConvertTo-Json -InputObject $allowedOrigins -Compress
$escapedHostPath = $hostExePath.Replace('\', '\\')
$manifestJson = (Get-Content -LiteralPath $templatePath -Raw).
    Replace('{{HOST_PATH}}', $escapedHostPath).
    Replace('{{ALLOWED_ORIGINS_JSON}}', $allowedOriginsJson)

if ($PSCmdlet.ShouldProcess($manifestPath, 'Write Edge native host manifest')) {
    New-Item -ItemType Directory -Force -Path $generatedDir | Out-Null
    Set-Content -LiteralPath $manifestPath -Value $manifestJson -Encoding UTF8
}

if ($PSCmdlet.ShouldProcess($registryPath, "Register $hostName for Edge native messaging")) {
    New-Item -Path $registryPath -Force | Out-Null
    Set-Item -Path $registryPath -Value $manifestPath
}

Write-Host "Edge native messaging host manifest: $manifestPath"
Write-Host "Edge registry key: $registryPath"
Write-Host "Allowed extension IDs: $($ExtensionId -join ', ')"
