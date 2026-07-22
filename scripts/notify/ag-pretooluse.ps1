. "$PSScriptRoot\common.ps1"

$payload = Read-HookPayload
$message = Get-HookValue $payload @('message', 'prompt', 'reason', 'permission.message')
$needsInput = Get-HookValue $payload @('requiresPermission', 'requires_permission', 'permissionRequired', 'needsInput')

if ($needsInput -match '^(true|1|yes)$' -or $message -match '(permission|approval|input|confirm)') {
    $tool = Get-HookValue $payload @('toolName', 'tool_name', 'tool')
    if (-not [string]::IsNullOrWhiteSpace($tool)) {
        $message = "${tool}: $message"
    }

    $context = Get-HookValue $payload @('context', 'session_id', 'conversationId', 'conversation_id')
    Invoke-SlNotify -Actor ag -Level blocked -Text $message -Context $context
}

exit 0
