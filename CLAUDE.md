@AGENTS.md

# Claude Code notes

Everything in `AGENTS.md` applies. These notes only add Claude-specific behaviour.

- **Plan mode** for any change under `src/Kernel/`, any migration, and anything touching tenancy,
  authentication, or money. Present the plan and wait for approval before editing.
- **Reviewer subagent** (`.claude/agents/reviewer.md`): run it on the full diff before opening every
  PR. Fix blocking findings; list non-blocking ones in the PR description.
- **Test-writer subagent** (`.claude/agents/test-writer.md`): use it for test-only work. It never
  edits `src/`.
- **Hand-write items:** never use Write or Edit on a hand-write file beyond its stub. When Nika asks
  for help there, answer in text following the hint ladder in `AGENTS.md` section 6.
- Verify before claiming: run the build and tests yourself and report the actual output.
