param([Parameter(Mandatory)][int]$AppPid)
$ErrorActionPreference = 'Stop'
function Check([string[]]$UiArgs) {
    & winapp ui @UiArgs -a $AppPid --json
    if ($LASTEXITCODE -ne 0) { throw "UI check failed: $($UiArgs -join ' ')" }
}
Check @('invoke', 'NavAgent')
Check @('wait-for', 'CodexSignOutButton', '-t', '5000')
Check @('wait-for', 'CodexSignInButton', '--gone', '-t', '2000')
Check @('wait-for', 'DraftAgentToggle', '--value', 'Off', '-t', '2000')
function Get-LunaReply {
    $tree = & winapp ui inspect -a $AppPid --json --depth 16 | ConvertFrom-Json
    function Get-Nodes($Nodes) {
        foreach ($node in $Nodes) {
            $node
            if ($node.children) { Get-Nodes $node.children }
        }
    }
    $messages = @(Get-Nodes $tree.windows.elements | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'AgentChatMessage { Role = Luna,*' })
    return $messages[-1].name
}
Check @('invoke', 'DraftAgentToggle')
Check @('wait-for', 'DraftAgentToggle', '--value', 'On', '-t', '2000')
Check @('invoke', 'DraftAgentToggle')
Check @('wait-for', 'DraftAgentToggle', '--value', 'Off', '-t', '2000')
Check @('invoke', 'StartAgentButton')
Check @('wait-for', 'LunaState', '--value', 'Connected', '-t', '5000')
Check @('set-value', 'CodexTerminalInput', 'Reply with the current date and NFL week, then call mfl_list_leagues and report the selected league id. Do not make any changes. Be brief.')
Check @('invoke', 'SendTerminalInput')
Check @('wait-for', 'LunaState', '--value', 'Working', '-t', '3000')
Check @('wait-for', 'LunaState', '--value', 'Connected', '-t', '55000')
Check @('wait-for', 'DraftAgentToggle', '--value', 'Off', '-t', '2000')
$reply = Get-LunaReply
if ($reply -notmatch '2026' -or $reply -notmatch '(?i)week\D{0,5}4' -or $reply -notmatch '\d{5}') { throw "Unexpected date/tool response: $reply" }
if ($reply -match '(?i)unavailable|approval|could not|cannot|failed') { throw "Tool access failed: $reply" }
Write-Output "Verified live reply: $reply"
Check @('wait-for', 'CodexAccountEmail', '-t', '2000')
Check @('invoke', 'NavDashboard')
Check @('wait-for', 'CodexAccountEmail', '--gone', '-t', '2000')
Check @('invoke', 'NavAgent')
Check @('set-value', 'CodexTerminalInput', 'Use mfl_get_roster, mfl_get_free_agents and mfl_get_rankings for my selected league. Report roster and free-agent counts plus ranking week/readiness/staleness. Then use live web search for NFL week 4 news dated September 29 2026 or later. Discard older or undated evidence. If none qualifies, say so. Under 100 words, no mutations.')
Check @('invoke', 'SendTerminalInput')
Check @('wait-for', 'LunaState', '--value', 'Working', '-t', '3000')
Check @('wait-for', 'LunaState', '--value', 'Connected', '-t', '55000')
$reply = Get-LunaReply
if ($reply -notmatch '(?i)roster' -or $reply -notmatch '(?i)rank' -or $reply -match '(?i)tool approval|tool.*unavailable') { throw "League-data reply failed: $reply" }
Write-Output "Verified roster/rankings/web reply: $reply"
Check @('wait-for', 'DraftAgentToggle', '--value', 'Off', '-t', '2000')
Check @('set-value', 'CodexTerminalInput', 'Use live web search to research current-week injury reports. Advice only, no changes.')
Check @('invoke', 'SendTerminalInput')
Check @('wait-for', 'LunaState', '--value', 'Working', '-t', '3000')
Check @('invoke', 'StopAgentButton')
Check @('wait-for', 'LunaState', '--value', 'Stopped', '-t', '5000')
Check @('wait-for', 'SendTerminalInput', '-p', 'IsEnabled', '--value', 'True', '-t', '3000')
Check @('invoke', 'StartAgentButton')
Check @('wait-for', 'LunaState', '--value', 'Connected', '-t', '5000')
Check @('set-value', 'CodexTerminalInput', 'Reply exactly: Luna ready.')
Check @('invoke', 'SendTerminalInput')
Check @('wait-for', 'LunaState', '--value', 'Working', '-t', '3000')
Check @('wait-for', 'LunaState', '--value', 'Connected', '-t', '55000')
if ((Get-LunaReply) -notmatch 'Luna ready') { throw 'Luna did not recover after Stop/Start.' }
Write-Output 'PASS: live chat, resumed tools/web, account scope, draft toggle, Stop and recovery.'
