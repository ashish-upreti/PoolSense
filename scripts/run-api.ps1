[CmdletBinding()]
param(
    [string]$LaunchProfile = "http",
    [string]$Environment = "Development"
)

. (Join-Path $PSScriptRoot "_common.ps1")

Assert-Command "dotnet" "Install the .NET 9 SDK and make sure dotnet is on PATH."

$apiProject = Resolve-RepoPath "PoolSense.Api\PoolSense.Api.csproj"
$env:ASPNETCORE_ENVIRONMENT = $Environment

Write-Step "Starting PoolSense API with launch profile '$LaunchProfile' ($Environment)"
dotnet run --project $apiProject --launch-profile $LaunchProfile
exit $LASTEXITCODE