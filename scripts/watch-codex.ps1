# Afficheur lisible du flux codex d'un lot (filtre le JSON verbeux de codex-run.log).
# Usage :
#   powershell -File scripts\watch-codex.ps1 -Lot 5            # instantane (tout le log)
#   powershell -File scripts\watch-codex.ps1 -Lot 5 -Follow    # live (Ctrl+C pour arreter)
# Les logs sont ecrits par `*>>` en UTF-16 avec des BOM repetes -> lecture partagee +
# retrait des BOM + decodage UTF-16LE. Reprend la logique de CodexJsonInterpreter.cs.
# Script strictement ASCII (PS 5.1 lit les .ps1 en Windows-1252).
param([string]$Lot = "5", [switch]$Follow)

$map = @{ "5"="sl-lot5-interpretation"; "6"="sl-lot6-gardien"; "2"="sl-lot2-pause-horodatage"; "1"="sl-lot1-modele-complexite" }
$wt  = "C:\Users\najwa\OneDrive\Desktop\SL-wt-lot$Lot"
$log = Join-Path $wt "codex-run.log"

function Read-LogLines($path) {
    if (-not (Test-Path $path)) { return @() }
    # Log ecrit en UTF-8 par run-codex-lot.ps1 ; lecture partagee (codex l'a ouvert).
    $fs = [System.IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
    try { $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8); $text = $sr.ReadToEnd(); $sr.Dispose() } finally { $fs.Dispose() }
    return ($text -split "`r?`n")
}

function Trunc($s) { if ($null -eq $s) { return $null }; $s = ($s -replace "`r"," ") -replace "`n"," "; $s = $s.Trim(); if ($s.Length -gt 160) { $s.Substring(0,160) + "..." } else { $s } }

function Render($line) {
    $s = $line.Trim()
    if (-not $s.StartsWith("{")) { return $null }
    try { $d = $s | ConvertFrom-Json } catch { return $null }
    switch ($d.type) {
        "thread.started" { return "--- session codex demarree ---" }
        "turn.completed" { return "--- tour termine ---" }
        "error"          { return "!! " + $d.message }
        { $_ -in "item.started","item.updated","item.completed" } {
            $it = $d.item; if ($null -eq $it) { return $null }
            switch -Regex ($it.type) {
                "^agent_message$" { if ($d.type -eq "item.completed" -and $it.text) { return "`n" + $it.text + "`n" } ; return $null }
                "^reasoning$"     { if ($d.type -eq "item.completed") { $r = $it.text; if (-not $r) { $r = $it.summary }; return "  (reflexion) " + (Trunc $r) } ; return $null }
                "command|shell|exec" { if ($d.type -eq "item.started") { $c = $it.command; if (-not $c) { $c = $it.cmd }; return "  $ " + (Trunc $c) } ; return $null }
                "file|patch|change|diff" { if ($d.type -eq "item.completed") { $f = $it.path; if (-not $f) { $f = $it.file }; return "  ~ " + (Trunc $f) } ; return $null }
                default { return $null }
            }
        }
        default { return $null }
    }
}

Write-Host "=== Lot $Lot ($($map[$Lot])) - $log ===" -ForegroundColor Cyan
if (-not (Test-Path $log)) { Write-Host "Pas encore de log (codex demarre ?)."; exit 0 }

if ($Follow) {
    $shown = 0
    while ($true) {
        $lines = Read-LogLines $log
        for ($k = $shown; $k -lt $lines.Count; $k++) { $r = Render $lines[$k]; if ($r) { Write-Host $r } }
        $shown = $lines.Count
        Start-Sleep -Seconds 2
    }
} else {
    foreach ($ln in (Read-LogLines $log)) { $r = Render $ln; if ($r) { Write-Host $r } }
}
