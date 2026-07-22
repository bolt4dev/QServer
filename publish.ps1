<#
.SYNOPSIS
    Builds a ready-to-run QServer release into .\dist.

.EXAMPLE
    .\publish.ps1                 # framework-dependent (needs the .NET 8 runtime on the target machine)
    .\publish.ps1 -SelfContained  # bundles the runtime (bigger, no prerequisites)
#>
param(
    [string]$Configuration = "Release",
    [string]$Rid = "win-x64",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$rid  = $Rid
$out  = Join-Path $root "dist"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

# DebugType=none / DebugSymbols=false: no .pdb is produced and, crucially, the DLLs carry no embedded
# absolute path to one (which would otherwise leak the build machine's source directory into the release).
$common = @("-c", $Configuration, "-r", $rid, "-o", $out, "--self-contained", $($SelfContained.IsPresent).ToString().ToLower(),
            "-p:DebugType=none", "-p:DebugSymbols=false")

Write-Host "Publishing QServer.Scraper..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\QServer.Scraper\QServer.Scraper.csproj") @common

Write-Host "Publishing QServer..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\QServer\QServer.csproj") @common

# Ship a default config + docs + launcher.
Copy-Item (Join-Path $root "qserver.sample.json") (Join-Path $out "qserver.json") -Force
Copy-Item (Join-Path $root "README.md") $out -Force
Copy-Item (Join-Path $root "LICENSE")   $out -Force

@'
@echo off
title QServer
cd /d "%~dp0"
QServer.exe
pause
'@ | Out-File (Join-Path $out "start.bat") -Encoding ascii

Write-Host ""
Write-Host "Done. Release is in: $out" -ForegroundColor Green
Write-Host "Edit qserver.json, then run start.bat (in a real terminal)." -ForegroundColor Green
