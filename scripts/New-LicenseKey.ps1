<#
.SYNOPSIS
    Génère une clé de licence Jarvis AI (Ed25519, offline).
.DESCRIPTION
    La clé privée est lue depuis la variable d'env JARVIS_LICENSE_PRIVATE_KEY
    ou depuis le fichier .env.local (ENCODAGE HEX, 64 caractères hex).
    La clé privée est utilisée ici UNIQUEMENT : elle ne doit JAMAIS être
    embarquée dans l'application.
.PARAMETER Owner
    Nom du client (optionnel).
.PARAMETER Tier
    « standard » (défaut) ou « pro ».
.PARAMETER Days
    Durée en jours (défaut : 365).
.PARAMETER Out
    Fichier de sortie pour la clé (défaut : stdout).
#>
param(
    [string]$Owner = "",
    [string]$Tier = "standard",
    [int]$Days = 365,
    [string]$Out = ""
)

$ErrorActionPreference = "Stop"

# Chargement de la clé privée
$envFile = Join-Path $PSScriptRoot "..\.env.local"
if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        if ($_ -match '^\s*JARVIS_LICENSE_PRIVATE_KEY\s*=\s*([0-9a-fA-F]+)') {
            $env:JARVIS_LICENSE_PRIVATE_KEY = $Matches[1]
        }
    }
}

$privateKeyHex = $env:JARVIS_LICENSE_PRIVATE_KEY
if (-not $privateKeyHex -or $privateKeyHex.Length -lt 64) {
    Write-Error "Clé privée introuvable. Définis JARVIS_LICENSE_PRIVATE_KEY (hex 64 chars) en variable d'env ou dans .env.local"
    exit 1
}

Add-Type -AssemblyName System.Security.Cryptography

$privateKeyBytes = [System.Convert]::FromHexString($privateKeyHex)
$expiry = [DateTime]::UtcNow.AddDays($Days).ToString("yyyy-MM-ddTHH:mm:ssZ")
$payload = @{
    id      = [guid]::NewGuid().ToString("N")[..8]
    owner   = $Owner
    expiry  = $expiry
    tier    = $Tier
} | ConvertTo-Json -Compress

$payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($payload)

# Génération de la signature Ed25519
$algo = [System.Security.Cryptography.Ed25519]::Create()
if ($algo) {
    # Import PKCS#8 de la clé privée
    $privKeyPkcs8 = [byte[]]@(
        0x30, 0x2E, 0x02, 0x01, 0x00, 0x30, 0x05, 0x06, 0x03, 0x2B, 0x65, 0x70,
        0x04, 0x22, 0x04, 0x20
    ) + $privateKeyBytes

    $pKey = [System.Security.Cryptography.Ed25519Ed25519PrivateKey]::ImportPkcs8PrivateKey($privKeyPkcs8, $null)
    $signature = $algo.SignData($payloadBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA512)
    $pKey.Dispose()
} else {
    Write-Error "Ed25519 non supporté sur ce système (.NET 8 requis)"
    exit 1
}

$payloadB64 = [System.Convert]::ToBase64String($payloadBytes)
$sigHex = [System.Convert]::ToHexString($signature)
$licenseKey = "$payloadB64.$sigHex"

if ($Out) {
    $licenseKey | Out-File -FilePath $Out -Encoding ascii -NoNewline
    Write-Host "Licence sauvegardée : $Out" -ForegroundColor Green
    Write-Host "  Owner : $Owner"
    Write-Host "  Tier  : $Tier"
    Write-Host "  Expiry: $expiry"
} else {
    Write-Output $licenseKey
}
