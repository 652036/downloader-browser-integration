[CmdletBinding(SupportsShouldProcess = $true)]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$hostName = 'com.local.fastdownloader'
$registryPaths = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$hostName"
)

foreach ($registryPath in $registryPaths) {
    if (Test-Path -LiteralPath $registryPath) {
        if ($PSCmdlet.ShouldProcess($registryPath, "Remove $hostName native messaging registration")) {
            Remove-Item -LiteralPath $registryPath -Force
        }
    }
    else {
        Write-Host "Registration not found: $registryPath"
    }
}
