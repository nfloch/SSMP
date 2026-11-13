# Télécharge le dernier build SSMP (branche world-sync) et l'installe dans le profil Cogfly.
#
# Usage : double-cliquer sur installer-ssmp.cmd, ou
#   powershell -ExecutionPolicy Bypass -File installer-ssmp.ps1 [-Profil <nom du profil Cogfly>]
#
# Sans -Profil, le script cherche les profils Cogfly qui contiennent SSMP et demande lequel utiliser s'il y en a
# plusieurs. L'ancienne version est gardée à côté, en SSMP.dll.bak.

param(
    [string] $Profil = "",
    [string] $Branche = "world-sync"
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$ZipUrl = "https://github.com/nfloch/SSMP/releases/download/latest-$Branche/SSMP-$Branche.zip"
$Fichiers = @("SSMP.dll", "SSMP.pdb", "BouncyCastle.Cryptography.dll")

function Find-SsmpPluginFolder([string] $nomProfil) {
    $racine = Join-Path $env:APPDATA "Cogfly\profiles"
    if (-not (Test-Path $racine)) {
        throw "Dossier des profils Cogfly introuvable : $racine"
    }

    if ($nomProfil) {
        $dossier = Join-Path $racine "$nomProfil\BepInEx\plugins\SSMP-SSMP"
        if (-not (Test-Path $dossier)) {
            throw "SSMP n'est pas installé dans le profil '$nomProfil' ($dossier)"
        }
        return $dossier
    }

    $trouves = @(Get-ChildItem $racine -Directory | Where-Object {
        Test-Path (Join-Path $_.FullName "BepInEx\plugins\SSMP-SSMP")
    })
    if ($trouves.Count -eq 0) {
        throw "Aucun profil Cogfly avec SSMP trouvé dans $racine. Installe SSMP une première fois avec Cogfly."
    }
    if ($trouves.Count -eq 1) {
        $choix = $trouves[0]
    } else {
        Write-Host "Plusieurs profils Cogfly contiennent SSMP :"
        for ($i = 0; $i -lt $trouves.Count; $i++) {
            Write-Host "  [$($i + 1)] $($trouves[$i].Name)"
        }
        $n = [int](Read-Host "Numéro du profil à mettre à jour")
        if ($n -lt 1 -or $n -gt $trouves.Count) { throw "Choix invalide" }
        $choix = $trouves[$n - 1]
    }
    Write-Host "Profil Cogfly : $($choix.Name)"
    return Join-Path $choix.FullName "BepInEx\plugins\SSMP-SSMP"
}

if (Get-Process -Name "Hollow Knight Silksong" -ErrorAction SilentlyContinue) {
    throw "Silksong est lancé : ferme le jeu puis relance ce script."
}

$plugin = Find-SsmpPluginFolder $Profil

$temp = Join-Path $env:TEMP "ssmp-maj-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $zip = Join-Path $temp "ssmp.zip"
    Write-Host "Téléchargement de $ZipUrl"
    Invoke-WebRequest -Uri $ZipUrl -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath (Join-Path $temp "x")
    $source = Join-Path $temp "x"

    foreach ($f in $Fichiers) {
        if (-not (Test-Path (Join-Path $source $f))) { throw "Le zip ne contient pas $f" }
    }

    $actuel = Join-Path $plugin "SSMP.dll"
    if (Test-Path $actuel) {
        Copy-Item $actuel (Join-Path $plugin "SSMP.dll.bak") -Force
    }
    foreach ($f in $Fichiers) {
        Copy-Item (Join-Path $source $f) $plugin -Force
    }
    Copy-Item (Join-Path $source "COMMIT.txt") (Join-Path $plugin "SSMP-COMMIT.txt") -Force

    # Lien d'envoi automatique des logs en fin de session (lu par SSMP, à côté de SSMP.dll)
    $config = Join-Path $PSScriptRoot "ssmp-jojo.config"
    $lien = if (Test-Path $config) { Get-Content $config | Where-Object { $_ -match '^\s*UploadUrl\s*=' } | Select-Object -First 1 }
    if ($lien) {
        Set-Content -Path (Join-Path $plugin "ssmp-upload.cfg") -Value $lien.Trim() -Encoding ASCII
    } else {
        Write-Host "Pas de lien UploadUrl dans $config : les logs ne seront pas envoyés automatiquement." -ForegroundColor Yellow
    }

    $commit = (Get-Content (Join-Path $source "COMMIT.txt") -Raw).Trim()
    $hash = (Get-FileHash $actuel -Algorithm SHA256).Hash.Substring(0, 16).ToLower()
    Write-Host ""
    Write-Host "SSMP mis à jour dans $plugin"
    Write-Host "  commit : $($commit.Substring(0, 7))"
    Write-Host "  SHA-256 de SSMP.dll : $hash"
    Write-Host "Les deux joueurs doivent avoir le même commit."
} finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
