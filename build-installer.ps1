# =====================================================================
#  build-installer.ps1 — Étape 26 : crée le MSI d'installation de Jarvis.
#
#  Ce script :
#    1. publie l'application (dist\JarvisAI)
#    2. copie les plugins et les ressources vocales
#    3. génère automatiquement un fichier WiX (.wxs) à partir du dossier publié
#    4. compile le MSI avec `wix build`
#
#  Le MSI installe Jarvis dans "Program Files", crée le raccourci du menu
#  Démarrer avec l'icône, enregistre la désinstallation dans "Applications
#  et fonctionnalités" et ne contient aucun code source (binaires seulement).
#
#  Usage : powershell -ExecutionPolicy Bypass -File build-installer.ps1
#          powershell -ExecutionPolicy Bypass -File build-installer.ps1 -SkipPublish
#          powershell -ExecutionPolicy Bypass -File build-installer.ps1 -IncludeVoiceVenv
# =====================================================================
param(
    [switch]$SkipPublish,
    [switch]$IncludeVoiceVenv,
    [switch]$SkipDependencies
)
$ErrorActionPreference = "Stop"

$root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist   = Join-Path $root "dist\JarvisAI"
$installerDir = Join-Path $root "installer"
$wxsFile = Join-Path $installerDir "JarvisAI.wxs"
$msiOut  = Join-Path $root "dist\JarvisAI-Setup.msi"

if (-not $SkipPublish) {
    Write-Host "==> Publication de l'application..." -ForegroundColor Cyan
    $publishArgs = @()
    if ($SkipDependencies) { $publishArgs += "-SkipDependencies" }
    & (Join-Path $root "publish.ps1") @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "publish.ps1 a échoué" }
}

if (-not (Test-Path "$dist\JarvisAI.Desktop.exe")) {
    throw "Publication introuvable : $dist\JarvisAI.Desktop.exe. Lance d'abord publish.ps1."
}

# --- Version de l'application (depuis l'exe publié) --------------------
$fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo("$dist\JarvisAI.Desktop.exe").FileVersion
if ([string]::IsNullOrWhiteSpace($fileVersion)) { $fileVersion = "1.0.0" }
$version = ($fileVersion -split "\.", 4)[0..2] -join "."
Write-Host "==> Version détectée : $version" -ForegroundColor Cyan

# --- Collecte des fichiers à empaqueter ---------------------------------
$excluded = @(
    '(^|\\)\.venv\\',                     # environnement Python local (chemins absolus, 2,4 Go)
    '(^|\\)\.playwright\\',               # navigateurs Playwright (téléchargés au 1er run)
    '(^|\\)JarvisAI\.Desktop\.exe\.WebView2\\',  # cache WebView2 (recréé au démarrage)
    '\.pdb$',                             # symboles de debug (pas du code source, mais inutiles)
    '\.wixpdb$'
)
$allFiles = Get-ChildItem $dist -Recurse -File | Where-Object {
    $rel = $_.FullName.Substring($dist.Length).TrimStart('\')
    -not ($excluded | Where-Object { $rel -match $_ })
}

if ($IncludeVoiceVenv) {
    # Rien de plus : le switch est documenté pour le mode exhaustif mais le
    # .venv (2,4 Go) n'est jamais embarqué car il contient des chemins absolus.
}

Write-Host ("==> {0} fichiers empaquetés ({1:N1} Mo)" -f $allFiles.Count, (($allFiles | Measure-Object Length -Sum).Sum / 1MB)) -ForegroundColor Cyan

# --- GUID déterministe par chemin relatif --------------------------------
function New-DeterministicGuid([string]$key) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $hash = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($key.ToLowerInvariant()))
    $b = [byte[]]::new(16)
    [Array]::Copy($hash, $b, 16)
    $b[6] = (($b[6] -band 0x0f) -bor 0x50)
    $b[8] = (($b[8] -band 0x3f) -bor 0x80)
    [System.Guid]::new($b)
}
function Get-Id([string]$rel) {
    $clean = $rel -replace '[^A-Za-z0-9]', '_'
    if ($clean.Length -gt 40) { $clean = $clean.Substring(0, 40) }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($rel))
        $hash = ([BitConverter]::ToString($hashBytes, 0, 8) -replace '-', '').ToLowerInvariant()
    } finally { $sha.Dispose() }
    "id_" + $clean + "_" + $hash
}

# --- Construction de l'arbre de répertoires --------------------------------
# Chaque entrée : relDir -> @{ children = <sorted hashtable dirName->entry>, files = @(absPath) }
$rootNode = @{ rel = ""; children = @{}; files = [System.Collections.Generic.List[string]]::new() }
$nodeMap = @{ "" = $rootNode }

function Ensure-Node([string]$relDir) {
    if ($relDir -eq ".") { $relDir = "" }
    if (-not $nodeMap.ContainsKey($relDir)) {
        $nodeMap[$relDir] = @{ rel = $relDir; children = @{}; files = [System.Collections.Generic.List[string]]::new() }
    }
    return $nodeMap[$relDir]
}

