[CmdletBinding()]
param(
    [string]$LocalAiRoot = 'C:\Users\danfl\dev\LocalAI',
    [string]$StrataUrl = 'http://127.0.0.1:8080',
    [string]$PromptPath,
    [string]$SessionPath
)

$ErrorActionPreference = 'Stop'
$taskRepoRoot = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $taskRepoRoot
$taskBranch = (git branch --show-current).Trim()
if ($taskBranch -ne 'codex/ux-overhaul-qwen38-iq2-xs') {
    throw "This experiment must run on codex/ux-overhaul-qwen38-iq2-xs; current branch: $taskBranch"
}
if (-not $PromptPath) {
    $PromptPath = Join-Path $taskRepoRoot 'docs\ux-overhaul-qwen38-prompt.md'
}
if (-not (Test-Path -LiteralPath $PromptPath)) { throw "Prompt missing: $PromptPath" }

$taskHealth = Invoke-RestMethod "$StrataUrl/health" -TimeoutSec 10
if (-not $taskHealth.loaded -or $taskHealth.model -ne 'qwen3.8-flash-next-iq2_xs') {
    throw 'The requested Qwen3.8-Flash-Next IQ2_XS model is not loaded in Strata.'
}
$taskRunDir = Join-Path $taskRepoRoot ('artifacts\qwen38-ux-overhaul\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$taskAgentDir = Join-Path $taskRunDir 'agent'
New-Item -ItemType Directory -Force -Path $taskAgentDir, (Join-Path $taskRunDir 'sessions') | Out-Null
$taskConfig = @{
    providers = @{
        strata = @{
            baseUrl = "$StrataUrl/v1"
            api = 'openai-completions'
            apiKey = 'local-no-key'
            models = @(@{
                id = 'strata'
                name = 'Qwen3.8-Flash-Next GSQ-RCO IQ2_XS'
                reasoning = $false
                input = @('text')
                contextWindow = $taskHealth.max_context
                maxTokens = 8192
                cost = @{input=0; output=0; cacheRead=0; cacheWrite=0}
            })
        }
    }
}
$taskConfig | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $taskAgentDir 'models.json')
@{packages=@(); compaction=@{enabled=$true; reserveTokens=8192; keepRecentTokens=12000}} |
    ConvertTo-Json -Depth 4 | Set-Content (Join-Path $taskAgentDir 'settings.json')
@{
    provider = 'exa'
    webSearch = @{allowedProviders=@('exa', 'duckduckgo')}
    image = @{enabled=$false}
} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $taskAgentDir 'web-search.json')

$taskPiRoot = Join-Path $LocalAiRoot 'apps\PiHarness\runtime\pi-1.2.0'
$taskNodeRoot = Join-Path $LocalAiRoot 'apps\PiHarness\runtime\node-v22.19.0-win-x64'
$taskWebExtension = Join-Path $LocalAiRoot 'config\pi\npm\node_modules\pi-web-access\index.ts'
$taskContextExtension = Join-Path $PSScriptRoot 'qwen38-context-limits.ts'
$taskSkillsRoot = Join-Path $env:USERPROFILE '.codex\plugins\cache\microsoft-winui\winui\0.3.0\skills'
$env:PI_CODING_AGENT_DIR = $taskAgentDir
$env:PI_CODING_AGENT_SESSION_DIR = Join-Path $taskRunDir 'sessions'
$env:NODE_PATH = Join-Path $taskPiRoot 'node_modules'
$env:PATH = "$taskNodeRoot;$env:PATH"
$env:PI_OFFLINE = '1'
$env:PI_TELEMETRY = '0'

$taskArguments = @(
    '--provider', 'strata', '--model', 'strata',
    '--print', '--mode', 'json', '--no-mcp', '--approve',
    '--no-extensions', '--extension', $taskWebExtension,
    '--extension', $taskContextExtension,
    '--no-skills', '--no-prompt-templates', '--no-themes',
    '--tools', 'read,edit,write,powershell,grep,find,ls,web_search,fetch_content,get_search_content',
    '--session-dir', $env:PI_CODING_AGENT_SESSION_DIR,
    '--name', 'MFL UX overhaul - Qwen3.8 IQ2_XS'
)
foreach ($taskSkill in @('winui-design', 'winui-code-review', 'winui-dev-workflow', 'winui-ui-testing')) {
    $taskArguments += @('--skill', (Join-Path $taskSkillsRoot "$taskSkill\SKILL.md"))
}
if ($SessionPath) { $taskArguments += @('--session', $SessionPath) }
$taskArguments += @("@$PromptPath")
$taskState = @{
    status = 'running'
    started_at = [DateTimeOffset]::Now.ToString('o')
    branch = $taskBranch
    model = $taskHealth.model
    context = $taskHealth.max_context
    base_commit = (git rev-parse HEAD).Trim()
    prompt = $PromptPath
    run_directory = $taskRunDir
    harness = 'pi-1.2.0'
}
$taskState | ConvertTo-Json | Set-Content (Join-Path $taskRunDir 'run-state.json')
$taskRunDir | Set-Content (Join-Path $taskRepoRoot 'artifacts\qwen38-ux-overhaul\latest-run.txt')
Write-Output "Executing $($taskHealth.model); transcript: $taskRunDir\transcript.jsonl"
try {
    & (Join-Path $taskPiRoot 'pi.cmd') @taskArguments 1> (Join-Path $taskRunDir 'transcript.jsonl') 2> (Join-Path $taskRunDir 'harness.stderr.log')
    $taskExitCode = $LASTEXITCODE
    $taskLastAssistant = $null
    Get-Content (Join-Path $taskRunDir 'transcript.jsonl') | ForEach-Object {
        if ($_ -like '{"type":"message_end"*') {
            try {
                $taskEvent = $_ | ConvertFrom-Json -Depth 30
                if ($taskEvent.message.role -eq 'assistant') { $taskLastAssistant = $taskEvent.message }
            } catch { }
        }
    }
    $taskState.model_stop_reason = $taskLastAssistant.stopReason
    $taskState.report_present = Test-Path (Join-Path $taskRepoRoot 'docs\ux-overhaul-qwen38-report.md')
    if ($taskExitCode -eq 0 -and ($null -eq $taskLastAssistant -or $taskLastAssistant.stopReason -in @('error', 'aborted'))) {
        $taskExitCode = 2
        $taskState.model_error = $taskLastAssistant.errorMessage
    }
    $taskState.status = if ($taskExitCode -eq 0) { 'finished' } else { 'failed' }
    $taskState.exit_code = $taskExitCode
} catch {
    $taskState.status = 'failed'
    $taskState.error = $_.Exception.Message
    throw
} finally {
    $taskState.finished_at = [DateTimeOffset]::Now.ToString('o')
    $taskState | ConvertTo-Json | Set-Content (Join-Path $taskRunDir 'run-state.json')
}
Write-Output "Model run $($taskState.status); exit code $taskExitCode"
exit $taskExitCode
