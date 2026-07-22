# 10 headless back-to-back runs: each must reach the ready sentinel and leave no survivors.
# Uses --run-sec with redirected output (panel auto-selects headless mode).
# Run from repo root:  powershell -File tests\rapid-cycle.ps1 -Panel .\src\QServer\bin\Debug\net8.0\win-x64\QServer.exe
param([Parameter(Mandatory = $true)][string]$Panel, [int]$Rounds = 10)
$ErrorActionPreference = "Stop"
for ($i = 1; $i -le $Rounds; $i++) {
    $out = & $Panel --run-sec 15 2>&1 | Out-String
    # "sentinel=none" must be checked FIRST: it has no digit after "=", so the -notmatch "sentinel=\d"
    # check below already fires on a not-ready run — testing none afterward would be a dead branch.
    if ($out -match "sentinel=none") { throw "round ${i}: server never became ready" }
    if ($out -notmatch "sentinel=\d") { throw "round ${i}: no sentinel summary in output:`n$out" }
    $left = @(Get-Process -Name "FakeServer", "QServer.Scraper" -ErrorAction SilentlyContinue)
    if ($left.Count -gt 0) { $left | Stop-Process -Force; throw "round ${i}: survivors left behind" }
    Write-Host "round ${i}: OK"
}
Write-Host "rapid-cycle: $Rounds/$Rounds clean starts"
