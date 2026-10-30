[CmdletBinding()]
param(
    [string]$ApiLaunchProfile = "http",
    [string]$Environment = "Development",
    [switch]$InstallUi
)

. (Join-Path $PSScriptRoot "_common.ps1")

Assert-Command "dotnet" "Install the .NET 10 SDK required by global.json and make sure dotnet is on PATH."
Assert-Command "npm" "Install Node.js 20.11.1 or later and make sure npm is on PATH."

$powerShell = (Get-Command "powershell.exe" -ErrorAction Stop).Source
$apiScript = Resolve-RepoPath "scripts\run-api.ps1"
$uiScript = Resolve-RepoPath "scripts\run-ui.ps1"

$apiArguments = @(
    "-NoExit",
    "-ExecutionPolicy", "Bypass",
    "-File", "`"$apiScript`"",
    "-LaunchProfile", "`"$ApiLaunchProfile`"",
    "-Environment", "`"$Environment`""
)

$uiArguments = @(
    "-NoExit",
    "-ExecutionPolicy", "Bypass",
    "-File", "`"$uiScript`""
)

if ($InstallUi) {
    $uiArguments += "-Install"
}

Write-Step "Opening API and UI dev servers in separate PowerShell windows"
Start-Process -FilePath $powerShell -ArgumentList $apiArguments -WorkingDirectory $script:RepoRoot
Start-Process -FilePath $powerShell -ArgumentList $uiArguments -WorkingDirectory $script:RepoRoot

Write-Host ""
Write-Host "API: http://localhost:5217"
Write-Host "UI:  http://localhost:4200"