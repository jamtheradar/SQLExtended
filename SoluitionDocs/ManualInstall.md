# SQLExtended — Manual (xcopy) Install

For test PCs where you want to drop files in and overwrite easily during debugging — no `VSIXInstaller.exe`, no admin prompts (if using the per-user path).

> The project's `CopyToSsms` MSBuild target already does this on the dev PC after every build. This guide replicates the same layout on another machine.

## Files to copy

After `dotnet build --configuration Release`, grab these from `SQLExtended\bin\Release\net48\`:

```
SQLExtended.dll
SQLExtended.pkgdef
extension.vsixmanifest                 (the build writes this into SQLExtended\; on a machine with no
                                        build, copy SQLExtended\source.extension.vsixmanifest and rename)
Resources\icon.png                     (the manifest names these by relative path - without them
Resources\preview.png                    Manage Extensions has no icon to draw)
LICENSE.txt                            (likewise, for the manifest's License element; the build stages
                                        it into SQLExtended\ from the repo root LICENSE)
Microsoft.Data.SqlClient.dll
Microsoft.SqlServer.TransactSql.ScriptDom.dll
Newtonsoft.Json.dll
System.Data.SQLite.dll
ICSharpCode.AvalonEdit.dll
x64\SQLite.Interop.dll
x86\SQLite.Interop.dll
```

Plus any other `*.dll` next to `SQLExtended.dll` in the build output that aren't already shipped with SSMS — when in doubt, copy the whole `net48\` folder contents (excluding `*.pdb` and the `.vsix` itself).

## Where to put them on the target PC

Pick **one** of these destinations:

### Option A — Per-user (no admin) — recommended for testing

```
%LOCALAPPDATA%\Microsoft\SSMS\22.0_6ee4710c\Extensions\SQLExtended\
```

The `22.0_6ee4710c` suffix can vary — open `%LOCALAPPDATA%\Microsoft\SSMS\` and use whichever `22.0_*` folder exists.

### Option B — Machine-wide (needs admin) — matches dev PC layout

```
C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\SQLExtended\
```

This is the same path the `CopyToSsms` MSBuild target writes to.

## Folder layout in the destination

```
SQLExtended\
├── SQLExtended.dll
├── SQLExtended.pkgdef
├── extension.vsixmanifest
├── Microsoft.Data.SqlClient.dll
├── Microsoft.SqlServer.TransactSql.ScriptDom.dll
├── Newtonsoft.Json.dll
├── System.Data.SQLite.dll
├── ICSharpCode.AvalonEdit.dll
├── LICENSE.txt
├── Resources\
│   ├── icon.png
│   └── preview.png
├── x64\
│   └── SQLite.Interop.dll
└── x86\
    └── SQLite.Interop.dll
```

## Make SSMS pick up the new files

Each time you replace files, **close SSMS** then clear the caches so it re-discovers the MEF exports and re-reads the pkgdef:

```powershell
Remove-Item "$env:LOCALAPPDATA\Microsoft\SSMS\22.0_6ee4710c\ComponentModelCache\*" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$env:LOCALAPPDATA\Microsoft\SSMS\22.0_6ee4710c\Extensions\*.cache" -Force -ErrorAction SilentlyContinue
```

(Same script as `clearSSMScache.ps1` in the repo root.)

Then start SSMS once with `/setup` to register the pkgdef, then normally:

```powershell
& "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Ssms.exe" /setup
& "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Ssms.exe"
```

`/setup` is only needed the **first** time after copying — subsequent file replacements just need a cache clear + restart.

## One-shot deploy script

Save as `deploy-to-ssms.ps1` next to your copied build output:

```powershell
param(
    [string]$Source = ".\net48",
    [string]$Dest   = "$env:LOCALAPPDATA\Microsoft\SSMS\22.0_6ee4710c\Extensions\SQLExtended"
)

# Stop SSMS if running
Get-Process Ssms -ErrorAction SilentlyContinue | Stop-Process -Force

# Wipe & copy
Remove-Item $Dest -Recurse -Force -ErrorAction SilentlyContinue
New-Item  $Dest -ItemType Directory -Force | Out-Null
Copy-Item "$Source\*" $Dest -Recurse -Force -Exclude *.pdb,*.vsix

# Make sure manifest is named correctly
if (Test-Path "$Dest\source.extension.vsixmanifest") {
    Move-Item "$Dest\source.extension.vsixmanifest" "$Dest\extension.vsixmanifest" -Force
}

# Clear caches
Remove-Item "$env:LOCALAPPDATA\Microsoft\SSMS\22.0_6ee4710c\ComponentModelCache\*" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Deployed to $Dest. Launch SSMS with /setup the first time." -ForegroundColor Green
```

## Verifying

1. Launch SSMS
2. **Tools → Extensions** — *SSMS Schema Viewer* should appear
3. Try `Ctrl+Shift+D` on a table name, or `Ctrl+K, Ctrl+Y` on selected SQL

## To uninstall

Close SSMS, delete the `SQLExtended\` folder you copied, clear the caches, restart.
