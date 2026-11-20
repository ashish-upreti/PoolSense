[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot "_common.ps1")

Assert-Command "dotnet" "Install the .NET 10 SDK required by global.json and make sure dotnet is on PATH."
Assert-Command "npm" "Install Node.js 20.11.1 or later and make sure npm is on PATH."

Push-Location $script:RepoRoot
try {
    Push-Location (Resolve-RepoPath "PoolSense.UI")
    try {
        Write-Step "Building production UI"
        npm run build -- --configuration production
        if ($LASTEXITCODE -ne 0) {
            throw "Frontend build failed. If dependencies are missing, run npm ci in PoolSense.UI and retry."
        }
    }
    finally {
        Pop-Location
    }

    $uiRoot = Resolve-RepoPath "PoolSense.UI\dist\poolsense.ui\browser"
    if (-not (Test-Path (Join-Path $uiRoot "index.html") -PathType Leaf)) {
        throw "The frontend build did not produce browser\index.html."
    }

    $output = Resolve-RepoPath ("artifacts\publish\PoolSense-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
    New-Item -ItemType Directory -Path $output | Out-Null

    Write-Step "Publishing Release API and UI to $output"
    dotnet publish (Resolve-RepoPath "PoolSense.Api\PoolSense.Api.csproj") --configuration Release --self-contained false --output $output
    if ($LASTEXITCODE -ne 0) {
        throw "API publish failed. The incomplete package at '$output' must not be deployed."
    }

    Write-Step "Verifying published package"
    foreach ($required in @(
        "PoolSense.Api.dll",
        "PoolSense.Application.dll",
        "PoolSense.CodeRAG.dll",
        "PoolSense.Domain.dll",
        "PoolSense.Api.runtimeconfig.json",
        "web.config",
        "wwwroot\index.html"
    )) {
        if (-not (Test-Path (Join-Path $output $required) -PathType Leaf)) {
            throw "Missing published file '$required'. Do not deploy '$output'."
        }
    }

    $runtime = Get-Content (Join-Path $output "PoolSense.Api.runtimeconfig.json") -Raw | ConvertFrom-Json
    if ($runtime.runtimeOptions.tfm -ne "net10.0") {
        throw "Unexpected target framework. Do not deploy '$output'."
    }

    $assets = @(Get-ChildItem $uiRoot -File -Recurse)
    foreach ($asset in $assets) {
        $relative = $asset.FullName.Substring($uiRoot.Length + 1)
        $published = Join-Path (Join-Path $output "wwwroot") $relative
        if (-not (Test-Path $published -PathType Leaf)) {
            throw "Missing UI asset '$relative'. Do not deploy '$output'."
        }
        if ((Get-FileHash $asset.FullName -Algorithm SHA256).Hash -ne (Get-FileHash $published -Algorithm SHA256).Hash) {
            throw "UI asset mismatch '$relative'. Do not deploy '$output'."
        }
    }

    Write-Host "Verified .NET 10 assemblies, IIS configuration, and $($assets.Count) production UI files."
    Write-Host "Package ready. No server was changed. Preserve server-specific configuration and secrets during deployment."
    Write-Output $output
}
finally {
    Pop-Location
}
