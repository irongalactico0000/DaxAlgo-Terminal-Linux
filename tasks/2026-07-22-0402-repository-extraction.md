# Standalone Linux repository extraction
Status: complete

## Goal
Establish the Linux/Avalonia implementation as an independent private repository.

## Plan
Copy the clean Linux-owned source, tests, context, configuration, and optional runtime helpers; add
standalone repository guidance; regenerate context; build and publish the initial snapshot.

## Blast radius
All files in this new repository. No Professional implementation is included.

## Build filter
`TradingTerminal.Linux.slnx`

## Tests
Linux solution build, headless tests, context check, and secret-pattern review.

## Findings
The 35-project Linux graph is internally closed: no `ProjectReference` leaves `src/linux/` or
`tests/linux/`.

## Diff summary
Initial standalone repository snapshot extracted from public revision
`3822dc283e9c1305ac4dbcdd2b37c3a73f954efb`.

## Verification
The solution build, 481 headless tests, structural context check, deep generator check, and
credential-pattern scan passed before publication.

## Risks / deferred
The original public repository retains pre-split history. Optional native broker SDK binaries are
not included and remain local-only dependencies. GitHub Actions setup is deferred because the
current OAuth token does not have the `workflow` scope.
