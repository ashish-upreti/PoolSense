[CmdletBinding()]
param(
    [string]$LaunchProfile = "http",
    [string]$Environment = "Development"
)

. (Join-Path $PSScriptRoot "_common.ps1")

Assert-Command "dotnet" "Install the .NET 10 SDK required by global.json and make sure dotnet is on PATH."

$apiProject = Resolve-RepoPath "PoolSense.Api\PoolSense.Api.csproj"
$launchSettingsPath = Resolve-RepoPath "PoolSense.Api\Properties\launchSettings.json"

function Get-ProfilePorts {
    param([string]$Profile)

    if (-not (Test-Path $launchSettingsPath)) { return @() }
    $settings = Get-Content $launchSettingsPath -Raw | ConvertFrom-Json
    $profileSettings = $settings.profiles.PSObject.Properties[$Profile]
    if (-not $profileSettings -or -not $profileSettings.Value.applicationUrl) { return @() }

    return $profileSettings.Value.applicationUrl.Split(';', [StringSplitOptions]::RemoveEmptyEntries) |
        ForEach-Object { ([Uri]$_.Trim()).Port }
}

function Assert-PortsAvailable {
    param([int[]]$Ports)

    foreach ($port in $Ports) {
        $listener = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $listener) { continue }

        $owner = Get-CimInstance Win32_Process -Filter "ProcessId = $($listener.OwningProcess)" -ErrorAction SilentlyContinue
        $ownerExe = if ($owner) { $owner.ExecutablePath } else { $null }

        if ($ownerExe -and $ownerExe.StartsWith($script:RepoRoot, [StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "PoolSense API is already running on port $port (PID $($listener.OwningProcess)). Stop it first to restart: Stop-Process -Id $($listener.OwningProcess)" -ForegroundColor Yellow
            exit 0
        }

        $ownerName = if ($owner) { $owner.Name } else { "unknown" }
        throw "Port $port is in use by '$ownerName' (PID $($listener.OwningProcess)). Free the port or change applicationUrl for profile '$LaunchProfile' in $launchSettingsPath."
    }
}

Push-Location $script:RepoRoot
try {
    Assert-PortsAvailable (Get-ProfilePorts $LaunchProfile)

    Write-Step "Starting PoolSense API with launch profile '$LaunchProfile' ($Environment)"
    dotnet run --project $apiProject --launch-profile $LaunchProfile `
        --environment "ASPNETCORE_ENVIRONMENT=$Environment" `
        --environment "DOTNET_ENVIRONMENT=$Environment"
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}