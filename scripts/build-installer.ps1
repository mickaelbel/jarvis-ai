# ============================================================================
#  Jarvis AI — Pipeline de release commerciale
#  Crée un installateur EXE prêt à distribuer à partir du code source ACTUEL :
#    1. Audit anti-fuite : aucun log/capture/clé/donnée personnelle ne part
#    2. Publication .NET self-contained (aucun prérequis chez l'acheteur)
#    3. Assets vocaux (Piper + wake words) nettoyés des environnements dev
#    4. Obfuscation optionnelle si ConfuserEx présent dans tools\obfuscate
#    5. Installateur Inno Setup (installé silencieusement en espace utilisateur)
#
#  Usage :  powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
#           (ou double-clic sur Creer-Installateur.cmd à la racine)
#  Sortie : dist\installer\JarvisAI-Setup-<version>.exe (+ SHA256)
# ============================================================================
param(
    [switch]$SkipObfuscation,
    [switch]$KeepStage
)
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$stage = Join-Path $root "dist\installer-stage"
$appDir = Join-Path $stage "JarvisAI"
$outDir = Join-Path $root "dist\installer"

Write-Host ""
Write-Host "== Jarvis AI — Build installateur ==" -ForegroundColor Cyan

# ── Version : tag git sinon horodatage ──────────────────────────────────────
$version = "0.0.0"
try {
    $tag = git -C $root describe --tags --abbrev=0 2>$null
    if ($LASTEXITCODE -eq 0 -and $tag) { $version = $tag.TrimStart("v") }
} catch { }
if ($version -eq "0.0.0") { $version = Get-Date -Format "yyyy.MM.dd-HHmm" }
Write-Host "   Version : $version" -ForegroundColor DarkGray

# ── 1. Nettoyage du staging (jamais de résidus d'une session précédente) ────
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $appDir, $outDir | Out-Null

