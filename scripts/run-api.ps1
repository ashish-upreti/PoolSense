[CmdletBinding()]
param(
    [string]$LaunchProfile = "http",
    [string]$Environment = "Development"
)

. (Join-Path $PSScriptRoot "_common.ps1")

Assert-Command "dotnet" "Install the .NET 10 SDK required by global.json and make sure dotnet is on PATH."

$apiProject = Resolve-RepoPath "PoolSense.Api\PoolSense.Api.csproj"

Push-Location $script:RepoRoot
try {
    Write-Step "Starting PoolSense API with launch profile '$LaunchProfile' ($Environment)"
    dotnet run --project $apiProject --launch-profile $LaunchProfile `
        --environment "ASPNETCORE_ENVIRONMENT=$Environment" `
        --environment "DOTNET_ENVIRONMENT=$Environment"
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}