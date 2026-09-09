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

<!-- project-memory-management-graph: skill-version=10 -->
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
