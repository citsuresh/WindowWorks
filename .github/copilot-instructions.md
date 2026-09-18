## Project Guidelines

- At the very first user message in a new chat session (not on subsequent prompts within the
  same session), automatically invoke the `project-memory-management-graph` skill's Begin
  Session step before addressing the user's request — without asking for confirmation. Skip
  this only if the first prompt is clearly self-contained and unrelated to this codebase
  (e.g. general syntax/language questions, IDE/tool questions, or generic advice not
  requiring project context). If in doubt, run it — it is a cheap check.
- Manual commit review before any commit.
- Build/test verification after every change.
- Do not commit or push automatically — wait for explicit user confirmation first.
- **Sub-agent delegation workflow** (established during the Window Reparenting Phase 2 work;
  apply to all future sub-agent-delegated implementation work in this project):
  1. For each independent task/fix, launch a fresh background `general-purpose` sub-agent with
     full context. Never reuse one agent across unrelated tasks.
  2. Ask the sub-agent to perform a genuinely critical self-review of its own diff before
     reporting done — actively look for bugs, missed edge cases, and regressions rather than a
     cursory pass; state findings as facts, not confidence language.
  3. After the implementing sub-agent reports done, always perform your own (the orchestrating
     agent's) independent review pass in addition to step 2/3's sub-agent reviews — do not treat
     the sub-agents' reports as sufficient on their own.
  4. Independently verify with a separate `code-review` sub-agent against the actual source code
     (not the implementing agent's report text).
  5. If manual testing is needed to confirm a fix, wait for the user's explicit confirmation
     before stopping the implementation agent, so it can be resumed via `write_agent` if an issue
     is found.
  6. Only stop an agent after BOTH: (a) independent review passed, AND (b) the user has
     confirmed manual testing (or no testing was needed).
  7. If an issue is found before the agent is stopped, resume the SAME agent via `write_agent`
     with the findings rather than spawning a new one — it retains full context of its own prior
     change.
  8. Once approved and no longer needed, stop the agent if a stop mechanism is available in the
     current tool surface (best-effort cleanup only) — this is a hygiene step, not a correctness
     requirement, so don't block progress if no such mechanism exists or it fails.
  9. After each item/task is approved, post a percent-complete status report (table format)
     before moving to the next item.
  10. Do not stop to ask for confirmation before proceeding to the next item by default — only
     stop when (a) manual testing is required to confirm something, or (b) a genuine
     clarification/design decision is needed. Otherwise keep moving autonomously through the
     task list.
  11. Parallel sub-agents are unsafe for code-modifying work (race conditions on the shared
      working tree) — only run multiple agents in parallel if all of them are read-only
      (`code-review`/`explore`).
  12. Watch for a known false-positive review pattern: an independent `code-review` sub-agent
      may flag "touches unrelated files" if it diffs against the entire uncommitted working tree
      rather than just the current agent's actual new changes — cross-check any such "scope
      leakage" finding against the implementing agent's own reported file list before treating it
      as real.
  13. If the same underlying issue is reported by review 2+ times across attempted fixes for the
      same change, stop iterating with more agent round-trips — instead get harder evidence
      directly (e.g. a live `dotnet-dump` capture/inspection of the running process for hangs or
      hard-to-explain state bugs) before trying again.

<!-- project-memory-management-graph: skill-version=11 -->
## Persistent Project Memory

This section is fully regenerated on every Bootstrap run.

- Read `docs/CODE_SUMMARY.md` and `docs/DESIGN_DECISIONS.md` before exploring a new task when they exist; fall back to normal exploration if absent.
- Read `docs/PROJECT_STATE.md` and `docs/ROADMAP.md` for prior-work or next-step questions.
- Read `docs/domain-lookup-patterns.md` for domain conventions when it exists.
- Query `docs/full-graph.json` and `docs/project-dependencies.json` through `C:\MyFiles\Git\GraphTools\tools\Invoke-GraphTools.ps1 -Tool Query -- <args>`; never read the graph wholesale.
- Before general symbol search, check whether `docs/full-graph.json` exists and query it first for C# symbol definitions, callers, callees, and relationships. Fall back to normal search if the graph is absent, query fails, or cannot answer the question.
- Update `docs/CODE_SUMMARY.md` only for structural changes, preserving its `docs/KEY_FLOWS.md` pointer.
- Add confirmed multi-symbol flows to `docs/KEY_FLOWS.md` as concise arrow chains; domain details belong in `docs/domain-lookup-patterns.md`.
- Append non-obvious architectural decisions to `docs/DESIGN_DECISIONS.md`; overwrite `docs/PROJECT_STATE.md` at session end; change `docs/ROADMAP.md` only for deliberate priority changes.

## Response Guidelines

- Keep replies concise and minimal by default: no filler, restatement, or unnecessary preamble.
- Provide full detail for design rationale, build/error diagnosis, destructive actions, multiple approaches, and `docs/*.md` content generation.
