# test-before-commit.ps1
# Lance depuis la racine du repo SprintLauncher.
# Valide T2 (usage) + T3 (sprint resolution Jira) puis ecrit .test-ok.
# Le hook pre-commit bloque git commit si .test-ok est absent ou expire (>30min).

param([switch]$SkipT3)

$ErrorActionPreference = "Stop"
$repoRoot  = $PSScriptRoot
$testDir   = "C:\Temp\test-sprint-precommit"
$envSource = Join-Path $repoRoot ".env"
$markerFile = Join-Path $repoRoot ".test-ok"

function Fail($msg) { Write-Host "  ECHEC : $msg" -ForegroundColor Red; exit 1 }
function Ok($msg)   { Write-Host "  OK : $msg"    -ForegroundColor Green }

# ── 1. Build ──────────────────────────────────────────────────────────────────
Write-Host "`n[1/4] Build..." -ForegroundColor Cyan
$build = dotnet build "$repoRoot\src\SprintLauncher" -c Release --nologo 2>&1
if ($LASTEXITCODE -ne 0) { Write-Host $build; Fail "build echoue" }
Ok "build OK"

# ── 2. Publish dans dossier de test vide ──────────────────────────────────────
Write-Host "`n[2/4] Publish dans $testDir..." -ForegroundColor Cyan
if (Test-Path $testDir) { Remove-Item $testDir -Recurse -Force }
New-Item -ItemType Directory -Path $testDir | Out-Null
dotnet publish "$repoRoot\src\SprintLauncher" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o $testDir --nologo 2>&1 | Out-Null
if (-not (Test-Path "$testDir\sprint-launcher.exe")) { Fail "publish : sprint-launcher.exe absent" }
Ok "publish OK"

if (Test-Path $envSource) {
    Copy-Item $envSource "$testDir\.env" -Force
    Ok ".env copie"
} else {
    Write-Host "  AVERTISSEMENT : .env absent dans $repoRoot -- T3 saute" -ForegroundColor Yellow
    $SkipT3 = $true
}

# ── 3. T2 : usage sans args ───────────────────────────────────────────────────
Write-Host "`n[3/4] T2 : usage sans args..." -ForegroundColor Cyan
$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName           = "$testDir\sprint-launcher.exe"
$psi.UseShellExecute    = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true
$psi.RedirectStandardInput  = $true
$psi.WorkingDirectory   = $testDir
$p = [System.Diagnostics.Process]::Start($psi)
$p.StandardInput.Close()
$outT2 = $p.StandardOutput.ReadToEnd()
$errT2 = $p.StandardError.ReadToEnd()
$p.WaitForExit(5000) | Out-Null
if ($errT2 -notmatch "Usage:") { Fail "T2 : 'Usage:' absent dans stderr`n$errT2" }
if ($p.ExitCode -ne 1)         { Fail "T2 : exit code attendu 1, obtenu $($p.ExitCode)" }
Ok "T2 : usage affiche, exit 1"

# ── 4. T3 : sprint resolution Jira ────────────────────────────────────────────
if (-not $SkipT3) {
    Write-Host "`n[4/4] T3 : sprint resolution Jira (25s)..." -ForegroundColor Cyan
    Remove-Item "$testDir\sprints.json" -ErrorAction SilentlyContinue

    $job = Start-Job -ScriptBlock {
        param($dir)
        Set-Location $dir
        & ".\sprint-launcher.exe" "--sprint" "6" "--no-cache" 2>"t3-err.txt" | Out-File "t3-out.txt" -Encoding UTF8
    } -ArgumentList $testDir
    Start-Sleep -Seconds 25
    Stop-Job $job | Out-Null
    Remove-Job $job -Force

    if (-not (Test-Path "$testDir\sprints.json")) {
        $err = Get-Content "$testDir\t3-err.txt" -ErrorAction SilentlyContinue
        Fail "T3 : sprints.json non cree`n$err"
    }
    $keys = (Get-Content "$testDir\sprints.json" | ConvertFrom-Json).6
    if ($null -eq $keys -or $keys.Count -eq 0) { Fail "T3 : sprint 6 vide dans sprints.json" }
    Ok "T3 : sprint 6 resolu ($($keys.Count) tickets depuis Jira)"
} else {
    Write-Host "`n[4/4] T3 saute (pas de .env)" -ForegroundColor Yellow
}

# ── Marker .test-ok ────────────────────────────────────────────────────────────
$ts = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$ts | Out-File $markerFile -Encoding ASCII -NoNewline
Write-Host "`nTests valides -- commit autorise (30 min)" -ForegroundColor Green
