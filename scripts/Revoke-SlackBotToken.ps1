# Revoque le SLACK_BOT_TOKEN du .env, ou verifie simplement s'il est encore valide.
#
# Le token est lu DANS CE PROCESSUS depuis le .env et envoye a Slack. Il n'est jamais
# affiche, ni ecrit dans un log, ni passe en argument de ligne de commande (une ligne de
# commande est visible de toute la machine). Voir la section secrets de CLAUDE.md.
#
# Usage :
#   .\scripts\Revoke-SlackBotToken.ps1            verifie seulement (aucune revocation)
#   .\scripts\Revoke-SlackBotToken.ps1 -Revoke    revoque pour de bon
#
# NB : script tenu strictement ASCII (PS 5.1 lit les .ps1 en Windows-1252).
param(
    [string]$EnvFile = "$PSScriptRoot\..\.env",
    [switch]$Revoke
)
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if (-not (Test-Path $EnvFile)) { Write-Output "ABSENT : $EnvFile"; exit 1 }

$token = $null
foreach ($line in (Get-Content $EnvFile -Encoding UTF8)) {
    if ($line -match '^\s*SLACK_BOT_TOKEN\s*=(.*)$') { $token = $Matches[1].Trim() }
}
if ([string]::IsNullOrWhiteSpace($token)) { Write-Output "SLACK_BOT_TOKEN absent ou vide."; exit 1 }

# auth.test : dit si le token est encore valide, et a quelle app / quel workspace il
# appartient. Aucune de ces informations n'est un secret.
$headers = @{ Authorization = "Bearer $token" }
try {
    $who = Invoke-RestMethod -Uri "https://slack.com/api/auth.test" -Method Post -Headers $headers
} catch {
    Write-Output "Appel auth.test impossible : $($_.Exception.Message)"; exit 1
}

if (-not $who.ok) {
    Write-Output "Token DEJA INVALIDE (erreur Slack : $($who.error))."
    Write-Output "Rien a revoquer. Colle le nouveau token dans le .env s'il n'y est pas encore."
    exit 0
}

Write-Output "Token ENCORE VALIDE :"
Write-Output "  workspace : $($who.team)"
Write-Output "  bot       : $($who.user)"
Write-Output ""

if (-not $Revoke) {
    Write-Output "Verification seule. Relance avec -Revoke pour l'invalider definitivement."
    exit 0
}

$res = Invoke-RestMethod -Uri "https://slack.com/api/auth.revoke" -Method Post -Headers $headers
if ($res.ok -and $res.revoked) {
    Write-Output "REVOQUE. Ce token ne fonctionne plus nulle part."
    Write-Output "Etape suivante : reinstaller l'app Slack pour en obtenir un neuf, puis le coller dans le .env."
} else {
    Write-Output "Echec de la revocation (erreur Slack : $($res.error))."
    Write-Output "Passe par api.slack.com/apps -> ton app -> OAuth & Permissions -> Reinstall to Workspace."
}
