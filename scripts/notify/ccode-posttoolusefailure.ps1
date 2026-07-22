. "$PSScriptRoot\common.ps1"
if (Test-SprintLauncherActorGuard) { exit 0 }

$payload = Read-HookPayload
$tool = Get-HookValue $payload @('tool_name', 'toolName', 'tool')
$errorMessage = Get-HookValue $payload @('error', 'message', 'reason')
if ([string]::IsNullOrWhiteSpace($tool)) {
    $tool = 'outil'
}
if ([string]::IsNullOrWhiteSpace($errorMessage)) {
    $errorMessage = 'echec apres utilisation outil.'
}

$context = Get-HookValue $payload @('context', 'session_id', 'transcript_path')
Invoke-SlNotify -Actor ccode -Level warn -Text "${tool}: $errorMessage" -Context $context