# ── 2. Publication self-contained win-x64 ───────────────────────────────────
Write-Host "==> Publication .NET self-contained (win-x64)..." -ForegroundColor Cyan
$desktop = Join-Path $root "src\Desktop\JarvisAI.Desktop"
dotnet publish $desktop -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -o $appDir --nologo -v q `
    -p:DebugType=none -p:DebugSymbols=false -p:SatelliteResourceLanguages=fr
if ($LASTEXITCODE -ne 0) { throw "dotnet publish a échoué" }

# Supprime les PDB (symboles = aide à la rétro-ingénierie, inutiles au client)
Get-ChildItem $appDir -Recurse -Filter "*.pdb" | Remove-Item -Force

# Playwright : ne garder que la plateforme Windows du poste cible (~-460 Mo)
$pwNode = Join-Path $appDir ".playwright\node"
if (Test-Path $pwNode) {
    Get-ChildItem $pwNode -Directory | Where-Object { $_.Name -ne "win32_x64" } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

# ── 3. Plugins ───────────────────────────────────────────────────────────────
Write-Host "==> Compilation et copie des plugins..." -ForegroundColor Cyan
$pluginCsproj = Join-Path $root "Plugins\SystemControl\JarvisAI.Plugins.SystemControl.csproj"
dotnet build $pluginCsproj -c Release -v q
if ($LASTEXITCODE -ne 0) { throw "Compilation plugin échouée" }
$pluginDest = Join-Path $appDir "Plugins\SystemControl"
New-Item -ItemType Directory -Force -Path $pluginDest | Out-Null
$pluginBinRoot = Join-Path $root "Plugins\SystemControl\bin\Release"
$pluginBin = Get-ChildItem $pluginBinRoot -Directory -Filter "net8.0*" | Select-Object -First 1
Copy-Item -Force -Path (Join-Path $pluginBin.FullName "*") -Destination $pluginDest -Recurse
Get-ChildItem $pluginDest -Filter "*.pdb" | Remove-Item -Force -ErrorAction SilentlyContinue

# ── 4. Assets vocaux (Piper + wake words), SANS environnement dev ───────────
Write-Host "==> Copie des assets vocaux..." -ForegroundColor Cyan
$voiceSrc = Join-Path $root "src\Web\JarvisAI.Web\voice"
$voiceDest = Join-Path $appDir "voice"
New-Item -ItemType Directory -Force -Path $voiceDest | Out-Null
# piper.exe + modèles de voix (pas le zip d'origine ni espeak-data superflu)
Copy-Item -Recurse -Force (Join-Path $voiceSrc "piper\piper") (Join-Path $voiceDest "piper")
Copy-Item -Force (Join-Path $voiceSrc "piper\*.onnx*") (Join-Path $voiceDest "piper") -ErrorAction SilentlyContinue
# Modèles wake word ouverts (~2 Mo)
Copy-Item -Recurse -Force (Join-Path $voiceSrc "wakeword_models") (Join-Path $voiceDest "wakeword_models")
# Serveurs Python STT/wakeword (code source Python nécessaire au runtime)
Copy-Item -Force (Join-Path $voiceSrc "*.py") $voiceDest
Copy-Item -Force (Join-Path $voiceSrc "requirements.txt") $voiceDest

# ── 5. Obfuscation (optionnelle, hook ConfuserEx) ───────────────────────────
if (-not $SkipObfuscation) {
    $confuser = Get-ChildItem (Join-Path $root "tools\obfuscate") -Recurse -Filter "Confuser.CLI.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($confuser) {
        Write-Host "==> Obfuscation ConfuserEx..." -ForegroundColor Cyan
        # Règle minimale : renommage + contrôle de flux sur les DLL Jarvis uniquement
        $crProj = @"
<project outputDir='$appDir' baseDir='$appDir' xmlns='http://confuser.codeplex.com'>
  <rule pattern='true' preset='aggressive' />
</project>
"@
        $crPath = Join-Path $stage "confuser.crproj"
        [IO.File]::WriteAllText($crPath, $crProj)
        & $confuser.FullName $crPath
        if ($LASTEXITCODE -ne 0) { throw "Obfuscation échouée" }
    } else {
        Write-Warning "ConfuserEx absent (tools\obfuscate) — assembly non obfusqué. L'IL reste lisible via ILSpy ; pour une protection forte, ajoutez ConfuserEx ou .NET Reactor."
    }
}

# ── 6. AUDIT ANTI-FUITE (bloquant) ──────────────────────────────────────────
Write-Host "==> Audit anti-fuite..." -ForegroundColor Cyan
$forbiddenDirs = @('\.git', '\.venv', '__pycache__', '\captures', '\coffre', '\hub', '\Tests', '\src', '\dist\JarvisAI')
$forbiddenFiles = @("*.log", "*.err.log", "*.user", "*.pdb", "*.lnk")
# Les .md sont interdits SAUF ceux embarqués par les libs tierces (Playwright)
$violations = @()
foreach ($pat in $forbiddenFiles) {
    $hits = Get-ChildItem $appDir -Recurse -Filter $pat -ErrorAction SilentlyContinue
    foreach ($h in $hits) { $violations += $h.FullName.Replace($root, "...") }
}
foreach ($h in (Get-ChildItem $appDir -Recurse -Filter "*.md" -ErrorAction SilentlyContinue)) {
    if ($h.FullName -notlike "*\.playwright\*") { $violations += $h.FullName.Replace($root, "...") }
}
foreach ($d in $forbiddenDirs) {
    $name = $d.TrimStart('\')
    $hits = Get-ChildItem $appDir -Recurse -Directory -Filter $name -ErrorAction SilentlyContinue
    foreach ($h in $hits) { $violations += "[dir] " + $h.FullName.Replace($root, "...") }
}
# Scan de contenu : clés API dans les fichiers texte embarqués
$keyPatterns = 'sk-[A-Za-z0-9]{20,}', 'AIza[0-9A-Za-z\-_]{35}', 'xox[baprs]-[0-9A-Za-z\-]{10,}', 'BEGIN (RSA )?PRIVATE KEY'
$textExt = @(".json", ".txt", ".py", ".ps1", ".cmd", ".cfg", ".yaml", ".yml", ".iss")
foreach ($f in (Get-ChildItem $appDir -Recurse -File | Where-Object {
        $textExt -contains $_.Extension.ToLower() -and $_.Length -lt 2MB })) {
    $content = Get-Content $f.FullName -Raw -ErrorAction SilentlyContinue
    if ($content) {
        foreach ($kp in $keyPatterns) {
            if ($content -match $kp) { $violations += "[CLE API ?] " + $f.FullName.Replace($root, "...") }
        }
    }
}
if ($violations.Count -gt 0) {
    Write-Host "FUITE DÉTECTÉE — build annulé :" -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "   ✗ $_" -ForegroundColor Red }
    throw "Audit anti-fuite en échec ($($violations.Count) élément(s))"
}
Write-Host "   Aucune fuite détectée ✓" -ForegroundColor Green

# ── 7. Inno Setup (détection ou installation silencieuse par utilisateur) ───
$isccCandidates = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    (Join-Path $root "tools\InnoSetup6\ISCC.exe")
) | Where-Object { $_ -and (Test-Path $_) }
$iscc = $isccCandidates | Select-Object -First 1

if (-not $iscc) {
    Write-Host "==> Installation silencieuse d'Inno Setup (espace utilisateur)..." -ForegroundColor Cyan
    $innoDir = Join-Path $root "tools\InnoSetup6"
    New-Item -ItemType Directory -Force -Path $innoDir | Out-Null
    $innoUrls = @(
        "https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe",
        "https://github.com/jrsoftware/issrc/releases/download/is-6_7_2/innosetup-6.7.2.exe"
    )
    $innoTmp = Join-Path $env:TEMP "innosetup.jarvis.exe"
    $downloaded = $false
    foreach ($u in $innoUrls) {
        try {
            Invoke-WebRequest -Uri $u -OutFile $innoTmp -UseBasicParsing -TimeoutSec 300
            if ((Get-Item $innoTmp).Length -gt 1MB) { $downloaded = $true; break }
        } catch { Write-Warning "Téléchargement échoué ($u)" }
    }
    if (-not $downloaded) { throw "Impossible de télécharger Inno Setup — installe-le manuellement puis relance." }
    Start-Process -FilePath $innoTmp -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "`"/DIR=$innoDir`"", "/CURRENTUSER" -Wait
    Remove-Item $innoTmp -Force -ErrorAction SilentlyContinue
    $iscc = Join-Path $innoDir "ISCC.exe"
    if (-not (Test-Path $iscc)) { throw "Installation d'Inno Setup impossible — installe-le manuellement puis relance." }
}

