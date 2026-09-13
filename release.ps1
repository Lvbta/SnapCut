# release.ps1 - one-click publish (auto-publish rule)
# Prereq: `gh auth login` done once.
# Usage:
#   .\release.ps1            publish current built version (build\SnapCut-Setup-vX.Y.Z.exe must exist)
#   .\release.ps1 -Build     bump version + build, then publish
#   .\release.ps1 -Build -Message "notes"
param([string]$Message = "", [switch]$Build)

$ErrorActionPreference = "Stop"

$ghPath = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $ghPath) { $ghPath = "C:\Program Files\GitHub CLI\gh.exe" }
if (-not (Test-Path $ghPath)) { throw "gh not found. Install GitHub CLI and run gh auth login." }

$root  = $PSScriptRoot

if ($Build) {
    Write-Host "Building and bumping version..." -ForegroundColor Cyan
    & powershell -ExecutionPolicy Bypass -File (Join-Path $root "build.ps1") -Bump -Message $Message
}

# 必须在构建/升版本之后读取，否则拿到的是升版前的旧版本号
$ver   = (Get-Content (Join-Path $root "version.txt") -Raw).Trim()
$setup = Join-Path $root ("build\SnapCut-Setup-v" + $ver + ".exe")
$zip   = Join-Path $root ("build\SnapCut-v" + $ver + "-portable.zip")

if (-not (Test-Path $setup)) { throw "Setup not found: $setup. Build first (.\build.ps1 -Bump)." }
$size = (Get-Item $setup).Length

$assets = @("$setup")
if (Test-Path $zip) { $assets += "$zip" }

Write-Host "Publishing v$ver to GitHub Releases..." -ForegroundColor Cyan
$notes = if ($Message) { $Message } else { "Regular build." }
& $ghPath release create "v$ver" $assets `
    --repo Lvbta/SnapCut `
    --title "SnapCut v$ver" `
    --notes $notes
if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }

# Update the cloud manifest (release first so the download URL is live, then push manifest to avoid 404)
$mf = Join-Path $root "update\manifest.txt"
$content = Get-Content $mf -Raw -Encoding UTF8
$content = [regex]::Replace($content, '(?m)^version=.*$', "version=$ver")
$content = [regex]::Replace($content, '(?m)^url=.*$',
    ("url=https://github.com/Lvbta/SnapCut/releases/download/v$ver/SnapCut-Setup-v$ver.exe"))
$content = [regex]::Replace($content, '(?m)^size=.*$', "size=$size")
if ($Message) { $content = [regex]::Replace($content, '(?m)^notes=.*$', ("notes=" + $Message)) }
Set-Content $mf $content -Encoding UTF8

Write-Host "Committing and pushing all changes..." -ForegroundColor Cyan
git -C $root add -A
git -C $root commit -m "release: v$ver"
git -C $root push

Write-Host "Published: v$ver" -ForegroundColor Green
