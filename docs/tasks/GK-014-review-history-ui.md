# GK-014 — Review, history, storage, and deletion UI

**Status:** In progress
**Owner:** Codex `/root`
**Branch:** `task/GK-014-review-history-ui`
**Depends on:** GK-003, GK-011
**Suggested branch:** `task/GK-014-review-history-ui`

## Outcome

Users can optionally review terminal sessions, inspect Goal history and local storage use, and perform explicitly confirmed safe session/Goal deletion without blocking return to the Goal list.

## Owned surface

- Review, history, storage, and deletion feature components under `GoalKeeper.Web`
- Feature-local styles and presentation mapping
- UI/integration tests for post-session workflows

This is UI-only work over GK-003 commands/queries. Do not create a migration, change repository contracts, edit shared navigation/global styles, or change the live controller.

## Work

- Show the optional Session Review after Fulfilled and Ended Early outcomes.
- Capture meaningful progress, Intervention helpfulness, optional note, and optional Goal completion.
- Allow skip and always return to the Goal list.
- Display Goal session history, terminal reason/state, immutable contract summary, review status, and per-session/total snapshot storage.
- Add confirmed Focus Session deletion while preserving its Goal.
- Keep confirmed Goal deletion cascading through all sessions and marker-owned artifacts.
- Present ownership-validation failures without deleting metadata.

## Acceptance criteria

- Review submission is optional, deterministic, and accepted at most once.
- Skipping review never blocks navigation.
- History renders immutable contract data rather than current Goal/profile text.
- Session deletion preserves the Goal; Goal deletion removes all associated rows and owned directories.
- Unowned or mismatched artifact directories block deletion and remain untouched.
- Storage totals match persisted snapshot byte counts.

## Out of scope

- Habit scoring or recommendations
- Automatic retention/deletion
- Editing historical contracts or reviews
