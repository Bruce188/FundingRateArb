# Prompt Strategy Learnings

Patterns learned from review iterations that improve AI code generation quality. Updated after each review cycle.

## How to Use
After resolving `/review` findings, add entries here for any patterns worth remembering:
- What went wrong (the finding pattern)
- Why it happened (root cause in the prompt or context)
- How to prevent it (prompt improvement or CLAUDE.md update)

## Learnings

### Entity Default Alignment
**Finding pattern:** Entity default values in C# don't match database migration defaults.
**Root cause:** Prompts specify "update defaults" without requiring entity-migration parity verification.
**Improvement:** When prompts modify entity defaults, explicitly require: "Ensure C# property defaults match the EF migration `defaultValue` parameters."

### Test Synchronization Patterns
**Finding pattern:** Async tests use `Task.Delay` for timing, causing flaky results.
**Root cause:** AI defaults to `Task.Delay` for async coordination instead of deterministic synchronization primitives.
**Improvement:** Prompts for async test code should specify: "Use `TaskCompletionSource` or `SemaphoreSlim` for synchronization -- never `Task.Delay`."

### Defensive Copy in Cached Repositories
**Finding pattern:** Cached repository returns expose mutable references to shared cache entries.
**Root cause:** AI implements caching without considering mutation safety.
**Improvement:** When prompts involve caching, specify: "Return shallow copies from cached data -- never expose the cache entry directly."

### ViewModel Default Drift
**Finding pattern:** ViewModel fallback defaults diverge from updated entity defaults after entity changes.
**Root cause:** Prompts that update entity defaults don't chain to dependent ViewModels.
**Improvement:** When entity defaults change, explicitly require: "Update corresponding ViewModel defaults to match."

### Documentation-Code Parity
**Finding pattern:** Documentation values become stale when code defaults change in the same commit.
**Root cause:** Prompts update code but don't cross-reference documentation files.
**Improvement:** When prompts modify configuration defaults, require: "Update all documentation files that reference these defaults."

### Migration WHERE Clause Safety
**Finding pattern:** Data migrations use blanket UPDATE without WHERE clause, overwriting user-customized values.
**Root cause:** AI assumes single-tenant context without considering existing data.
**Improvement:** Data migration prompts should specify: "Add WHERE clause to UPDATE statements, scoping to rows matching previous default values only."
