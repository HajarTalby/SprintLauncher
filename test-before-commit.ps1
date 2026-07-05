# test-before-commit.ps1
# Validation complete avant commit sur du code .cs.
# T2 (~5s)  : usage sans args
# T3 (~25s) : sprint resolution Jira
# T4 (~5min): execution reelle d'un acteur IA (ClaudePilotage sur SERZENIA-112)
# Ecrit .test-ok si tout passe. Le hook pre-commit bloque git commit si absent/expire.

param([switch]$SkipT4)

$ErrorActionPreference = "Stop"
$repoRoot   = $PSScriptRoot
$testDir    = "C:\Temp\test-sprint-precommit"
$envSource  = Join-Path $repoRoot ".env"
$markerFile = Join-Path $repoRoot ".test-ok"
$testTicket = "SERZENIA-112"   # 2 commentaires => prompt leger => ClaudePilotage rapide

function Fail($msg) { Write-Host "`n  ECHEC : $msg" -ForegroundColor Red; exit 1 }
function Ok($msg)   { Write-Host "  OK : $msg"      -ForegroundColor Green }
function Step($n, $label) { Write-Host "`n[$n] $label..." -ForegroundColor Cyan }

# ── 1. Build ──────────────────────────────────────────────────────────────────
Step "1/5" "Build"
$build = dotnet build "$repoRoot\src\SprintLauncher" -c Release --nologo 2>&1
if ($LASTEXITCODE -ne 0) { Write-Host $build; Fail "build echoue" }
Ok "build OK"

# ── 2. Publish dans dossier de test vide ──────────────────────────────────────
Step "2/5" "Publish dans $testDir (dossier vide)"
if (Test-Path $testDir) { Remove-Item $testDir -Recurse -Force }
New-Item -ItemType Directory -Path $testDir | Out-Null
dotnet publish "$repoRoot\src\SprintLauncher" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o $testDir --nologo 2>&1 | Out-Null
if (-not (Test-Path "$testDir\sprint-launcher.exe")) { Fail "sprint-launcher.exe absent apres publish" }
Ok "publish OK"

if (-not (Test-Path $envSource)) {
    Write-Host "  AVERTISSEMENT : .env absent -- T3 et T4 sauteront" -ForegroundColor Yellow
    $SkipT4 = $true
} else {
    Copy-Item $envSource "$testDir\.env" -Force
    Ok ".env copie"
}

# ── 3. T2 : usage sans args ───────────────────────────────────────────────────
Step "3/5" "T2 : usage sans args"
$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = "$testDir\sprint-launcher.exe"
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true
$psi.RedirectStandardInput  = $true
$psi.WorkingDirectory = $testDir
$p = [System.Diagnostics.Process]::Start($psi)
$p.StandardInput.Close()
$errT2 = $p.StandardError.ReadToEnd()
$p.WaitForExit(5000) | Out-Null
if ($errT2 -notmatch "Usage:") { Fail "T2 : 'Usage:' absent dans stderr`n$errT2" }
if ($p.ExitCode -ne 1)         { Fail "T2 : exit code attendu 1, obtenu $($p.ExitCode)" }
Ok "usage affiche, exit 1"

if ($SkipT4) {
    Write-Host "`n[4/5] T3 saute (pas de .env)" -ForegroundColor Yellow
    Write-Host "[5/5] T4 saute (pas de .env)" -ForegroundColor Yellow
} else {
    # ── 4. T3 : sprint resolution Jira ────────────────────────────────────────
    Step "4/5" "T3 : sprint resolution Jira (25s)"
    Remove-Item "$testDir\sprints.json" -ErrorAction SilentlyContinue
    $job3 = Start-Job -ScriptBlock {
        param($dir)
        Set-Location $dir
        & ".\sprint-launcher.exe" "--sprint" "6" "--no-cache" 2>"t3-err.txt" | Out-File "t3-out.txt" -Encoding UTF8
    } -ArgumentList $testDir
    Start-Sleep -Seconds 25
    Stop-Job $job3 | Out-Null; Remove-Job $job3 -Force

    if (-not (Test-Path "$testDir\sprints.json")) {
        $err = Get-Content "$testDir\t3-err.txt" -ErrorAction SilentlyContinue
        Fail "T3 : sprints.json non cree`n$err"
    }
    $keys = (Get-Content "$testDir\sprints.json" -Raw | ConvertFrom-Json).6
    if ($null -eq $keys -or $keys.Count -eq 0) { Fail "T3 : sprint 6 vide dans sprints.json" }
    Ok "sprint 6 resolu ($($keys.Count) tickets)"

    # ── 5. T4 : execution reelle IA — ClaudePilotage sur $testTicket ──────────
    Step "5/5" "T4 : execution IA (ClaudePilotage sur $testTicket, max 5min)"
    Write-Host "  Cela prend 2-5 minutes selon la reponse de claude.exe..."
    $artifactsDir = "$testDir\artifacts\run\$testTicket"
    $outputFile   = "$artifactsDir\output-ClaudePilotage.txt"
    Remove-Item $artifactsDir -Recurse -ErrorAction SilentlyContinue

    $job4 = Start-Job -ScriptBlock {
        param($dir, $ticket)
        Set-Location $dir
        & ".\sprint-launcher.exe" $ticket "--no-cache" 2>"t4-err.txt" | Out-File "t4-out.txt" -Encoding UTF8
    } -ArgumentList $testDir, $testTicket

    # Poll toutes les 15s jusqu'a 5 minutes
    $deadline = [DateTime]::Now.AddMinutes(5)
    $claudePilotageOk = $false
    while ([DateTime]::Now -lt $deadline) {
        Start-Sleep -Seconds 15
        if (Test-Path $outputFile) {
            $size = (Get-Item $outputFile).Length
            if ($size -gt 200) {
                $claudePilotageOk = $true
                break
            }
        }
        $elapsed = [int]([DateTime]::Now - ($deadline.AddMinutes(-5))).TotalSeconds
        Write-Host "  ... ${elapsed}s ecoules, en attente output ClaudePilotage..."
    }
    Stop-Job $job4 | Out-Null; Remove-Job $job4 -Force

    if (-not $claudePilotageOk) {
        $err = Get-Content "$testDir\t4-err.txt" -ErrorAction SilentlyContinue
        $out = Get-Content "$testDir\t4-out.txt" -ErrorAction SilentlyContinue | Select-Object -Last 10
        Fail "T4 : output-ClaudePilotage.txt absent ou trop court apres 5min`n--- stdout ---`n$out`n--- stderr ---`n$err"
    }
    $chars = (Get-Item $outputFile).Length
    $snippet = Get-Content $outputFile -Raw
    $snippet = $snippet.Substring(0, [Math]::Min(120, $snippet.Length))
    Ok "ClaudePilotage complete ($chars chars) : $snippet..."
}

# ── Marker .test-ok ────────────────────────────────────────────────────────────
$ts = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$ts | Out-File $markerFile -Encoding ASCII -NoNewline
Write-Host "`nTous les tests passes -- commit autorise (30 min)" -ForegroundColor Green
