[CmdletBinding()]
param(
    [string]$Filter,
    [string]$Configuration = "Debug",
    [switch]$NoBuild,

    [ValidateSet("Api", "UI", "All")]
    [string]$Target = "Api",

    [switch]$InstallUi,
    [switch]$InstallBrowsers
)

. (Join-Path $PSScriptRoot "_common.ps1")

if ($Target -in @("Api", "All")) {
    Assert-Command "dotnet" "Install the .NET 10 SDK required by global.json and make sure dotnet is on PATH."
}

if ($Target -in @("UI", "All")) {
    Assert-Command "npm" "Install Node.js 20.11.1 or later and make sure npm is on PATH."
}

Push-Location $script:RepoRoot
try {
    if ($Target -in @("Api", "All")) {
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
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }

    if ($Target -in @("UI", "All")) {
        Set-Location (Resolve-RepoPath "PoolSense.UI")
        if ($InstallUi -or -not (Test-Path ".\node_modules")) {
            Write-Step "Installing UI dependencies"
            npm ci
            if ($LASTEXITCODE -ne 0) {
                exit $LASTEXITCODE
            }
        }

        if ($InstallBrowsers) {
            Write-Step "Installing Playwright Chromium"
            npm exec -- playwright install chromium
            if ($LASTEXITCODE -ne 0) {
                exit $LASTEXITCODE
            }
        }

        Write-Step "Running Code Knowledge UI tests against http://localhost:4200"
        npm run test:code
        exit $LASTEXITCODE
    }

    exit 0
}
finally {
    Pop-Location
}