# Orchestrateur : cree les worktrees manquants et lance UN codex detache par lot restant.
# Concu pour tourner en tache planifiee (schtasks /Run) -> independant de la session Claude,
# survit a une coupure de quota Claude. Chaque codex tourne sur l'abonnement OpenAI de Hajar.
# Demande de Hajar (2026-07-18). Script strictement ASCII (PS 5.1 = Windows-1252).
$ErrorActionPreference = "Continue"
$repo = "C:\Users\najwa\OneDrive\Desktop\SprintLauncher"
$scripts = Join-Path $repo "scripts"
$runner = Join-Path $scripts "run-codex-lot.ps1"
$olog = Join-Path $env:TEMP "codex-lots-orchestrator.log"
function Log($m) { "[$(Get-Date -Format 'dd/MM HH:mm:ss')] $m" | Add-Content $olog }
Log "=== orchestrateur demarre ==="

$lots = @(
    @{ N="5"; WT="C:\Users\najwa\OneDrive\Desktop\SL-wt-lot5"; Branch="sl-lot5-interpretation";     Brief=(Join-Path $scripts "briefs\brief-lot5.txt") },
    @{ N="6"; WT="C:\Users\najwa\OneDrive\Desktop\SL-wt-lot6"; Branch="sl-lot6-gardien";            Brief=(Join-Path $scripts "briefs\brief-lot6.txt") },
    @{ N="2"; WT="C:\Users\najwa\OneDrive\Desktop\SL-wt-lot2"; Branch="sl-lot2-pause-horodatage";   Brief=(Join-Path $scripts "briefs\brief-lot2.txt") },
    @{ N="1"; WT="C:\Users\najwa\OneDrive\Desktop\SL-wt-lot1"; Branch="sl-lot1-modele-complexite";  Brief=(Join-Path $scripts "briefs\brief-lot1.txt") }
)

foreach ($lot in $lots) {
    $wt = $lot.WT
    if (-not (Test-Path $wt)) {
        Log "creation worktree Lot $($lot.N) -> $wt (branche $($lot.Branch))"
        git -C $repo worktree add $wt -b $lot.Branch sl-fixes-2026-07-16 2>&1 | Add-Content $olog
    } else {
        Log "worktree Lot $($lot.N) deja present"
    }
    if (-not (Test-Path $wt)) { Log "ECHEC worktree Lot $($lot.N), on saute"; continue }

    Log "lancement codex detache Lot $($lot.N)"
    Start-Process -FilePath "powershell.exe" -WindowStyle Hidden -ArgumentList @(
        "-NoProfile","-ExecutionPolicy","Bypass","-File",$runner,
        "-Worktree",$wt,"-BriefFile",$lot.Brief
    )
    Start-Sleep -Seconds 45   # quinconce : eviter 4 builds .NET simultanes sur le disque
}
Log "=== orchestrateur : tous les lots lances ==="
