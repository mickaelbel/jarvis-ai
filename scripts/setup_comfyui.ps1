# =====================================================================
#  setup_comfyui.ps1 — Installe ComfyUI + modèle SDXL pour Jarvis AI.
#
#  Idempotent : ne réinstalle pas ce qui existe déjà.
#  Emplacement cible : %LOCALAPPDATA%\JarvisAI\ComfyUI
#
#  Usage :
#    powershell -ExecutionPolicy Bypass -File scripts\setup_comfyui.ps1
#    powershell -ExecutionPolicy Bypass -File scripts\setup_comfyui.ps1 -Force
# =====================================================================
param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# --- Emplacement cible ------------------------------------------------
$localAppData = $env:LOCALAPPDATA
$comfyDir     = Join-Path $localAppData "JarvisAI\ComfyUI"
$modelDir     = Join-Path $comfyDir "ComfyUI\models\checkpoints"
$portable7z   = Join-Path $env:TEMP "ComfyUI_windows_portable_nvidia.7z"
$logFile      = Join-Path $localAppData "JarvisAI\comfyui-setup.log"

function Log($msg) {
    $ts = Get-Date -Format "HH:mm:ss"
    $line = "[$ts] $msg"
    Write-Host $line -ForegroundColor Cyan
    Add-Content -Path $logFile -Value $line -ErrorAction SilentlyContinue
}

# --- Vérifier si déjà installé ----------------------------------------
function Test-ComfyUIReady {
    $exe = Join-Path $comfyDir "run_nvidia_gpu.bat"
    $ckpt = Get-ChildItem $modelDir -Filter "*.safetensors" -ErrorAction SilentlyContinue | Select-Object -First 1
    return (Test-Path $exe) -and ($null -ne $ckpt)
}

if ((Test-ComfyUIReady) -and -not $Force) {
    Log "ComfyUI déjà installé et modèle présent → rien à faire."
    Write-Host "ComfyUI est déjà prêt." -ForegroundColor Green
    exit 0
}

# --- Étape 1 : Télécharger ComfyUI portable (Nvidia) -------------------
if (-not (Test-Path $portable7z) -or $Force) {
    Log "Téléchargement de ComfyUI portable Nvidia (~2.5 Go)..."
    $url = "https://github.com/Comfy-Org/ComfyUI/releases/download/v0.34.0/ComfyUI_windows_portable_nvidia.7z"
    try {
        Invoke-WebRequest -Uri $url -OutFile $portable7z -UseBasicParsing
        Log "Téléchargé : $([math]::Round((Get-Item $portable7z).Length/1MB,0)) Mo"
    } catch {
        Log "ERREUR téléchargement ComfyUI : $_"
        exit 1
    }
} else {
    Log "7z déjà présent : $portable7z"
}

# --- Étape 2 : Extraire ComfyUI ----------------------------------------
if (-not (Test-Path (Join-Path $comfyDir "ComfyUI"))) {
    Log "Extraction de ComfyUI dans $comfyDir..."
    New-Item -ItemType Directory -Force -Path $comfyDir | Out-Null
    $7z = "C:\Program Files\7-Zip\7z.exe"
    if (-not (Test-Path $7z)) {
        Log "7-Zip introuvable. Installation via winget..."
        winget install --id 7zip.7zip --silent --accept-package-agreements --accept-source-agreements
        $7z = "C:\Program Files\7-Zip\7z.exe"
    }
    & $7z x "$portable7z" -o"$comfyDir" -y
    if ($LASTEXITCODE -ne 0) { Log "ERREUR extraction 7z"; exit 1 }
    Log "Extraction terminée."
} else {
    Log "ComfyUI déjà extrait."
}

# --- Étape 3 : Télécharger le modèle SDXL réaliste ---------------------
New-Item -ItemType Directory -Force -Path $modelDir | Out-Null
$existingModel = Get-ChildItem $modelDir -Filter "*.safetensors" -ErrorAction SilentlyContinue | Select-Object -First 1

if ($null -eq $existingModel -or $Force) {
    # RealVisXL V4.0 — modèle photo réaliste haute qualité, ~6.6 Go
    # Alternative : SDXL 1.0 base (~6.5 Go) si RealVisXL indisponible
    $modelUrl = "https://huggingface.co/digiplay/RealVisXL_V4.0/resolve/main/RealVisXL_V4.0.safetensors"
    $modelFile = Join-Path $modelDir "RealVisXL_V4.0.safetensors"

    if (-not (Test-Path $modelFile)) {
        Log "Téléchargement du modèle RealVisXL V4.0 (~6.6 Go)..."
        Log "Cela peut prendre plusieurs minutes selon votre connexion."
        try {
            # HuggingFace peut nécessiter un user-agent
            $headers = @{ "User-Agent" = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) JarvisAI/1.0" }
            Invoke-WebRequest -Uri $modelUrl -OutFile $modelFile -UseBasicParsing -Headers $headers
            $sizeGB = [math]::Round((Get-Item $modelFile).Length/1GB, 2)
            Log "Modèle téléchargé : $sizeGB Go"
        } catch {
            Log "ERREUR téléchargement modèle : $_"
            Log "Tentative avec SDXL 1.0 base..."
            $fallbackUrl = "https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0/resolve/main/sd_xl_base_1.0.safetensors"
            $fallbackFile = Join-Path $modelDir "sd_xl_base_1.0.safetensors"
            try {
                Invoke-WebRequest -Uri $fallbackUrl -OutFile $fallbackFile -UseBasicParsing -Headers $headers
                Log "Modèle SDXL base téléchargé."
            } catch {
                Log "ERREUR téléchargement modèle fallback : $_"
                exit 1
            }
        }
    } else {
        Log "Modèle déjà présent : $modelFile"
    }
} else {
    Log "Modèle déjà présent : $($existingModel.Name)"
}

# --- Étape 4 : Vérification finale -------------------------------------
if (Test-ComfyUIReady) {
    Log "=== ComfyUI prêt ! ==="
    Log "Emplacement : $comfyDir"
    Log "Modèle : RealVisXL V4.0 (SDXL réaliste)"
    Log "Pour démarrer manuellement : $comfyDir\run_nvidia_gpu.bat"
    Write-Host ""
    Write-Host "ComfyUI installé avec succès !" -ForegroundColor Green
    Write-Host "  Emplacement : $comfyDir" -ForegroundColor Gray
    Write-Host "  Modèle : RealVisXL V4.0 (photo réaliste)" -ForegroundColor Gray
    Write-Host "  Jarvis utilisera le moteur local automatiquement." -ForegroundColor Gray
} else {
    Log "ERREUR : installation incomplète."
    Write-Host "L'installation a échoué. Voir $logFile" -ForegroundColor Red
    exit 1
}

# Nettoyage du 7z temporaire
if (Test-Path $portable7z) {
    Remove-Item $portable7z -Force -ErrorAction SilentlyContinue
    Log "7z temporaire nettoyé."
}
