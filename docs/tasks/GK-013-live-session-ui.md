# GK-013 — Live Focus Session UI

**Status:** Ready
**Depends on:** GK-011
**Suggested branch:** `task/GK-013-live-session-ui`

## Outcome

The Blazor application supports mandatory preflight and the live Focus Session flow, showing authoritative state and exposing only valid, confirmed user commands.

## Owned surface

- Preflight/live components and feature-local styles under `GoalKeeper.Web`
- Shared Blazor shell, routes, navigation, and global style integration
- Presentation view models and Blazor component/host tests

This task owns shared web-shell files until GK-016. Do not access EF entities/provider SDKs, create an EF migration, or move business rules into components.

## Work

- Add preview, capture, retry, cancel, validation, acceptance, and explicit confirmation UX.
- Start monitoring only after confirmed contract and successful mandatory preflight.
- Display Focus Timer, authoritative state, Scheduled Break countdown, projected end, monitoring health, and Recovery status.
- Add confirmed Complete Goal and End Early controls only in allowed states.
- Surface the generic GK-010 Recovery interaction slot so text works immediately and GK-012 voice can plug in independently.
- Handle reconnect/navigation safely without implying process crash recovery.
- Use accessible labels, keyboard flow, error summaries, and clear camera/microphone indicators.

## Acceptance criteria

- Component/host tests cover preflight retry/cancel/confirm and prevent start before success.
- Displayed timing/state comes from application queries, not client-derived authority.
- Complete Goal and End Early require confirmation and issue one idempotent command.
- Scheduled Break and Monitoring Unavailable UI cannot submit behavioral evidence.
- Terminal navigation releases runtime resources and offers the optional review route.

## Out of scope

- Session Review/history/deletion behavior
- Remote control or multiple concurrent sessions
- Browser-side authoritative timing
