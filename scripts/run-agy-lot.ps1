# Lance UN acteur agy (Antigravity CLI) sur un lot, dans son worktree, en mode detache.
# Meme concept que run-codex-lot.ps1, pour delegation hors quota Claude, cette fois via AGY
# quand codex est lui-meme hors quota. Reutilise le contrat valide par
# ActorRunner.PrepareAgyInvocation / docs/141-ag-smoke.md : agy n'a pas de stdin (-p exige
# son argument), donc le prompt complet part dans un fichier du workspace et seule une
# consigne courte qui pointe dessus passe en argument.
# Demande de Hajar (2026-08-04) : meme concept de delegation de lot, avec AGY.
# NB : script strictement ASCII (PS 5.1 lit les .ps1 en Windows-1252).
param(
    [Parameter(Mandatory=$true)][string]$Worktree,
    [Parameter(Mandatory=$true)][string]$BriefFile,
    # 2026-08-05 : agy a change sa nomenclature de modeles. L'ancien defaut "gemini-3-pro"
    # est rejete ("not recognized as a known model"). Les noms valides sont ceux affiches
    # par agy lui-meme, avec espaces et parentheses, p.ex. "Gemini 3.6 Flash (High)",
    # "Gemini 3.1 Pro (High)", "Claude Sonnet 4.6 (Thinking)". En cas de nouveau rejet,
    # lancer `agy -p x --model bidon` : l'erreur liste les modeles reellement disponibles.
    # 2026-08-05 17h30 : le quota des modeles GEMINI d'agy est epuise (message
    # "Individual quota reached", reset annonce dans ~163 h, soit vers le 12/08). Six lots
    # lances a la suite sont tous tombes dessus. En revanche "Claude Sonnet 4.6 (Thinking)"
    # repond encore : le quota est par famille de modeles, pas global. C'est donc lui le
    # defaut tant que Gemini n'est pas revenu.
    # ATTENTION appelant : ce nom contient des espaces ET des parentheses. Passe par le
    # defaut plutot que par -Model dans un Start-Process : un -ArgumentList en tableau ne
    # requote pas les elements, la valeur est alors decoupee et le lot ne demarre pas
    # silencieusement (constate le 2026-08-05).
    [string]$Model = "Claude Sonnet 4.6 (Thinking)"
)
$ErrorActionPreference = "Continue"
$log = Join-Path $Worktree "agy-run.log"
function Log($m) { Add-Content -Path $log -Value ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) -Encoding UTF8 }

if (-not (Test-Path $Worktree)) { "worktree absent: $Worktree" | Add-Content $log; exit 1 }
if (-not (Test-Path $BriefFile)) { Log "brief absent: $BriefFile"; exit 1 }

# Resoudre agy.exe, meme ordre que BinaryLocator.FindAgy : AGY_BIN, PATH, racines
# Antigravity connues (dont le lien WinGet observe en smoke reel, docs/141-ag-smoke.md).
$agy = $env:AGY_BIN
if (-not $agy -or -not (Test-Path $agy)) {
    $onPath = Get-Command agy -ErrorAction SilentlyContinue
    $agy = if ($onPath) { $onPath.Source } else { $null }
}
if (-not $agy) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Antigravity\agy.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Antigravity\bin\agy.exe"),
        (Join-Path $env:LOCALAPPDATA "agy\bin\agy.exe"),
        (Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\agy.exe")
    )
    $agy = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $agy) { Log "agy.exe introuvable (AGY_BIN / PATH / racines Antigravity connues)"; exit 1 }

# Mode abonnement : pas de clef API dans l'env du process.
$env:OPENAI_API_KEY = $null
$env:ANTHROPIC_API_KEY = $null

# --- Contexte partage entre les trois agents (demande de Hajar, 2026-08-05) -------------
# agy ne peut lire que ce qui est sous --add-dir : sans cette etape il ne voit ni la
# memoire commune, ni CLAUDE.md, ni les branches, et il se dephase de Claude et de codex.
# Cas reel du 2026-08-05 : un lot a conclu que tout le travail metier/UI de codex etait
# perdu alors qu'il vivait sur cinq branches codex/* poussees le jour meme -- son worktree
# detache ne montrait que le commit de base.
$memSrc = "C:\Users\najwa\.claude\projects\c--Users-najwa-OneDrive-Desktop-SERZENIA\memory"
if (Test-Path $memSrc) {
    $memDst = Join-Path $Worktree "memoire"
    New-Item -ItemType Directory -Force -Path $memDst | Out-Null
    Copy-Item -Path (Join-Path $memSrc "*.md") -Destination $memDst -Force
    Log "memoire partagee copiee dans $memDst"
}

