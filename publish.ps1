# Publie Jarvis AI en application Windows et crée le raccourci du menu Démarrer.
# Usage : powershell -ExecutionPolicy Bypass -File publish.ps1
#         powershell -ExecutionPolicy Bypass -File publish.ps1 -SkipDependencies
param(
    [switch]$SkipVoiceCopy,
    [switch]$SkipDependencies
)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root "dist\JarvisAI"
$desktop = Join-Path $root "src\Desktop\JarvisAI.Desktop"
$webVoice = Join-Path $root "src\Web\JarvisAI.Web\voice"
$plugin = Join-Path $root "Plugins\SystemControl\JarvisAI.Plugins.SystemControl.csproj"

if (-not $SkipDependencies) {
    # Dépendances optionnelles (venvs gestes/musique, yt-dlp, ffmpeg) —
    # idempotent : saute ce qui est déjà installé. Échoue jamais le build.
    Write-Host "==> Vérification des dépendances optionnelles..." -ForegroundColor Cyan
    $py = Get-Command py -ErrorAction SilentlyContinue
    if ($py) { & py scripts/setup_all.py }
    else { & python scripts/setup_all.py }
    Write-Host ""
}

Write-Host "==> Publication (dotnet publish)..." -ForegroundColor Cyan
dotnet publish $desktop -c Release -o $out -p:DebugType=none -p:DebugSymbols=false -p:SatelliteResourceLanguages=fr
if ($LASTEXITCODE -ne 0) { throw "dotnet publish a échoué" }

# Playwright : plateforme Windows uniquement (~-460 Mo en local aussi)
$pwNode = Join-Path $out ".playwright\node"
if (Test-Path $pwNode) {
    Get-ChildItem $pwNode -Directory | Where-Object { $_.Name -ne "win32_x64" } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "==> Copie des plugins..." -ForegroundColor Cyan
dotnet build $plugin -c Release -v q
if ($LASTEXITCODE -ne 0) { throw "La compilation du plugin a échoué" }
$pluginSrcRoot = Join-Path $root "Plugins\SystemControl\bin\Release"
$pluginSrc = (Get-ChildItem $pluginSrcRoot -Directory -Filter "net8.0*" | Select-Object -First 1).FullName
$pluginDest = Join-Path $out "Plugins\SystemControl"
if (Test-Path $pluginDest) { Remove-Item -Recurse -Force $pluginDest }
New-Item -ItemType Directory -Force -Path $pluginDest | Out-Null
Copy-Item -Force -Path (Join-Path $pluginSrc "*") -Destination $pluginDest -Recurse
Write-Host "    Plugins\SystemControl -> $pluginDest" -ForegroundColor DarkGray

if (-not $SkipVoiceCopy) {
    Write-Host "==> Copie des ressources vocales (STT, Piper)..." -ForegroundColor Cyan
    $destVoice = Join-Path $out "voice"
    if (Test-Path $destVoice) { Remove-Item -Recurse -Force $destVoice }
    Copy-Item -Recurse -Force -Path $webVoice -Destination $destVoice
}

Write-Host "==> Création du raccourci dans le dossier projet..." -ForegroundColor Cyan
$lnk = Join-Path $root "Jarvis AI.lnk"
$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut($lnk)
$sc.TargetPath = Join-Path $out "JarvisAI.Desktop.exe"
$sc.WorkingDirectory = $out
$sc.Description = "Jarvis AI - assistant vocal local"
$sc.IconLocation = (Join-Path $out "JarvisAI.Desktop.exe") + ",0"
$sc.Save()

Write-Host ""
Write-Host "Terminé." -ForegroundColor Green
Write-Host "  Application  : $out\JarvisAI.Desktop.exe"
Write-Host "  Raccourci    : $lnk"
Write-Host ""
Write-Host "Lance Jarvis depuis le raccourci '$lnk'."
