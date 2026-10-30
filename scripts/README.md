# PoolSense Scripts

PowerShell helpers for common local workflows. Run them from the repository root, or with an absolute path from any folder.

.NET commands run from the repository root so that `global.json` controls SDK selection. Each script restores the caller's working directory when it exits and returns a nonzero exit code if a command fails.

## Prerequisites

- Windows PowerShell 5.1 or PowerShell 7. `run-dev.ps1` opens separate Windows PowerShell windows.
- The .NET 10 SDK compatible with the repository's `global.json` (`10.0.100` or a newer stable 10.0 feature band).
- Node.js 20.11.1 or later with npm for UI builds, development, and browser tests.
- API configuration and database setup described in the [root README](../README.md) before starting the API.

UI dependencies are installed with `npm ci` when `node_modules` is missing or an install switch is supplied. This uses `package-lock.json` without updating dependencies.

```powershell
.\scripts\run-dev.ps1
```

If your execution policy blocks local scripts, run the same command through PowerShell with a one-time bypass:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-dev.ps1
```

## Commands

```powershell
# Start API and UI in separate PowerShell windows.
.\scripts\run-dev.ps1

# Install UI packages first, then start both dev servers.
.\scripts\run-dev.ps1 -InstallUi

# Start only the API at http://localhost:5217.
.\scripts\run-api.ps1

# Select the HTTPS launch profile or override its environment.
.\scripts\run-api.ps1 -LaunchProfile https -Environment Staging

# Start only the Angular UI at http://localhost:4200.
.\scripts\run-ui.ps1

# Build the whole solution, or only one target.
.\scripts\build.ps1
.\scripts\build.ps1 -Target Api
.\scripts\build.ps1 -Target UI -Configuration Release
.\scripts\build.ps1 -Target CodeRAG

# Explicitly restore the selected target before building it.
.\scripts\build.ps1 -Target Api -Restore

# Run API tests, optionally with a VSTest filter.
.\scripts\test.ps1
.\scripts\test.ps1 -Filter "FullyQualifiedName~NyraConfigurationTests"
.\scripts\test.ps1 -Filter "FullyQualifiedName~CodeRAG"

# Reuse an existing test build for the selected configuration.
.\scripts\test.ps1 -Configuration Release -NoBuild
```

## Browser Tests

Start the UI in another terminal with `.\scripts\run-ui.ps1`. The current Code Knowledge Playwright tests require `http://localhost:4200` and mock API calls, so no running API or database is needed for these tests.

```powershell
# First run: install UI dependencies and the Chromium browser.
.\scripts\test.ps1 -Target UI -InstallUi -InstallBrowsers

# Subsequent runs reuse installed dependencies and browsers.
.\scripts\test.ps1 -Target UI

# Run API tests followed by UI tests; stop if API tests fail.
.\scripts\test.ps1 -Target All
```

`Api` remains the default test target. `-Filter`, `-Configuration`, and `-NoBuild` apply only to API tests; `-InstallUi` and `-InstallBrowsers` apply only to UI tests. Solution builds include the CodeRAG project and the Angular frontend.