# Verifies that NO FakeServer/scraper process survives any way of closing the panel.
# Prereq: publish (or build) the panel, and point a copy of qserver.json at FakeServer:
#   server.exePath = <repo>\tools\FakeServer\bin\Debug\net8.0\FakeServer.exe
#   server.args    = "--ready-after-ms 500"    server.workingDirectory = that bin folder
# Run from repo root:  powershell -File tests\kill-matrix.ps1 -Panel .\src\QServer\bin\Debug\net8.0\win-x64\QServer.exe
param([Parameter(Mandatory = $true)][string]$Panel)
$ErrorActionPreference = "Stop"

function Assert-NoSurvivor([string]$scenario) {
    Start-Sleep -Seconds 4
    $left = @(Get-Process -Name "FakeServer", "QServer.Scraper" -ErrorAction SilentlyContinue)
    if ($left.Count -gt 0) {
        $left | Stop-Process -Force
        throw "SURVIVOR(S) after '$scenario': $($left.Name -join ', ')"
    }
    Write-Host "OK: $scenario"
}

function Start-Panel {
    $p = Start-Process -FilePath $Panel -PassThru
    Start-Sleep -Seconds 8      # give it time to spawn scraper + FakeServer
    if (-not (Get-Process -Name "FakeServer" -ErrorAction SilentlyContinue)) { throw "panel did not start FakeServer" }
    return $p
}

# 1: hard kill of the panel (Task Manager style) -> job object must reap children
$p = Start-Panel; taskkill /F /PID $p.Id | Out-Null;        Assert-NoSurvivor "hard kill (taskkill /F)"
# 2: graceful close (WM_CLOSE to the console window ~= clicking X on classic conhost)
$p = Start-Panel; taskkill /PID $p.Id | Out-Null;           Assert-NoSurvivor "window close (taskkill soft)"
# 3: the scraper dies -> its own job + panel Dispose must reap the server
$p = Start-Panel
Get-Process -Name "QServer.Scraper" | Stop-Process -Force
Assert-NoSurvivor "scraper killed"
if (-not $p.HasExited) { taskkill /F /PID $p.Id | Out-Null }
Start-Sleep 2
Write-Host "kill-matrix: ALL SCENARIOS PASSED"
# NOTE: Windows Terminal hosts the window itself, so scenario 2 approximates the X button; do one
# manual X-click check on classic conhost (and on the VDS) as the final sign-off.
