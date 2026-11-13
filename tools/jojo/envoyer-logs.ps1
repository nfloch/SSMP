# Compresse le LogOutput.log de BepInEx et l'envoie sur le Gokapi de Papaours (demande d'upload publique).
#
# Usage : double-cliquer sur envoyer-logs.cmd, ou
#   powershell -ExecutionPolicy Bypass -File envoyer-logs.ps1 [-Profil <nom du profil Cogfly>]
#
# Le lien d'upload est lu dans ssmp-jojo.config, à côté de ce script (ligne UploadUrl=...). Ce fichier n'est pas
# dans le dépôt : Papaours l'envoie avec les scripts.

param(
    [string] $Profil = ""
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.Net.Http

$ChunkSize = 45MB

function Read-UploadUrl {
    $config = Join-Path $PSScriptRoot "ssmp-jojo.config"
    if (-not (Test-Path $config)) { throw "Fichier de config introuvable : $config" }
    foreach ($ligne in Get-Content $config) {
        if ($ligne -match '^\s*UploadUrl\s*=\s*(\S+)\s*$') { return $Matches[1] }
    }
    throw "Pas de ligne UploadUrl=... dans $config"
}

function Find-CogflyProfile([string] $nomProfil) {
    $racine = Join-Path $env:APPDATA "Cogfly\profiles"
    if ($nomProfil) {
        $dossier = Join-Path $racine $nomProfil
        if (-not (Test-Path $dossier)) { throw "Profil Cogfly introuvable : $dossier" }
        return Get-Item $dossier
    }
    $trouves = @(Get-ChildItem $racine -Directory | Where-Object {
        Test-Path (Join-Path $_.FullName "BepInEx\plugins\SSMP-SSMP")
    })
    if ($trouves.Count -eq 0) { throw "Aucun profil Cogfly avec SSMP trouvé dans $racine" }
    if ($trouves.Count -eq 1) { return $trouves[0] }
    Write-Host "Plusieurs profils Cogfly contiennent SSMP :"
    for ($i = 0; $i -lt $trouves.Count; $i++) { Write-Host "  [$($i + 1)] $($trouves[$i].Name)" }
    $n = [int](Read-Host "Numéro du profil dont envoyer les logs")
    if ($n -lt 1 -or $n -gt $trouves.Count) { throw "Choix invalide" }
    return $trouves[$n - 1]
}

function Send-Request([System.Net.Http.HttpClient] $client, [string] $url, [hashtable] $headers, $content) {
    $requete = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, $url)
    foreach ($k in $headers.Keys) { [void] $requete.Headers.TryAddWithoutValidation($k, [string] $headers[$k]) }
    if ($content) { $requete.Content = $content }
    $reponse = $client.SendAsync($requete).GetAwaiter().GetResult()
    $corps = $reponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $reponse.IsSuccessStatusCode) {
        throw "Erreur $([int] $reponse.StatusCode) sur $url : $corps"
    }
    return $corps
}

# Lien de la forme https://<serveur>/publicUpload?id=<id>&key=<clé>
$uploadUrl = Read-UploadUrl
if ($uploadUrl -notmatch '^(https?://[^/]+(?:/.*?)?)/publicUpload\?id=([^&]+)&key=([^&]+)$') {
    throw "Lien d'upload non reconnu : $uploadUrl"
}
$api = "$($Matches[1])/api/uploadrequest/chunk"
$requestId = $Matches[2]
$apiKey = $Matches[3]

$profilCogfly = Find-CogflyProfile $Profil
$log = Join-Path $profilCogfly.FullName "BepInEx\LogOutput.log"
if (-not (Test-Path $log)) { throw "Log introuvable : $log" }
if (Get-Process -Name "Hollow Knight Silksong" -ErrorAction SilentlyContinue) {
    Write-Host "Attention : Silksong est lancé, le log envoyé s'arrêtera à maintenant." -ForegroundColor Yellow
}

$temp = Join-Path $env:TEMP "ssmp-logs-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temp | Out-Null
$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromMinutes(10)
try {
    # Copie d'abord : le jeu peut garder le fichier ouvert
    $copie = Join-Path $temp "LogOutput.log"
    Copy-Item $log $copie
    $nom = "LogOutput-$($env:USERNAME)-$($profilCogfly.Name)-$(Get-Date -Format 'yyyyMMdd-HHmm').zip"
    $zip = Join-Path $temp $nom
    Compress-Archive -Path $copie -DestinationPath $zip
    $taille = (Get-Item $zip).Length
    Write-Host "Envoi de $nom ($([math]::Round($taille / 1MB, 1)) Mo)"

    $reserve = Send-Request $client "$api/reserve" @{ id = $requestId; apikey = $apiKey } $null
    $uuid = ($reserve | ConvertFrom-Json).Uuid
    if (-not $uuid) { throw "Réponse inattendue du serveur : $reserve" }

    $octets = [IO.File]::ReadAllBytes($zip)
    for ($offset = 0; $offset -lt $octets.Length; $offset += $ChunkSize) {
        $longueur = [math]::Min($ChunkSize, $octets.Length - $offset)
        $form = New-Object System.Net.Http.MultipartFormDataContent
        $morceau = [System.Net.Http.ByteArrayContent]::new($octets, [int] $offset, [int] $longueur)
        $form.Add($morceau, "file", "blob")
        $form.Add((New-Object System.Net.Http.StringContent($uuid)), "uuid")
        $form.Add((New-Object System.Net.Http.StringContent([string] $taille)), "filesize")
        $form.Add((New-Object System.Net.Http.StringContent([string] $offset)), "offset")
        [void] (Send-Request $client "$api/add" @{ apikey = $apiKey; fileRequestId = $requestId } $form)
    }

    $nomBase64 = "base64:" + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($nom))
    [void] (Send-Request $client "$api/complete" @{
        uuid = $uuid; fileRequestId = $requestId; filename = $nomBase64; filesize = $taille
        nonblocking = "true"; contenttype = "application/zip"; apikey = $apiKey
    } $null)

    Write-Host ""
    Write-Host "Logs envoyés : $nom" -ForegroundColor Green
} finally {
    $client.Dispose()
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
