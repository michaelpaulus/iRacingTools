# Builds the Command Center and starts it from its build output (bin\Release\...). Run it with run.cmd.
#   run.cmd            build and start
#   run.cmd -NoStart   just build

param([switch]$NoStart)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $here 'IRacingCommandCenter.csproj'

# Windows locks a running exe, so the build can't replace it: wait for the Command Center to be closed.
if (Get-Process -Name 'IRacingCommandCenter' -ErrorAction SilentlyContinue) {
    Write-Host 'The Command Center is running: right-click its tray icon and choose Exit. Waiting (Ctrl+C to cancel)...'
    while (Get-Process -Name 'IRacingCommandCenter' -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 1 }
}

& dotnet build $project -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host 'The build failed (see above).' -ForegroundColor Red; exit 1 }
$exe = Get-ChildItem (Join-Path $here 'bin\Release') -Recurse -Filter 'IRacingCommandCenter.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName

# A Start menu shortcut, if you have one, follows the build. ("Start with Windows" is kept up to date by the app itself.)
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'iRacing Command Center.lnk'
if (Test-Path $shortcut) {
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    if ($link.TargetPath -ne $exe) {
        $link.TargetPath = $exe
        $link.WorkingDirectory = Split-Path $exe
        $link.IconLocation = "$exe,0"
        $link.Save()
        Write-Host "Start menu shortcut now opens $exe"
    }
}

if (-not $NoStart) {
    # Through Explorer, so it doesn't belong to this console: closing the window can't take it down.
    Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$exe`""
    Write-Host "Started $exe. It keeps running in the tray: you can close this window."
}
