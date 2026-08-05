# Verifie l'etat des secrets du .env SANS jamais afficher leur valeur.
#
# Raison d'etre (incident du 2026-08-05) : pour ouvrir le .env a Hajar, l'agent l'a lu
# en entier. Tout le fichier s'est retrouve dans le transcript de la conversation :
# l'ancien token Jira, mais aussi SLACK_BOT_TOKEN et SLACK_APP_TOKEN, qui n'avaient
# rien a y faire. Les trois ont du etre regeneres.
#
# Regle : on ne lit JAMAIS un fichier de secrets pour verifier un secret. On compare des
# empreintes. Ce script n'affiche que les 8 premiers caracteres d'un SHA-256 : assez pour
# constater qu'une valeur a change, inexploitable pour la reconstituer.
#
# Usage :
#   .\scripts\Test-EnvSecrets.ps1                  etat de tous les secrets
#   .\scripts\Test-EnvSecrets.ps1 -Key JIRA_API_TOKEN
#
# NB : script tenu strictement ASCII (PS 5.1 lit les .ps1 en Windows-1252).
param(
    [string]$EnvFile = "$PSScriptRoot\..\.env",
    [string]$Key
)
$ErrorActionPreference = "Stop"

if (-not (Test-Path $EnvFile)) { Write-Output "ABSENT : $EnvFile"; exit 1 }

# Tout ce qui ressemble a un secret. Les autres clefs (URL, email, nom de modele) sont
# affichees en clair, elles n'ont rien de sensible.
$secretPattern = 'TOKEN|SECRET|PASSWORD|KEY|CREDENTIAL'

function Get-Fingerprint([string]$value) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($value))
    $sha.Dispose()
    return (($bytes | ForEach-Object { $_.ToString("x2") }) -join '').Substring(0, 8)
}

$rows = @()
foreach ($line in (Get-Content $EnvFile -Encoding UTF8)) {
    if ($line -notmatch '^\s*([A-Za-z0-9_]+)\s*=(.*)$') { continue }
    $name = $Matches[1]
    $value = $Matches[2].Trim()
    if ($Key -and $name -ne $Key) { continue }

    if ($name -match $secretPattern) {
        $rows += [pscustomobject]@{
            Clef      = $name
            Etat      = if ([string]::IsNullOrWhiteSpace($value)) { "VIDE" } else { "renseigne" }
            Longueur  = $value.Length
            Empreinte = if ([string]::IsNullOrWhiteSpace($value)) { "-" } else { Get-Fingerprint $value }
        }
    } else {
        $rows += [pscustomobject]@{
            Clef = $name; Etat = "non sensible"; Longueur = $value.Length; Empreinte = $value
        }
    }
}

if (-not $rows) { Write-Output "Aucune clef trouvee$(if ($Key) { " pour '$Key'" })."; exit 1 }
$rows | Format-Table -AutoSize
Write-Output ""
Write-Output "Empreinte = 8 premiers caracteres du SHA-256. Elle change des que la valeur change."
Write-Output "Aucune valeur secrete n'est affichee par ce script, et il ne doit jamais en afficher."
