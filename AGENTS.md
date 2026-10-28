# FinOpsDesk - Repository Agent Instructions

These are repository-wide instructions for Cursor, Codex, and human contributors.

## Communication and language policy

**All user-facing communication MUST be in Polish.**

This is the default and mandatory communication language for this repository, regardless of the language used in a task prompt.

The following must be written in Polish:

- Conversational responses and explanations.
- Architectural analysis and implementation plans.
- Technical decisions, reasoning, assumptions, and trade-offs.
- Clarifying questions and requests for approval.
- Code review findings and recommendations.
- Implementation progress updates.
- Implementation summaries and final reports.
- Test result summaries and failure explanations.
- Security, concurrency, consistency, and performance analysis.
- Explanations of errors, exceptions, and unexpected behavior.
- Proposed next steps and outstanding issues.

**An English task prompt does not authorize switching the response language to English.**

If instructions arrive in English, understand and execute them normally, but communicate the results in Polish.

Only switch the communication language when the user explicitly requests a different language for a particular response.

### Technical artifacts remain in English

Use English for:

- Source code and identifiers.
- Class, interface, method, property, and variable names.
- Code comments and XML documentation.
- Test names and test descriptions in source code.
- Exception identifiers, error codes, and internal diagnostic identifiers.
- Git branch names, commit messages, and pull request titles.
- README files, ADRs, and technical documentation.
- API routes, request and response properties, and contracts.
- Database identifiers and migration names.

Preserve existing naming conventions and terminology.

Do not translate existing code, identifiers, technical documentation, or public contracts merely to satisfy the Polish communication requirement.

Preserve exact error messages, command output, stack traces, SQL statements, and code snippets when quoting them. Explain their meaning in Polish.

English technical terms such as `rowversion`, `Unit of Work`, `optimistic concurrency`, `deadlock`, `idempotency`, `Aggregate`, or `Value Object` may be used naturally within Polish explanations.

Do not force artificial Polish translations of established technical terminology.

User-facing application text follows the product's localization requirements, not this agent communication policy.

### Communication quality

Be precise, direct, and technically critical.

- Explain why a solution is appropriate, not only what it does.
- Identify important trade-offs and alternatives.
- Clearly distinguish verified behavior from assumptions.
- Explain financial invariants and failure scenarios when relevant.
- Do not automatically agree with proposals.
- Avoid unnecessary architectural complexity.
- Prefer concise reports for small changes and detailed reasoning for significant architectural decisions.

Do not switch to English in final summaries merely because the source code or task prompt is in English.

---

## Read before changing code

Read:

1. `README.md`
2. `docs/architecture.md`
3. `docs/development-guidelines.md`
4. Relevant records under `docs/adr`

For any non-trivial task, first explain in Polish:

- Intended product behaviour.
- Affected module and layer.
- Business assumptions.
- Consistency and transaction impact.
- Authorization and tenant-isolation impact.
- Planned tests.
- Files expected to change.

Do not edit files until the plan is accepted when the task is architectural or spans multiple layers.

---

## Planning and implementation approval

Architectural planning and implementation are separate activities.

When a task is marked `PLAN ONLY` or `PLANNING ONLY`:

- Inspect the repository and relevant documentation.
- Analyze the current implementation.
- Identify risks, assumptions, and trade-offs.
- Propose architecture and implementation steps.
- Do not modify source files, tests, configuration, documentation, or migrations.
- Do not create generated artifacts in the repository.
- Do not implement the proposed solution.

Wait for explicit user approval before implementation.

When implementation is approved:

- Implement only the explicitly approved scope.
- Keep changes narrow and independently reviewable.
- Do not start the next phase or increment automatically.
- Do not interpret approval of an architectural plan as approval of every implementation phase.
- Do not expand scope without explaining the reason and obtaining approval.
- Stop after the approved increment and report the results.

A successful implementation does not automatically authorize a commit or merge.

---

## Repository boundaries

- `FinOpsDesk.Api` is the host and composition root. It must not contain business rules.
- `FinOpsDesk.SharedKernel` must remain small and stable.
- A module represents a business capability, not a database table.
- The Reconciliation module owns invoices, bank transactions, match suggestions, and allocations.
- Modules must not reference the API host.
- A module must not access another module's internal database tables or internal types directly.
- Cross-module interactions require explicit contracts or events.
- Do not add projects, abstractions, or packages only to satisfy a pattern name.

---

## Domain rules

Domain code must not reference:

- ASP.NET Core.
- EF Core.
- MediatR.
- Logging frameworks.
- Authentication-provider types.
- HTTP requests, responses, or transport DTOs.
- Infrastructure or Presentation namespaces.

Keep invariants inside domain types.

Avoid public setters on aggregates.

Financial amounts use `decimal`, never `float` or `double`.

Do not invent undocumented business rules. State assumptions explicitly.

---

## Application rules

- Commands change state.
- Queries do not change state.
- MediatR may be used at application boundaries, not as a replacement for normal method calls.
- Expected business failures must not rely on unhandled exceptions.
- Cancellation tokens must flow from endpoint to handler and database call.
- Application contracts belong near the code that requires them.

---

## Persistence rules

