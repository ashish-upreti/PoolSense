[CmdletBinding()]
param(
    [switch]$Install
)

. (Join-Path $PSScriptRoot "_common.ps1")

Assert-Command "npm" "Install Node.js 20.11.1 or later and make sure npm is on PATH."

$uiPath = Resolve-RepoPath "PoolSense.UI"

Push-Location $uiPath
try {
    if ($Install -or -not (Test-Path ".\node_modules")) {
        Write-Step "Installing UI dependencies"
        npm ci
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }

    Write-Step "Starting PoolSense UI at http://localhost:4200"
    npm run dev -- --host localhost --port 4200
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}