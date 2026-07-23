# Provisionne les canaux Slack des acteurs du Sprint Launcher (SERZENIA-146).
# Lu par l'agent (pas Hajar) : lit SLACK_BOT_TOKEN depuis .env, cree un canal public par
# acteur (ou rejoint l'existant), ecrit l'id dans .env (SLACK_CHANNEL_<ACTEUR>) et poste un
# message de test. Le token ne transite jamais par la ligne de commande ni les logs.
# Scopes requis sur le token : chat:write, channels:manage, channels:read, channels:join.
# Script ASCII (PS 5.1 lit les .ps1 en Windows-1252).
param(
    [string[]]$Actors = @('ccode','ag','codex','sl'),
    [switch]$NoTestMessage
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$envPath = Join-Path $repoRoot '.env'
if (-not (Test-Path $envPath)) { Write-Host "ECHEC: .env introuvable ($envPath)"; exit 1 }

function Get-EnvValue([string]$key) {
    foreach ($line in Get-Content $envPath) {
        $t = $line.Trim()
        if ($t.StartsWith('#') -or -not $t.Contains('=')) { continue }
        $i = $t.IndexOf('=')
        if ($t.Substring(0,$i).Trim() -eq $key) { return $t.Substring($i+1).Trim() }
    }
    return $null
}

function Set-EnvValue([string]$key, [string]$value) {
    $lines = Get-Content $envPath
    $found = $false
    $out = foreach ($line in $lines) {
        $t = $line.Trim()
        if (-not $t.StartsWith('#') -and $t.Contains('=') -and $t.Substring(0,$t.IndexOf('=')).Trim() -eq $key) {
            $found = $true
            "$key=$value"
        } else { $line }
    }
    if (-not $found) { $out = $out + "$key=$value" }
    Set-Content -Path $envPath -Value $out -Encoding UTF8
}

$token = Get-EnvValue 'SLACK_BOT_TOKEN'
if ([string]::IsNullOrWhiteSpace($token)) { Write-Host "ECHEC: SLACK_BOT_TOKEN vide dans .env"; exit 1 }

$headers = @{ Authorization = "Bearer $token" }
function Invoke-Slack([string]$method, [hashtable]$body) {
    $json = ($body | ConvertTo-Json -Compress)
    return Invoke-RestMethod -Uri "https://slack.com/api/$method" -Method Post -Headers $headers `
        -Body $json -ContentType 'application/json; charset=utf-8'
}

# Cache de la liste des canaux (pour retrouver un canal deja existant).
$existing = @{}
$cursor = $null
do {
    $b = @{ types = 'public_channel'; limit = 200; exclude_archived = $true }
    if ($cursor) { $b.cursor = $cursor }
    $resp = Invoke-Slack 'conversations.list' $b
    if (-not $resp.ok) { Write-Host "ECHEC conversations.list: $($resp.error)"; exit 1 }
    foreach ($c in $resp.channels) { $existing[$c.name] = $c.id }
    $cursor = $resp.response_metadata.next_cursor
} while ($cursor)

$anyFail = $false
foreach ($actor in $Actors) {
    $name = $actor.ToLowerInvariant()
    $id = $null

    if ($existing.ContainsKey($name)) {
        $id = $existing[$name]
        Write-Host "= #$name existe deja ($id) -> join"
        $j = Invoke-Slack 'conversations.join' @{ channel = $id }
        if (-not $j.ok -and $j.error -ne 'method_not_supported_for_channel_type') {
            Write-Host "  ! join: $($j.error)"
        }
    } else {
        $c = Invoke-Slack 'conversations.create' @{ name = $name; is_private = $false }
        if ($c.ok) {
            $id = $c.channel.id
            Write-Host "+ #$name cree ($id)"
        } elseif ($c.error -eq 'name_taken') {
            # Course : cree entre la liste et maintenant, ou hors de la page listee.
            $lookup = Invoke-Slack 'conversations.list' @{ types = 'public_channel'; limit = 1000 }
            $id = ($lookup.channels | Where-Object { $_.name -eq $name } | Select-Object -First 1).id
            Write-Host "= #$name name_taken -> id $id"
        } else {
            Write-Host "ECHEC create #$name : $($c.error)"
            $anyFail = $true
            continue
        }
    }

    if ([string]::IsNullOrWhiteSpace($id)) { Write-Host "ECHEC: pas d'id pour #$name"; $anyFail = $true; continue }

    Set-EnvValue ("SLACK_CHANNEL_" + $actor.ToUpperInvariant()) $id

    if (-not $NoTestMessage) {
        $m = Invoke-Slack 'chat.postMessage' @{ channel = $id; text = "[$($actor.ToUpperInvariant())] info - canal branche au Sprint Launcher. Test OK." }
        if ($m.ok) { Write-Host "  -> message test poste dans #$name" }
        else { Write-Host "  ! chat.postMessage #$name : $($m.error)"; $anyFail = $true }
    }
}

Write-Host ""
if ($anyFail) { Write-Host "TERMINE AVEC ERREURS"; exit 1 } else { Write-Host "OK: canaux provisionnes + ids ecrits dans .env"; exit 0 }