# ── 8. Génération du script Inno (.iss) ─────────────────────────────────────
$appId = "{{7E4A9C52-8B31-4F6D-A0E8-5A2C41B9D301}"  # stable entre versions ({{ échappe l'accolade Inno)
$iss = @"
[Setup]
AppId=$appId
AppName=Jarvis AI
AppVersion=$version
AppPublisher=Jarvis AI
DefaultDirName={localappdata}\Programs\JarvisAI
PrivilegesRequired=lowest
OutputDir=$outDir
OutputBaseFilename=JarvisAI-Setup-$version
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\JarvisAI.Desktop.exe
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le bureau"; GroupDescription: "Raccourcis :"
Name: "startup"; Description: "Lancer Jarvis AI au démarrage de Windows"; Flags: unchecked

[Files]
Source: "$appDir\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Jarvis AI"; Filename: "{app}\JarvisAI.Desktop.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Jarvis AI"; Filename: "{app}\JarvisAI.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{userstartup}\Jarvis AI"; Filename: "{app}\JarvisAI.Desktop.exe"; Tasks: startup

[Run]
Filename: "{app}\JarvisAI.Desktop.exe"; WorkingDir: "{app}"; Description: "Lancer Jarvis AI maintenant"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Les données personnelles restent dans %LOCALAPPDATA%\JarvisAI (choix volontaire)
Type: filesandordirs; Name: "{app}\logs"
"@
$issPath = Join-Path $stage "jarvis.iss"
# UTF-8 avec BOM : Inno Setup interprète sinon le fichier comme ANSI (accents cassés)
[IO.File]::WriteAllText($issPath, $iss, (New-Object System.Text.UTF8Encoding($true)))

# ── 9. Compilation de l'installateur ────────────────────────────────────────
Write-Host "==> Compilation installateur (Inno Setup)..." -ForegroundColor Cyan
Remove-Item (Join-Path $outDir "JarvisAI-Setup-$version.exe") -Force -ErrorAction SilentlyContinue
& $iscc /Q $issPath
if ($LASTEXITCODE -ne 0) { throw "Compilation Inno Setup échouée" }

$setupExe = Join-Path $outDir "JarvisAI-Setup-$version.exe"
if (-not (Test-Path $setupExe)) { throw "Installateur introuvable après compilation" }

$sha = (Get-FileHash $setupExe -Algorithm SHA256).Hash
$sizeMb = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
Set-Content (Join-Path $outDir "SHA256.txt") "$sha  $(Split-Path -Leaf $setupExe)"

if (-not $KeepStage) { Remove-Item -Recurse -Force $stage }

Write-Host ""
Write-Host "TERMINÉ ✓" -ForegroundColor Green
Write-Host "   Installateur : $setupExe ($sizeMb Mo)"
Write-Host "   SHA256       : $sha"
Write-Host "   Données perso : hors package (elles vivent dans %LOCALAPPDATA%\JarvisAI côté client)."
