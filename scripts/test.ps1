[CmdletBinding()]
param(
    [string]$Filter,
    [string]$Configuration = "Debug",
    [switch]$NoBuild
)

. (Join-Path $PSScriptRoot "_common.ps1")

Assert-Command "dotnet" "Install the .NET 9 SDK and make sure dotnet is on PATH."

$testProject = Resolve-RepoPath "PoolSense.Api.Tests\PoolSense.Api.Tests.csproj"
$arguments = @("test", $testProject, "--configuration", $Configuration)

if ($NoBuild) {
    $arguments += "--no-build"
}

if ($Filter) {
    $arguments += @("--filter", $Filter)
}

Write-Step "Running API tests"
dotnet @arguments
exit $LASTEXITCODE