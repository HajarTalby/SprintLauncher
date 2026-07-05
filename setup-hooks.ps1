# setup-hooks.ps1 -- a executer une seule fois apres git clone
# Installe le pre-commit hook qui bloque git commit sans tests valides.

$hookDir  = Join-Path $PSScriptRoot ".git\hooks"
$hookFile = Join-Path $hookDir "pre-commit"

$hookContent = @'
#!/bin/sh
# Bloque le commit si des fichiers .cs sont modifies sans avoir execute test-before-commit.ps1
cs_changes=$(git diff --cached --name-only | grep '\.cs$')
if [ -z "$cs_changes" ]; then
    exit 0
fi
TEST_OK=".test-ok"
if [ ! -f "$TEST_OK" ]; then
    printf "\n  COMMIT BLOQUE -- .cs modifie, tests non executes.\n  Lancez: pwsh test-before-commit.ps1\n\n"
    exit 1
fi
ts=$(cat "$TEST_OK" | tr -d '[:space:]')
now=$(date +%s)
age=$((now - ts))
if [ "$age" -gt 1800 ]; then
    printf "\n  COMMIT BLOQUE -- tests expires (%ds > 30min).\n  Relancez: pwsh test-before-commit.ps1\n\n" "$age"
    exit 1
fi
printf "  Tests OK (%ds)\n" "$age"
exit 0
'@

if (-not (Test-Path $hookDir)) { New-Item -ItemType Directory -Path $hookDir | Out-Null }
[System.IO.File]::WriteAllText($hookFile, $hookContent.Replace("`r`n", "`n"))
Write-Host "Hook pre-commit installe : $hookFile"
Write-Host "Lancez 'pwsh test-before-commit.ps1' avant chaque commit sur du code .cs"
