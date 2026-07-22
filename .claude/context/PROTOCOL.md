# Protocol — Linux context-first change contract

## Per request

1. Load `.claude/context/linux/index.md`, `symbols.md`, and `deps.json` before source.
2. Inspect `git status --short`; unrelated dirty files belong to the user.
3. Create `tasks/<YYYY-MM-DD-HHMM>-<slug>.md` for material changes.
4. Search the matching generated `index/` or `symbols/` shard before targeted source reads.
5. Use `deps.json` to identify direct dependents and tests before editing.
6. Make the smallest coherent change and keep optional broker SDKs behind Infrastructure seams.
7. Verify the narrowest project or test first; never use a bare `dotnet build`.
8. Regenerate Linux context after project, source-path, or public-surface changes.
9. Record changed files, checks, results, risks, and deferred work in the task record.

The Windows public core and Professional overlay are separate repositories. Do not read, mirror, or
modify them unless a request explicitly expands the scope. External issues, commits to other
repositories, pushes, releases, and messages require explicit authorization.

## Hard stops

Stop when work would require Windows/Pro implementation, credentials, destructive recovery, or an
unapproved external repository change. Never use destructive Git recovery or `--no-verify`.
