param([Parameter(Mandatory)][int]$AppPid)
$ErrorActionPreference = 'Stop'

function Check([string[]]$UiArgs) {
    & winapp ui @UiArgs -a $AppPid --json | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "UI action failed: $($UiArgs -join ' ')" }
}

function Get-LunaReply {
    $tree = & winapp ui inspect -a $AppPid --json --depth 16 | ConvertFrom-Json
    function Get-Nodes($nodes) {
        foreach ($node in $nodes) {
            $node
            if ($node.children) { Get-Nodes $node.children }
        }
    }
    $messages = @(Get-Nodes $tree.windows.elements | Where-Object {
        $_.type -eq 'ListItem' -and $_.name -like 'AgentChatMessage { Role = Luna,*'
    })
    if ($messages.Count -eq 0) { return '' }
    return $messages[-1].name
}

Check @('invoke', 'NavAgent')
Check @('wait-for', 'CodexSignOutButton', '-t', '10000')
Check @('invoke', 'StartAgentButton')
Check @('wait-for', 'LunaState', '--value', 'Connected', '-t', '10000')
Check @('set-value', 'CodexTerminalInput', 'Hypothetically, a highly valued player on my roster is injured and absent from this weeks rankings. Should I drop him solely for that reason? Briefly name the injury-status and rest-of-season sources you would check. Do not inspect my actual roster or make any changes.')
Check @('invoke', 'SendTerminalInput')
$deadline = (Get-Date).AddSeconds(55)
$reply = ''
while ((Get-Date) -lt $deadline) {
    $reply = Get-LunaReply
    if ($reply -match '(?i)footballguys' -and $reply -match '(?i)nfl') { break }
    Start-Sleep -Seconds 2
}
if ($reply -notmatch '(?i)footballguys' -or $reply -notmatch '(?i)nfl') {
    throw "Luna did not use both default sources: $reply"
}
if ($reply -notmatch '(?i)hold|IR|rest.of.season|long.term|recovery' -or $reply -match '(?i)drop him solely|drop solely') {
    throw "Luna did not protect long-term value: $reply"
}
Write-Output "PASS: Luna named NFL injury information and Footballguys rest-of-season value without recommending a drop."
Write-Output $reply
