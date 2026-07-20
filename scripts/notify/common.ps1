param()

function Test-SprintLauncherActorGuard {
    return $env:SPRINTLAUNCHER_ACTOR -eq '1'
}

function Read-HookPayload {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return $null
    }

    try {
        return $raw | ConvertFrom-Json -ErrorAction Stop
    } catch {
        return [pscustomobject]@{ message = $raw.Trim() }
    }
}

function Get-HookValue {
    param(
        [Parameter(Mandatory=$false)] $Payload,
        [Parameter(Mandatory=$true)] [string[]] $Names
    )

    if ($null -eq $Payload) {
        return $null
    }

    foreach ($name in $Names) {
        $parts = $name.Split('.')
        $current = $Payload
        foreach ($part in $parts) {
            if ($null -eq $current) {
                break
            }

            $property = $current.PSObject.Properties[$part]
            if ($null -eq $property) {
                $current = $null
                break
            }

            $current = $property.Value
        }

        if ($null -ne $current -and -not [string]::IsNullOrWhiteSpace([string]$current)) {
            return [string]$current
        }
    }

    return $null
}

function Invoke-SlNotify {
    param(
        [Parameter(Mandatory=$true)] [ValidateSet('ccode','ag','codex','sl')] [string] $Actor,
        [Parameter(Mandatory=$true)] [ValidateSet('info','warn','blocked')] [string] $Level,
        [Parameter(Mandatory=$true)] [string] $Text,
        [Parameter(Mandatory=$false)] [string] $Context
    )

    $repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
    $project = Join-Path $repoRoot 'tools\notify\notify.csproj'
    $args = @('run', '--project', $project, '--', '--actor', $Actor, '--level', $Level, '--text', $Text)
    if (-not [string]::IsNullOrWhiteSpace($Context)) {
        $args += @('--context', $Context)
    }

    & dotnet @args
    exit 0
}
