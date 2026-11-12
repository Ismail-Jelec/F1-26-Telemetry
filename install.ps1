# Builds F1 26 Overlay Studio and installs it as a normal Windows app:
#   - app files in %LOCALAPPDATA%\F126Overlay\app
#   - Start menu entry "F1 26 Overlay Studio" (searchable with the Windows key)
#   - desktop shortcut
# Run it again after `git pull` to update the installed app.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'F126Overlay\F126Overlay.csproj'
$dest = Join-Path $env:LOCALAPPDATA 'F126Overlay\app'
$exe = Join-Path $dest 'F126Overlay.exe'
$name = 'F1 26 Overlay Studio'

Write-Host ""
Write-Host "  $name - installer" -ForegroundColor Red
Write-Host ""

# Close the app if it's running, otherwise its files can't be replaced.
$running = Get-Process F126Overlay -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Closing the running app..."
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

Write-Host "Building (this takes a moment)..."
dotnet publish $project -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $dest --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Build failed. Scroll up for the error and send it to Claude." }

$shell = New-Object -ComObject WScript.Shell
function New-Shortcut([string]$path) {
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $dest
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = 'Telemetry overlays for F1 26'
    $lnk.Save()
}

$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) "$name.lnk"
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) "$name.lnk"
New-Shortcut $startMenu
New-Shortcut $desktop

Write-Host ""
Write-Host "Installed!" -ForegroundColor Green
Write-Host "  Press the Windows key and type 'F1 26' to open it, or use the desktop shortcut."
Write-Host "  Tip: right-click it in the Start menu -> Pin to Start / Pin to taskbar."
Write-Host ""

Start-Process $exe
