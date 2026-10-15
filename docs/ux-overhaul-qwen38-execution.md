# Local model execution record

The user requested an application UX overhaul performed by Qwen3.8-Flash-Next IQ2_XS, with internet research informing the design. This record distinguishes orchestration by the supervising Codex agent from implementation by the requested local model.

## Prepared by the supervising agent

- Created `codex/ux-overhaul-qwen38-iq2-xs` from clean `main` at `a4733844727f9992de5320fa3784b10e76ce1f49`.
- Drafted `docs/ux-overhaul-qwen38-prompt.md` with research, implementation, architecture, safety, accessibility, and validation requirements.
- Added `tools/run-qwen38-ux-overhaul.ps1` to invoke the existing Pi 1.2.0 agent harness against Strata with an isolated configuration and session directory.
- Restricted the harness to the local Strata model, repository coding tools, and web search/page-fetch tools. It has no connected MFL MCP tools or copied account credentials.
- Confirmed Strata's health response identifies `qwen3.8-flash-next-iq2_xs`. The downloaded model shards reside in `D:\LocalAIData\Strata\models\IQ2_XS`; the engine is 0.1.43.1 using HIP on the RX 7900 XT.

## Runtime attempts

1. Started the existing IQ2_XS model with a task-specific 65,536-token context instead of the saved 200,000-token context. The engine's Windows memory-budget calculation left only 24 expert-cache slots (33 MiB). During the first 7,789-token prompt it reported roughly 15 prompt tokens/second, nearly full system RAM, and heavy SSD reads. The supervisor stopped this task's client and server before the model produced a response or edited application code.
2. Restarted the same model with a task-specific 32,768-token context, `--mmap-experts`, `--vram-reserve-mib 3072`, and the documented opt-in `STRATA_WDDM_BUDGET=0` override. This retained 9,137 expert-cache slots (about 12.2 GiB) and about 2.7 GiB free VRAM after startup. The saved LocalAI configuration was not edited. This override is an experiment on this machine, not a general recommendation to disable Windows memory budgeting.
   The model made 18 inspection tool calls (3 PowerShell, 8 directory listings, 7 file reads). It recovered from trying to read a directory as a file. It did not call the web tools or edit application code. The first prompt took about 126 seconds to read; individual completed replies decoded at approximately 23–39 tokens/second. The supervisor stopped the task's server after sustained paging slowed ordinary shell operations. The client subsequently recorded connection errors caused by that intentional stop. Pi returned process exit code 0 despite those error messages; the runner now checks the final assistant stop reason and reports such runs as failures.
3. Tried smaller 1,024-token prompt buffers and removing the optional MTP draft layer. A configuration with `--spec 0` was rejected by the installed native IQ pack, which requires at least `--spec 2`; this was a supervisor configuration error. The corrected `--spec 2 --prefill 1024` configuration requested 9,850 expert-cache slots (about 13.2 GiB), but failed during startup at pair 7,270 with `ExpertCache::fill_slot_blocking: unspecified launch failure`. No agent implementation run began under this configuration.

Machine resources: approximately 31.1 GiB usable system RAM, RX 7900 XT with 20 GiB VRAM, Ryzen 7 9700X. ComfyUI was idle with no queued generation and only a small model allocation; no user application was stopped or modified.

## Artifacts and interpretation

Full model event transcripts, Pi sessions, server/engine logs, and timestamped run-state files are under the git-ignored `artifacts/qwen38-ux-overhaul/` directory. `latest-run.txt` points to the latest agent run. These local artifacts are separate from the research/plan/report files the model is instructed to create.

## Baseline application validation

The supervisor verified the unchanged WinUI app with:

```powershell
dotnet build src/MflManager.App/MflManager.App.csproj -c Debug /p:Platform=x64 /p:SelfContained=true -r win-x64 /m:1
```

Restore and build succeeded with zero warnings and zero errors. The explicit self-contained property aligns the app and its executable project references. Prior checks found that the solution has no `Debug|x64` solution configuration, and that an app-only `--no-restore` build without aligned self-contained properties encounters NETSDK1150. These are recorded baseline invocation/configuration findings, not regressions introduced by the model. No tests or visual inspections of a UI overhaul have been completed, because no app changes were produced.

## Current outcome

The requested model was invoked with the full overhaul prompt and executed repository-inspection tools. It has not completed internet research, a design plan, or application edits. The application overhaul is incomplete. A user preference is pending on further IQ2_XS diagnosis versus using another model to finish implementation.

The prompt was refined to start competitor research earlier, and `tools/qwen38-context-limits.ts` caps each file read at 160 lines to avoid flooding the 32K context. These are supervisor changes to the harness/prompt, not work authored by Qwen. No application source files, account data, or league state were changed. A process exit or successful baseline build is not evidence that the requested overhaul succeeded.
