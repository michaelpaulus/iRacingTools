# Installs (or updates) the iRacing Command Center from this source folder.
# Run it with install.cmd (double-click), not from inside another app's sandbox.
#
#   install.cmd            build, replace the installed copy, add a Start menu shortcut, start it
#   install.cmd -NoStart   the same, without starting it afterwards

param([switch]$NoStart)

$ErrorActionPreference = 'Stop'
$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $here 'IRacingCommandCenter.csproj'
$target  = Join-Path $env:LOCALAPPDATA 'Programs\IRacingCommandCenter'
$exeName = 'IRacingCommandCenter.exe'
$keepBackups = 3

function Step($text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host ''; Write-Host "Install failed: $text" -ForegroundColor Red; exit 1 }

# 1. Build
Step 'Building (Release)'
& dotnet build $project -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { Fail 'the build failed (see above). Nothing was changed.' }
$built = Get-ChildItem (Join-Path $here 'bin\Release') -Recurse -Filter $exeName | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $built) { Fail "couldn't find $exeName in bin\Release after building." }
$source = $built.DirectoryName
Write-Host "Built: $source"

# 2. Close the running copy. Its own Exit (tray menu) restarts OneDrive and stops the helper apps it started;
#    killing it skips that, so ask first.
$running = @(Get-Process -Name 'IRacingCommandCenter' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Step 'Closing the running Command Center'
    if (Get-Process -Name 'iRacingSim64DX11' -ErrorAction SilentlyContinue) {
        Write-Host 'Note: iRacing is running. The plug keeps its current state while the Command Center is closed.' -ForegroundColor Yellow
    }
    Write-Host 'Right-click its tray icon and choose Exit. Waiting up to 2 minutes...'
    $deadline = (Get-Date).AddMinutes(2)
    while ((Get-Process -Name 'IRacingCommandCenter' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
    if (Get-Process -Name 'IRacingCommandCenter' -ErrorAction SilentlyContinue) {
        $answer = Read-Host 'Still running. Force it closed? (OneDrive stays paused if it paused it) [y/N]'
        if ($answer -notmatch '^(y|yes)$') { Fail 'the Command Center is still running. Nothing was changed.' }
        Get-Process -Name 'IRacingCommandCenter' | Stop-Process -Force
        Start-Sleep -Seconds 1
    }
    Write-Host 'Closed.'
}

# 3. Back up the installed copy (the last few are kept)
New-Item -ItemType Directory -Force $target | Out-Null
$installed = @(Get-ChildItem $target -File -ErrorAction SilentlyContinue)
if ($installed.Count -gt 0) {
    Step 'Backing up the installed copy'
    $backupRoot = Join-Path $target 'backups'
    $backup = Join-Path $backupRoot (Get-Date -Format 'yyyyMMdd-HHmmss')
    New-Item -ItemType Directory -Force $backup | Out-Null
    $installed | Copy-Item -Destination $backup
    Get-ChildItem $backupRoot -Directory | Sort-Object Name -Descending | Select-Object -Skip $keepBackups |
        Remove-Item -Recurse -Force
    Write-Host "Backup: $backup"
}

# 4. Copy the new build in
Step "Installing to $target"
Get-ChildItem $source -File | Copy-Item -Destination $target -Force
foreach ($dir in Get-ChildItem $source -Directory) { Copy-Item $dir.FullName -Destination $target -Recurse -Force }
$exe = Join-Path $target $exeName
Write-Host "Installed $((Get-Item $exe).LastWriteTime)"

# 5. Start menu shortcut
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'iRacing Command Center.lnk'
if (-not (Test-Path $shortcut)) {
    Step 'Adding a Start menu shortcut'
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    $link.TargetPath = $exe
    $link.WorkingDirectory = $target
    $link.IconLocation = "$exe,0"
    $link.Description = 'iRacing Command Center'
    $link.Save()
    Write-Host "Shortcut: $shortcut"
}

# 6. Start it
if (-not $NoStart) {
    Step 'Starting the Command Center'
    Start-Process -FilePath $exe -WorkingDirectory $target
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
