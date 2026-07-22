. "$PSScriptRoot\common.ps1"

$payload = Read-HookPayload
$termination = Get-HookValue $payload @('terminationReason', 'termination_reason', 'reason', 'status')
$message = Get-HookValue $payload @('message', 'error', 'error.message', 'reason')
$context = Get-HookValue $payload @('context', 'session_id', 'conversationId', 'conversation_id')

if ($termination -match 'error' -and $message -match '(rate[_ -]?limit|quota|billing[_ -]?error|billing|429)') {
    Invoke-SlNotify -Actor ag -Level blocked -Text $message -Context $context
}

if ([string]::IsNullOrWhiteSpace($message)) {
    $message = 'Tache terminee.'
}

Invoke-SlNotify -Actor ag -Level info -Text $message -Context $context
