# Auto-reprise SL + Claude Code apres coupure quota (tache planifiee Windows).
# Demande de Hajar (2026-07-18) : version durable du cron de session.
# Lance un controle headless claude -p en sonnet-5 (controle = execution, pas
# cadrage — regle de Hajar : fable-5 reserve au cadrage/architecture/complexe).
$ErrorActionPreference = "Continue"
$log = Join-Path $env:TEMP "sl-autoreprise.log"
"[$(Get-Date -Format 'dd/MM HH:mm:ss')] --- reveil ---" | Add-Content $log

# 1. Si le CLI sprint-launcher tourne, ne rien faire (jamais deranger un run actif).
if (Get-Process sprint-launcher -ErrorAction SilentlyContinue) {
    "[$(Get-Date -Format 'HH:mm:ss')] run SL actif - rien a faire" | Add-Content $log
    exit 0
}

# 2. Resoudre claude.exe (chemin versionne -> toujours prendre la plus recente).
$claude = Get-ChildItem "$env:LOCALAPPDATA\Packages" -Directory -Filter "Claude_*" -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ChildItem (Join-Path $_.FullName "LocalCache\Roaming\Claude\claude-code") -Directory -ErrorAction SilentlyContinue } |
    Sort-Object Name -Descending | Select-Object -First 1 |
    ForEach-Object { Join-Path $_.FullName "claude.exe" }
if (-not $claude -or -not (Test-Path $claude)) {
    "[$(Get-Date -Format 'HH:mm:ss')] claude.exe introuvable" | Add-Content $log
    exit 1
}

# 3. Controle headless : etat du SL + reprise si un moteur etait au quota.
$prompt = @"
Controle automatique (tache Windows SL-AutoReprise, sans intervention de Hajar).
Contexte : dossier de run C:\Temp\test-v1.1.6 (binaire SL + artifacts\sprint6\...\state.json).
1. Si l'UI sprint-launcher-ui tourne SANS processus sprint-launcher et que state.json
   montre du travail restant (PendingReviews, ou dernier verdict QA avec ecarts et
   plafond de remediation NON atteint), tu peux relancer un run --resume --write via
   l'UI (UIAutomation : TxtSprint=6, ChkWrite+ChkResume coches, BtnRun).
2. Si le run s'est termine au plafond de remediation (RemediationCycles >= 2), NE
   RELANCE PAS : consigne juste l'etat en une phrase.
3. Si un fichier pending-directive.txt traine sans run actif, laisse-le (il sera
   ramasse au prochain lancement).
Reste bref et n'engage aucune action destructive. Termine par une ligne de resume.
"@
"[$(Get-Date -Format 'HH:mm:ss')] lancement controle claude ($claude)" | Add-Content $log
Set-Location "C:\Users\najwa\OneDrive\Desktop\SERZENIA"
& $claude -p --model claude-sonnet-5 --permission-mode bypassPermissions `
    --add-dir "C:\Temp" --add-dir "C:\Users\najwa\OneDrive\Desktop\SprintLauncher" `
    $prompt 2>&1 | Select-Object -Last 5 | Add-Content $log
"[$(Get-Date -Format 'HH:mm:ss')] --- fin ---" | Add-Content $log
