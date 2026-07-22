. "$PSScriptRoot\common.ps1"
if (Test-SprintLauncherActorGuard) { exit 0 }

$payload = Read-HookPayload
$message = Get-HookValue $payload @('message', 'error', 'reason', 'stop_reason', 'terminationReason')
if ([string]::IsNullOrWhiteSpace($message)) {
    $message = 'Claude stoppe sur quota ou erreur de facturation.'
}

$context = Get-HookValue $payload @('context', 'session_id', 'transcript_path')
Invoke-SlNotify -Actor ccode -Level blocked -Text $message -Context $context
