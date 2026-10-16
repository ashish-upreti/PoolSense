[CmdletBinding()]
param(
    [ValidateSet("Solution", "Api", "UI")]
    [string]$Target = "Solution",

    [string]$Configuration = "Debug",

    [switch]$Restore
)

. (Join-Path $PSScriptRoot "_common.ps1")

Assert-Command "dotnet" "Install the .NET 9 SDK and make sure dotnet is on PATH."

if ($Restore) {
    Write-Step "Restoring solution packages"
    dotnet restore (Resolve-RepoPath "PoolSense.sln")
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

switch ($Target) {
    "Api" { $project = Resolve-RepoPath "PoolSense.Api\PoolSense.Api.csproj" }
    "UI" { $project = Resolve-RepoPath "PoolSense.UI\PoolSense.UI.csproj" }
    default { $project = Resolve-RepoPath "PoolSense.sln" }
}

Write-Step "Building $Target ($Configuration)"
dotnet build $project --configuration $Configuration
exit $LASTEXITCODE