foreach ($f in $allFiles) {
    $rel = $f.FullName.Substring($dist.Length).TrimStart('\')
    $relDir = Split-Path $rel -Parent
    if ($relDir -eq "") { $relDir = "." }
    Ensure-Node $relDir | Out-Null

    # rattache le répertoire à son parent
    $parts = if ($relDir -eq ".") { @() } else { $relDir -split '\\' }
    $acc = ""
    $parent = $rootNode
    foreach ($p in $parts) {
        if ($acc -eq "") { $acc = $p } else { $acc = "$acc\$p" }
        $parentNode = Ensure-Node $acc
        if (-not $parent.children.ContainsKey($p)) { $parent.children[$p] = $parentNode }
        $parent = $parentNode
    }
    $node = Ensure-Node $relDir
    $node.files.Add($f.FullName)
}

# --- Génération du .wxs ------------------------------------------------------
$sb = [System.Text.StringBuilder]::new()
$componentRefs = [System.Collections.Generic.List[string]]::new()
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <Package Name="Jarvis AI" Manufacturer="JarvisAI" Version="' + $version + '"')
[void]$sb.AppendLine('           UpgradeCode="9E3C6A2D-8B4F-4C21-A8D5-1F2A6B7C8D9E" Language="1036" Scope="perMachine">')
[void]$sb.AppendLine('    <MajorUpgrade DowngradeErrorMessage="Une version plus récente de Jarvis AI est déjà installée." />')
[void]$sb.AppendLine('    <MediaTemplate EmbedCab="yes" CompressionLevel="high" />')
[void]$sb.AppendLine('    <StandardDirectory Id="ProgramFiles64Folder">')
[void]$sb.AppendLine('      <Directory Id="INSTALLFOLDER" Name="JarvisAI">')

function Emit-Directory($node, $indent) {
    foreach ($name in ($node.children.Keys | Sort-Object)) {
        $child = $node.children[$name]
        $childId = Get-Id $child.rel
        [void]$sb.AppendLine("$indent<Directory Id='$childId' Name='$name'>")
        Emit-Directory $child ($indent + "  ")
        foreach ($filePath in $child.files) {
            $rel = $filePath.Substring($dist.Length).TrimStart('\')
            $fileId = Get-Id ("f_" + $rel)
            $guid = New-DeterministicGuid $rel
            $componentRefs.Add($fileId)
            [void]$sb.AppendLine("$indent  <Component Id='$fileId' Guid='$guid'>")
            [void]$sb.AppendLine("$indent    <File Id='${fileId}_file' Source='$filePath' />")
            [void]$sb.AppendLine("$indent  </Component>")
        }
        [void]$sb.AppendLine("$indent</Directory>")
    }
}

# fichiers à la racine
foreach ($filePath in $rootNode.files) {
    $rel = $filePath.Substring($dist.Length).TrimStart('\')
    $fileId = Get-Id ("f_" + $rel)
    $guid = New-DeterministicGuid $rel
    $componentRefs.Add($fileId)
    [void]$sb.AppendLine("        <Component Id='$fileId' Guid='$guid'>")
    [void]$sb.AppendLine("          <File Id='${fileId}_file' Source='$filePath' />")
    [void]$sb.AppendLine("        </Component>")
}
Emit-Directory $rootNode "        "

[void]$sb.AppendLine('      </Directory>')
[void]$sb.AppendLine('    </StandardDirectory>')

# Raccourci menu Démarrer
[void]$sb.AppendLine('    <StandardDirectory Id="ProgramMenuFolder">')
[void]$sb.AppendLine('      <Directory Id="StartMenuDir" Name="Jarvis AI">')
[void]$sb.AppendLine('        <Component Id="StartMenuShortcut" Guid="B2E1D4F0-6C8A-4E3B-9F2D-8A1C5B7E3D9F">')
[void]$sb.AppendLine('          <Shortcut Id="JarvisStartMenu" Name="Jarvis AI" Description="Jarvis AI - assistant vocal local"')
[void]$sb.AppendLine('                     Target="[INSTALLFOLDER]JarvisAI.Desktop.exe" WorkingDirectory="INSTALLFOLDER" Icon="JarvisAppIcon" />')
[void]$sb.AppendLine('          <RemoveFolder Id="RemoveStartMenuDir" Directory="StartMenuDir" On="uninstall" />')
[void]$sb.AppendLine('          <RegistryValue Root="HKCU" Key="Software\JarvisAI" Name="installed" Type="integer" Value="1" KeyPath="yes" />')
[void]$sb.AppendLine('        </Component>')
[void]$sb.AppendLine('      </Directory>')
[void]$sb.AppendLine('    </StandardDirectory>')

# Icones (raccourci + désinstallation)
[void]$sb.AppendLine('    <Icon Id="JarvisAppIcon" SourceFile="' + $dist + '\jarvis.ico" />')
[void]$sb.AppendLine('    <Property Id="ARPPRODUCTICON" Value="JarvisAppIcon" />')

# Feature + composants
[void]$sb.AppendLine('    <Feature Id="MainFeature" Title="Jarvis AI" Level="1">')
[void]$sb.AppendLine('      <ComponentRef Id="StartMenuShortcut" />')
foreach ($id in $componentRefs) {
    [void]$sb.AppendLine("      <ComponentRef Id='$id' />")
}
[void]$sb.AppendLine('    </Feature>')
[void]$sb.AppendLine('  </Package>')
[void]$sb.AppendLine('</Wix>')

if (-not (Test-Path $installerDir)) { New-Item -ItemType Directory -Force -Path $installerDir | Out-Null }
[System.IO.File]::WriteAllText($wxsFile, $sb.ToString())

# --- Compilation MSI ---------------------------------------------------------
Write-Host "==> Compilation du MSI (wix build)..." -ForegroundColor Cyan
& wix build -arch x64 -o $msiOut $wxsFile
if ($LASTEXITCODE -ne 0) { throw "wix build a échoué" }

# --- Bootstrapper Exe (Bundle Burn qui embarque le MSI) ----------------------
$bundleExe = Join-Path $root "dist\JarvisAI-Setup.exe"
$bundleWxs = Join-Path $installerDir "JarvisAI.Bundle.wxs"
$balExt = "WixToolset.Bal.wixext"
$balDll = Join-Path $env:USERPROFILE ".wix\extensions\$balExt\5.0.2\wixext5\WixToolset.BootstrapperApplications.wixext.dll"

if (Test-Path $balDll) {
    # WiX cherche le DLL sous le nom de l'extension : on s'assure qu'il existe.
    $expectedBalDll = Join-Path $env:USERPROFILE ".wix\extensions\$balExt\5.0.2\wixext5\WixToolset.Bal.wixext.dll"
    if (-not (Test-Path $expectedBalDll)) {
        New-Item -ItemType Directory -Force -Path (Split-Path $expectedBalDll) | Out-Null
        Copy-Item $balDll $expectedBalDll -Force
    }
    if (-not (Test-Path $balDll)) { Copy-Item $expectedBalDll $balDll -Force }

    $bundleSb = [System.Text.StringBuilder]::new()
    [void]$bundleSb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"')
    [void]$bundleSb.AppendLine('     xmlns:bal="http://wixtoolset.org/schemas/v4/wxs/bal">')
    [void]$bundleSb.AppendLine('  <Bundle Name="Jarvis AI Setup" Version="' + $version + '" Manufacturer="JarvisAI"')
    [void]$bundleSb.AppendLine('           UpgradeCode="9E3C6A2D-8B4F-4C21-A8D5-1F2A6B7C8D9E"')
    [void]$bundleSb.AppendLine('           IconSourceFile="' + $dist + '\jarvis.ico">')
    [void]$bundleSb.AppendLine('    <BootstrapperApplication>')
    [void]$bundleSb.AppendLine('      <bal:WixStandardBootstrapperApplication Theme="rtfLicense" LicenseUrl="" />')
    [void]$bundleSb.AppendLine('    </BootstrapperApplication>')
    [void]$bundleSb.AppendLine('    <Chain>')
    [void]$bundleSb.AppendLine('      <MsiPackage SourceFile="' + $msiOut + '" Vital="yes" />')
    [void]$bundleSb.AppendLine('    </Chain>')
    [void]$bundleSb.AppendLine('  </Bundle>')
    [void]$bundleSb.AppendLine('</Wix>')
    [System.IO.File]::WriteAllText($bundleWxs, $bundleSb.ToString())

    Write-Host "==> Compilation du bootstrapper Exe (wix build -ext $balExt)..." -ForegroundColor Cyan
    & wix build -ext $balExt -o $bundleExe $bundleWxs
    if ($LASTEXITCODE -ne 0) { throw "wix build (bundle) a échoué" }
} else {
    Write-Host "==> Extension BAL introuvable ; l'Exe d'installation ne sera pas généré (le MSI reste disponible)." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Installeurs générés :" -ForegroundColor Green
Write-Host "  $msiOut   (MSI - désinstallation dans Applications et fonctionnalités)"
if (Test-Path $bundleExe) {
    Write-Host "  $bundleExe   (Exe - assistant d'installation avec icône et élévation)"
}
Write-Host ""
Write-Host "Pour installer Jarvis sur un autre PC : double-clic sur l'Exe ou le MSI."
Write-Host "Les installeurs n'embarquent que les binaires (aucun code source). Les données"
Write-Host "(mémoire, settings, outils auto-générés) restent dans %LOCALAPPDATA%\JarvisAI."
Write-Host ""
Write-Host "NOTES :"
Write-Host "  - publish.ps1 installe automatiquement les dépendances optionnelles"
Write-Host "    (venvs gestes/musique, ffmpeg, yt-dlp) via scripts\setup_all.py —"
Write-Host "    idempotent, sautable avec -SkipDependencies."
Write-Host "  - Le .venv Python (2,4 Go) n'est pas embarqué : la reconnaissance vocale STT"
Write-Host "    et le wake-word sont désactivés tant que voice\.venv n'existe pas dans le"
Write-Host "    dossier installé. Tout le reste (chat, outils, TTS Piper, plugins) fonctionne."
Write-Host "  - Les navigateurs Playwright sont téléchargés au premier usage automatisé."
