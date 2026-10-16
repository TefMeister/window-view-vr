# install.ps1 -- copies Window View from dist\ to %LOCALAPPDATA%\Programs\WindowView and puts its icon on the desktop.
# Nothing is switched on: the app's VR LAYER button does that. Run again after a rebuild (close the app first).
#   powershell -ExecutionPolicy Bypass -File install.ps1 [-From <folder with the built files>]
param([string]$From = "")
# from a source folder the built files are in dist\; from the download zip they sit next to this script
if ($From -eq "") { $From = if (Test-Path (Join-Path $PSScriptRoot "dist\WindowView.exe")) { Join-Path $PSScriptRoot "dist" } else { $PSScriptRoot } }
$ErrorActionPreference = "Stop"
$to = Join-Path $env:LOCALAPPDATA "Programs\WindowView"
New-Item -ItemType Directory -Force $to | Out-Null
foreach ($f in "WindowView.exe", "window_view_layer.dll", "window_view_layer32.dll", "window_view.ico") {
    if (Test-Path (Join-Path $From $f)) { Copy-Item (Join-Path $From $f) (Join-Path $to $f) -Force }
}
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath("Desktop")) "Window View.lnk"))
$lnk.TargetPath = Join-Path $to "WindowView.exe"
$lnk.WorkingDirectory = $to
$lnk.IconLocation = (Join-Path $to "window_view.ico") + ",0"
$lnk.Description = "See desktop windows in any OpenXR game, and use them with the mouse"
$lnk.Save()
Write-Output "Installed to $to, icon on the desktop."
