# GK-015 — Configuration, logging, and failure safety

**Status:** Blocked
**Depends on:** GK-003, GK-006, GK-009, GK-011, GK-012
**Suggested branch:** `task/GK-015-configuration-log-safety`

## Outcome

Every runtime setting is typed and validated before a session starts, operational logs are useful without sensitive payloads, and all external boundaries have repeatable failure-injection coverage.

## Owned surface

- Typed options, validators, safe logging/redaction helpers, and operational health reporting
- Configuration examples and local-operation documentation
- Cross-adapter failure and log-leak tests
- Configuration/logging hook in `Program.cs` after GK-011 has merged

Do not redesign feature contracts or add deferred product behavior. Host edits are limited to composing the operational extension supplied by this task.

## Work

- Bind and validate cadence, freshness, timeouts, technical grace, Recovery Window, repeated-Recovery/coaching caps, JPEG settings, context bounds, and exact provider/model identifiers.
- Keep secrets in environment variables or Secret Manager, data under `%LocalAppData%\GoalKeeper`, and the default web binding loopback-only.
- Use structured safe metadata while excluding credentials, image/base64 bodies, raw audio, and full sensitive provider payloads.
- Inject timeout, invalid schema, rate-limit, network, stale-result, and cancellation failures at every external port.
- Distinguish technical, indeterminate, and behavioral outcomes in diagnostics.
- Add startup and log-capture tests, including representative secret/payload canaries.

## Acceptance criteria

- Invalid or incomplete configuration fails before a Focus Session starts with an actionable non-secret message.
- A fake-only development configuration starts without provider credentials.
- Log tests prove credentials, JPEG/base64 bodies, raw audio, and sensitive raw responses are absent.
- Every camera/Perception/Reasoning/Recovery failure is typed, auditable, and cannot become Deviation evidence by itself.
- Settings and their defaults are documented once and match runtime binding.

## Out of scope

- Long-duration soak and consented live-system acceptance
- Secret-vault deployment or remote hosting
- Product analytics
