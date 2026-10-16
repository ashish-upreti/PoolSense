# PoolSense Scripts

PowerShell helpers for common local workflows. Run them from the repository root, or with an absolute path from any folder.

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

# Start only the Angular UI at http://localhost:4200.
.\scripts\run-ui.ps1

# Build the whole solution, or only one target.
.\scripts\build.ps1
.\scripts\build.ps1 -Target Api
.\scripts\build.ps1 -Target UI -Configuration Release

# Run API tests, optionally with a VSTest filter.
.\scripts\test.ps1
.\scripts\test.ps1 -Filter "FullyQualifiedName~NyraConfigurationTests"
```