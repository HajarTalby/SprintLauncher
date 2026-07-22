param()

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $repoRoot 'tools\notify\notify.csproj'
$output = Join-Path $repoRoot 'tools\notify\published'

Push-Location $repoRoot
try {
    & dotnet publish $project -c Release --no-self-contained -p:PublishSingleFile=true -p:DebugType=None -o $output
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
} finally {
    Pop-Location
}
