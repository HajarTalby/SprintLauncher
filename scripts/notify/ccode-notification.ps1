. "$PSScriptRoot\common.ps1"
if (Test-SprintLauncherActorGuard) { exit 0 }

$payload = Read-HookPayload
$message = Get-HookValue $payload @('message', 'notification', 'prompt', 'reason')
if ([string]::IsNullOrWhiteSpace($message)) {
    $message = 'Claude attend une permission ou un input.'
}

$context = Get-HookValue $payload @('context', 'session_id', 'transcript_path')
Invoke-SlNotify -Actor ccode -Level blocked -Text $message -Context $context
