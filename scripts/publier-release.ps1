# ============================================================================
#  Jarvis AI - Publication GitHub Releases (upload du dernier installateur)
#
#  Cherche le setup le plus récent dans dist\installer, crée (si besoin) un tag
#  git et une Release GitHub associée, puis uploade l'EXE + SHA256 en assets.
#
#  Pré-requis : gh CLI installé ET authentifié, OU un PAT (GITHUB_TOKEN / GH_PAT).
#  Usage :  powershell -ExecutionPolicy Bypass -File scripts\publier-release.ps1
#           (ou double-clic sur Publier-Release.cmd)
# ============================================================================
param(
    [string]$SetupPath,
    [switch]$NoGitTag
)
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$owner = "mickaelbel"
$repoName = "jarvis-ai"

$dirInstaller = Join-Path $root "dist\installer"
if ([string]::IsNullOrWhiteSpace($SetupPath)) {
    $setup = Get-ChildItem $dirInstaller -Filter "JarvisAI-Setup-*.exe" |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
} else {
    $setup = Get-Item $SetupPath
}
if (-not $setup) { throw "Aucun installateur trouvé dans $dirInstaller" }

if ($setup.Name -notmatch '^JarvisAI-Setup-(.+)\.exe$') {
    throw "Nom d'installateur inattendu : $($setup.Name)"
}
$version = $Matches[1]
$tag = "v$version"

Write-Host ""
Write-Host "== Jarvis AI - Publication GitHub Releases ==" -ForegroundColor Cyan
Write-Host "   Installateur : $($setup.Name)" -ForegroundColor DarkGray
Write-Host "   Version       : $version   (tag $tag)" -ForegroundColor DarkGray

$sha256 = ""
$shaFile = Join-Path $dirInstaller "SHA256.txt"
if (Test-Path $shaFile) {
    $line = Get-Content $shaFile | Select-String "$([regex]::Escape($setup.Name))" | Select-Object -First 1
    if ($line) { $sha256 = ($line.Line -split "\s+")[0] }
}
if (-not $sha256) {
    $sha256 = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash
}

if (-not $NoGitTag) {
    $existTag = git -C $root tag -l $tag 2>$null
    if (-not $existTag) {
        Write-Host "==> Création du tag git $tag..." -ForegroundColor Cyan
        git -C $root tag $tag
        if ($LASTEXITCODE -ne 0) { throw "Création du tag git a échoué" }
        git -C $root push origin $tag
        if ($LASTEXITCODE -ne 0) {
            Write-Host "    (push du tag impossible)" -ForegroundColor DarkYellow
        }
    } else {
        Write-Host "   Le tag $tag existe déjà." -ForegroundColor DarkGray
    }
}

$gh = Get-Command gh -ErrorAction SilentlyContinue
$token = $env:GITHUB_TOKEN
if (-not $token) { $token = $env:GH_PAT }
if (-not $token) {
    $tokenFile = Join-Path $root "scripts\.github-token"
    if (Test-Path $tokenFile) { $token = (Get-Content $tokenFile).Trim() }
}

if (-not $gh -and -not $token) {
    throw "Aucun moyen d'authentification : installe GitHub CLI ou fournis un PAT."
}

$assetFiles = @($setup.FullName)
if (Test-Path $shaFile) { $assetFiles += $shaFile }

if ($gh) {
    Write-Host "==> Publication via GitHub CLI..." -ForegroundColor Cyan
    $existing = gh release view $tag --repo "$owner/$repoName" 2>$null
    if ($existing) {
        Write-Host "   Release $tag déjà existante — ajout des assets..." -ForegroundColor DarkGray
        foreach ($f in $assetFiles) {
            gh release upload $tag $f --repo "$owner/$repoName" --clobber
        }
    } else {
        gh release create $tag --repo "$owner/$repoName" --title "Jarvis AI $version" --notes "Version $version" --latest
        foreach ($f in $assetFiles) {
            gh release upload $tag $f --repo "$owner/$repoName"
        }
    }
    if ($LASTEXITCODE -ne 0) { throw "Publication GitHub CLI a échoué" }
} else {
    Write-Host "==> Publication via API REST (token PAT)..." -ForegroundColor Cyan
    $headers = @{ Authorization = "token $token"; Accept = "application/vnd.github+json" }
    $baseApi = "https://api.github.com/repos/$owner/$repoName"
    $body = @{ tag_name = $tag; name = "Jarvis AI $version"; body = "Version $version"; draft = $false; prerelease = $false } | ConvertTo-Json

    try {
        $rel = Invoke-RestMethod -Method Post -Uri "$baseApi/releases" -Headers $headers -ContentType "application/json" -Body $body
        Write-Host "   Release créée : $($rel.html_url)" -ForegroundColor DarkGray
    } catch {
        $status = 0
        try { $status = $_.Exception.Response.StatusCode.value__ } catch { }
        if ($status -eq 422 -or $status -eq 403) {
            $rels = Invoke-RestMethod -Method Get -Uri "$baseApi/releases/tags/$tag" -Headers $headers
            $rel = $rels
            Write-Host "   Release $tag déjà existante — ajout des assets..." -ForegroundColor DarkGray
        } else { throw }
    }

    foreach ($f in $assetFiles) {
        $fileName = [System.IO.Path]::GetFileName($f)
        $upUrl = "$($rel.upload_url -replace '\{[^}]*\}','')?name=$([uri]::EscapeDataString($fileName))"
        Invoke-RestMethod -Method Post -Uri $upUrl -Headers @{ Authorization = "token $token"; Accept = "application/vnd.github+json" } -InFile $f -ContentType "application/octet-stream" | Out-Null
        Write-Host "   Asset uploadé : $fileName" -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host "== Publication terminée ==" -ForegroundColor Green
Write-Host "   https://github.com/$owner/$repoName/releases/tag/$tag" -ForegroundColor Green
