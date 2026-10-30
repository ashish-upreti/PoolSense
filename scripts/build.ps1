[CmdletBinding()]
param(
    [ValidateSet("Solution", "Api", "UI", "CodeRAG")]
    [string]$Target = "Solution",

    [string]$Configuration = "Debug",

    [switch]$Restore
)

. (Join-Path $PSScriptRoot "_common.ps1")

Assert-Command "dotnet" "Install the .NET 10 SDK required by global.json and make sure dotnet is on PATH."
if ($Target -in @("Solution", "UI")) {
    Assert-Command "npm" "Install Node.js 20.11.1 or later and make sure npm is on PATH."
}

switch ($Target) {
    "Api" { $project = Resolve-RepoPath "PoolSense.Api\PoolSense.Api.csproj" }
    "UI" { $project = Resolve-RepoPath "PoolSense.UI\PoolSense.UI.csproj" }
    "CodeRAG" { $project = Resolve-RepoPath "PoolSense.CodeRAG\PoolSense.CodeRAG.csproj" }
    default { $project = Resolve-RepoPath "PoolSense.sln" }
}

Push-Location $script:RepoRoot
try {
    $arguments = @("build", $project, "--configuration", $Configuration)
    if ($Restore) {
        Write-Step "Restoring $Target packages"
        dotnet restore $project
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }

        $arguments += "--no-restore"
    }

    Write-Step "Building $Target ($Configuration)"
    dotnet @arguments
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}