# Etat git reel, branches distantes comprises. Un worktree partage le .git du depot : les
# branches SONT accessibles, encore faut-il que l'acteur sache qu'elles existent.
$gitState = Join-Path $Worktree "ETAT-GIT.md"
$gs = @("# Etat git du depot au demarrage du lot ($(Get-Date -Format 'yyyy-MM-dd HH:mm'))", "")
$gs += "## HEAD de ce worktree"
$gs += '```'
$gs += (& git -C $Worktree log -1 --format='%H %ad %s' --date=short 2>&1 | ForEach-Object { [string]$_ })
$gs += '```'
$gs += ""
$gs += "## Branches locales et distantes (plus recentes d'abord)"
$gs += '```'
$gs += (& git -C $Worktree branch -a --sort=-committerdate --format='%(refname:short) | %(committerdate:short) | %(subject)' 2>&1 | ForEach-Object { [string]$_ })
$gs += '```'
$gs += ""
$gs += "## Ecart de chaque branche distante par rapport a origin/main"
$gs += '```'
foreach ($br in (& git -C $Worktree for-each-ref --format='%(refname:short)' refs/remotes 2>&1)) {
    $b = [string]$br
    if (-not $b -or $b -match 'HEAD') { continue }
    $n = (& git -C $Worktree rev-list --count "origin/main..$b" 2>&1) -join ''
    $f = (& git -C $Worktree diff --name-only "origin/main...$b" 2>&1 | Measure-Object).Count
    $gs += "$b : $n commit(s) d'avance, $f fichier(s) modifie(s)"
}
$gs += '```'
$gs -join "`r`n" | Set-Content $gitState -Encoding UTF8
Log "etat git ecrit dans $gitState"

# Le prompt complet est copie dans le workspace de l'acteur (agy ne peut lire que ce qui
# est sous --add-dir) ; seule une consigne courte qui pointe dessus passe en argument.
# Le brief est prefixe du contexte commun : meme etat du monde pour les trois agents.
$promptFile = Join-Path $Worktree "agy-prompt.txt"
$shared = Join-Path $PSScriptRoot "briefs\_contexte-commun.md"
if (Test-Path $shared) {
    $parts = @((Get-Content $shared -Raw -Encoding UTF8), (Get-Content $BriefFile -Raw -Encoding UTF8))
    ($parts -join "`r`n") | Set-Content $promptFile -Encoding UTF8
    Log "brief prefixe du contexte commun"
} else {
    Copy-Item -LiteralPath $BriefFile -Destination $promptFile -Force
    Log "ATTENTION contexte commun introuvable ($shared) : brief seul"
}
$instruction = "Lis integralement le fichier agy-prompt.txt a la racine du workspace et " +
    "execute la consigne qu'il contient. Ce fichier porte ton prompt complet : ne demande " +
    "aucune clarification, ne resume pas le fichier, execute-le."

$last = Join-Path $Worktree "agy-last.txt"
Log "demarrage agy ($agy) dans $Worktree, modele=$Model"
Set-Location $Worktree

# -p / --print : prompt unique non interactif. --add-dir : seul repertoire lisible/ecrivible
# par l'acteur (le worktree porte a la fois le code et agy-prompt.txt). --dangerously-skip-permissions :
# auto-approbation des outils, necessaire pour dev + build + git commit par l'acteur lui-meme.
$agyOutput = & $agy -p $instruction --model $Model --add-dir $Worktree `
    --dangerously-skip-permissions --print-timeout 60m 2>&1
$exitCode = $LASTEXITCODE
$agyOutput | ForEach-Object { Add-Content -Path $log -Value ([string]$_) -Encoding UTF8 }
$agyOutput | Out-File -FilePath $last -Encoding UTF8
Log "agy termine (exit $exitCode). Sortie complete: $last"

# Alerte Slack de fin de delegation (meme mecanique que run-codex-lot.ps1, SERZENIA-155
# point 2), sur le canal #ag. Une delegation agy est detachee : sans ce ping, la fin passe
# inapercue. Non bloquant : une alerte qui echoue ne doit jamais faire echouer le lot.
try {
    $repoRoot = Split-Path $PSScriptRoot -Parent
    $notifyProj = Join-Path $repoRoot "tools\notify"
    $wtName = Split-Path $Worktree -Leaf
    $lastMsg = if (Test-Path $last) { (Get-Content $last -Raw -Encoding UTF8).Trim() } else { "" }
    $env:SPRINTLAUNCHER_HOME = $repoRoot
    # Le worktree de l'acteur peut porter un global.json qui epingle un SDK absent (SERZENIA
    # exige 9.0.300) : `dotnet run` echouerait alors sur "A compatible .NET SDK was not found"
    # alors que le lot lui-meme s'est bien deroule. On privilegie donc le notify.exe publie,
    # qui ne depend d'aucun SDK, et on ne retombe sur `dotnet run` qu'a defaut - en se placant
    # dans le repo SprintLauncher pour ne pas heriter du global.json de l'acteur.
    $notifyExe = Join-Path $notifyProj "published\notify.exe"
    $notifyArgs = @("--actor", "ag", "--level", "info",
        "--text", "Delegation agy terminee (exit $exitCode) : $wtName", "--context", $lastMsg)
    if (Test-Path $notifyExe) {
        & $notifyExe @notifyArgs |
            ForEach-Object { Add-Content -Path $log -Value ("[notify] " + [string]$_) -Encoding UTF8 }
    } else {
        Push-Location $repoRoot
        & dotnet run --project $notifyProj --verbosity quiet -- @notifyArgs |
            ForEach-Object { Add-Content -Path $log -Value ("[notify] " + [string]$_) -Encoding UTF8 }
        Pop-Location
    }
} catch {
    Log ("notify Slack echoue: " + $_.Exception.Message)
}
