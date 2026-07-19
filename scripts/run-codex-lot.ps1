# Lance UN acteur codex (exec) sur un lot, dans son worktree, en mode detache.
# Reutilise le mecanisme du SL (BinaryLocator/ActorRunner) : codex.exe exec, prompt sur
# stdin en UTF-8 sans BOM, clefs API retirees (mode abonnement), sandbox bypass pour dev.
# Demande de Hajar (2026-07-18) : deleguer les lots a codex, hors quota Claude, survivant.
# NB : script strictement ASCII (PS 5.1 lit les .ps1 en Windows-1252).
param(
    [Parameter(Mandatory=$true)][string]$Worktree,
    [Parameter(Mandatory=$true)][string]$BriefFile
)
$ErrorActionPreference = "Continue"
$log = Join-Path $Worktree "codex-run.log"
# TOUT le log en UTF-8 (une seule encodage) : evite le melange ANSI/UTF-16 de `*>>`.
function Log($m) { Add-Content -Path $log -Value ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) -Encoding UTF8 }

if (-not (Test-Path $Worktree)) { "worktree absent: $Worktree" | Add-Content $log; exit 1 }
if (-not (Test-Path $BriefFile)) { Log "brief absent: $BriefFile"; exit 1 }

# Resoudre codex.exe (extension VS Code la plus recente), comme BinaryLocator.FindCodex.
$ext = Get-ChildItem "$env:USERPROFILE\.vscode\extensions" -Directory -Filter "openai.chatgpt-*" -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1
$codex = if ($ext) { Join-Path $ext.FullName "bin\windows-x86_64\codex.exe" } else { $null }
if (-not $codex -or -not (Test-Path $codex)) { Log "codex.exe introuvable"; exit 1 }

# Mode abonnement : pas de clef API dans l'env du process.
$env:OPENAI_API_KEY = $null
$env:ANTHROPIC_API_KEY = $null
# stdin vers un exe natif : forcer UTF-8 SANS BOM (le BOM casse le parseur stream-json).
$OutputEncoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

$last = Join-Path $Worktree "codex-last.txt"
Log "demarrage codex ($codex) dans $Worktree"
Set-Location $Worktree
$brief = Get-Content $BriefFile -Raw -Encoding UTF8
# codex exec : tache lue sur stdin ; --json = evenements au fil de l'eau ; bypass sandbox
# pour permettre dev + build + git commit (l'acteur committe lui-meme sur sa branche).
$brief | & $codex exec --skip-git-repo-check --json --output-last-message $last `
    --dangerously-bypass-approvals-and-sandbox 2>&1 |
    ForEach-Object { Add-Content -Path $log -Value ([string]$_) -Encoding UTF8 }
Log "codex termine (exit $LASTEXITCODE). Dernier message: $last"