- Use EF Core directly.
- Do not add a generic repository or redundant unit-of-work abstraction.
- Query handlers should normally use `AsNoTracking` and direct DTO projections.
- All list queries require bounded pagination and deterministic ordering.
- Tenant-owned data must be scoped by `OrganizationId`.
- Important mutable financial records use SQL Server `rowversion`.
- Idempotency must be enforced by a database uniqueness constraint, not only an application-side existence check.
- Do not edit an applied migration. Create a new migration.

---

## Security and tenancy

- Never trust an `OrganizationId` supplied by the client.
- Resolve user and organization from the authenticated server-side context.
- Every tenant-owned read and write must be organization-scoped.
- A global query filter may be defense in depth, but it is not the only security boundary.
- Do not bind HTTP request models directly to entities.
- Do not log secrets, tokens, full bank account numbers, or unrestricted request bodies.
- Every new state-changing endpoint requires an explicit authorization decision.
- Sensitive resource endpoints require BOLA/IDOR integration tests.

---

## Financial consistency

- Confirming or reversing an allocation must be atomic.
- Cross-currency allocation is outside the MVP.
- Allocation amount must be positive.
- A confirmed allocation must not exceed invoice outstanding amount or transaction unapplied amount.
- Confirmed financial operations are reversed, not hard-deleted.
- Audit records for financially relevant changes must be written in the same transaction.

---

## Frontend rules

When the frontend is added:

- Organize code by feature.
- Use TanStack Query for server state.
- Use React Hook Form and Zod for forms and validation.
- Do not copy query data into local component state without a deliberate draft model.
- Do not introduce Redux without a demonstrated cross-feature client-state problem.
- Handle loading, error, empty, forbidden, conflict, and success states deliberately.
- Do not store access tokens in `localStorage` or `sessionStorage`.

---

## Git ownership and restrictions

**The project owner exclusively manages Git mutations.**

Git staging, commits, branches, pull requests, and merges are performed manually by the project owner, primarily through GitExtensions.

AI agents must not modify Git state unless the project owner explicitly authorizes a specific operation.

### Allowed read-only Git commands

Examples:

- `git status`
- `git diff`
- `git diff --check`
- `git log`
- `git branch --show-current`
- `git ls-files`

### Forbidden Git operations

Do not run:

- `git add`
- `git commit`
- `git push`
- `git merge`
- `git rebase`
- `git reset`
- `git stash`
- `git checkout`
- `git switch`
- `git cherry-pick`
- `git restore`
- `git clean`

Do not:

- Create, delete, rename, or switch branches.
- Stage or unstage files.
- Create or modify commits.
- Rewrite Git history.
- Create pull requests.
- Merge pull requests.
- Modify Git configuration.
- Use alternative commands or tools to bypass these restrictions.

Do not use `git add -N` or other index-modifying techniques merely to include untracked files in a diff.

### Review requirements

All implementation changes must remain uncommitted and unstaged.

After implementation:

- Report modified and newly created files.
- Provide a complete review diff, including untracked files.
- Save review patches outside the repository.
- Do not modify the Git index to generate a patch.
- Run `git diff --check` where applicable.
- Report verification results before requesting acceptance.

The project owner decides when to stage, commit, create a pull request, and merge.

Do not automatically proceed to the next implementation phase after a successful review or test run.

---

## Database operation safety

- Do not perform destructive database operations without explicit approval.
- Do not drop or truncate production or shared databases.
- Do not rewrite historical migrations.
- Do not silently change existing financial data.
- Explain data migration and backward-compatibility risks before applying schema changes.
- Use the existing test database infrastructure for integration testing.
- Follow established migration and database fixture conventions.
- Distinguish isolated test database operations from production or shared environment operations.

---

## Verification

Before completing a backend change, run:

- `dotnet build FinOpsDesk.slnx`
- `dotnet test FinOpsDesk.slnx`
- `dotnet format --verify-no-changes`

Also perform relevant targeted tests and checks required by the approved task.

Report commands that were run and their results.

Report incomplete verification honestly.

When SQL Server-specific behavior matters, use real SQL Server integration tests.

Do not treat EF Core InMemory or SQLite as proof of SQL Server-specific locking, transactions, constraints, or concurrency behavior.

Do not report tests as passed unless they were actually executed successfully.

---

## AI-assisted development

AI output is a draft, not authority.

- Keep changes narrow and reviewable.
- Do not perform unrelated cleanup.
- Do not generate broad refactors without an accepted plan.
- Never weaken a test merely to make it pass.
- Do not add packages without explaining why the existing platform is insufficient.
- Do not change business behaviour silently.
- Summarize changed files, decisions, assumptions, and verification results.
- Ask before making a destructive database operation.
- Follow the strict Git ownership rules defined above.
- Do not claim correctness based only on successful compilation or a passing test count.
- Identify relevant unverified scenarios and limitations.

All reports to the project owner must follow the Polish communication policy.

---

## Definition of done

A change is complete only when:

- Intended behaviour is implemented.
- Relevant tests pass.
- Security and tenant isolation were considered.
- Consistency and failure behaviour were considered.
- Documentation is updated when the decision or workflow changed.
- No secrets or generated build artifacts are committed.
- The diff contains no unrelated changes.
- The approved implementation scope has been respected.
- Important limitations are documented.
- The implementation is ready for independent code review.

Implementation completion does not mean automatic authorization to commit or merge.

The project owner performs Git staging, commits, pull requests, and merges manually.
