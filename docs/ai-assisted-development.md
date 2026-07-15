# AI-Assisted Development

## Purpose

Cursor and Codex may accelerate implementation, testing, review, refactoring, and documentation. They do not own product decisions or replace engineering review.

AI-generated code is treated as an untrusted draft until it is understood, reviewed, and verified.

## Allowed uses

AI may assist with:

- boilerplate after the architecture is agreed;
- test-case discovery;
- focused implementation tasks;
- explaining unfamiliar code;
- code-review findings;
- refactoring proposals;
- documentation updates;
- threat-modelling prompts;
- migration and query review.

## Required workflow

For a non-trivial change:

1. Read `AGENTS.md` and relevant documentation.
2. Describe the requested product behaviour.
3. Identify missing or ambiguous business rules.
4. Propose affected files and layers.
5. Identify transaction, security, tenancy, and concurrency impact.
6. Propose tests.
7. Wait for approval before editing when the change is architectural.
8. Implement a narrow diff.
9. Run relevant verification commands.
10. Review the diff manually.
11. Record assumptions and follow-up work.

## Prompt pattern

```text
Read:
- AGENTS.md
- README.md
- docs/architecture.md
- docs/development-guidelines.md
- relevant ADRs

Task:
<one narrow task>

Before editing:
1. Summarize intended behaviour.
2. List assumptions and unresolved business questions.
3. List files expected to change.
4. Explain consistency, security, tenancy, and concurrency impact.
5. Propose tests.
6. Do not add packages or abstractions without justification.

After implementation:
1. Summarize changed files.
2. Explain important decisions.
3. List commands executed and results.
4. Report incomplete verification honestly.
5. Do not perform unrelated cleanup.
```

## Review pattern

Use a separate review request after implementation:

```text
Review the current diff without modifying files.

Check:
- correctness;
- domain invariants;
- transaction boundaries;
- optimistic concurrency;
- tenant isolation;
- authorization;
- mass assignment;
- error handling;
- query performance;
- test quality;
- unnecessary abstractions.

Return findings ordered by severity.
```

## Prohibited behaviour

AI must not:

- invent financial rules and implement them silently;
- confirm or reverse real financial operations autonomously;
- weaken tests to make them pass;
- add secrets, tokens, or credentials to the repository;
- run destructive Git or database commands without approval;
- perform broad refactors outside the agreed task;
- introduce infrastructure only because it is fashionable;
- claim tests passed when they were not run.

## Accountability

The human contributor remains responsible for:

- business decisions;
- architecture decisions;
- security;
- code correctness;
- test quality;
- licensing;
- dependency selection;
- final commit content.
