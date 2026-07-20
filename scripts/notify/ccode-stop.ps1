. "$PSScriptRoot\common.ps1"
if (Test-SprintLauncherActorGuard) { exit 0 }

$payload = Read-HookPayload
$message = Get-HookValue $payload @('message', 'reason', 'stop_reason')
if ([string]::IsNullOrWhiteSpace($message)) {
    $message = 'Tache terminee.'
}

$context = Get-HookValue $payload @('context', 'session_id', 'transcript_path')
Invoke-SlNotify -Actor ccode -Level info -Text $message -Context $context
