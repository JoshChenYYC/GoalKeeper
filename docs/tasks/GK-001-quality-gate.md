# GK-001 — Reproducible quality gate and CI

**Status:** Done
**Owner:** Codex `/root/gk001`
**Branch:** `main` working tree
**Depends on:** None
**Suggested branch:** `task/GK-001-quality-gate`

## Outcome

A fresh checkout has one documented, automated quality gate for the retained Python reference and the .NET application, with no webcam, provider credential, microphone, or network inference requirement.

## Owned surface

- `.github/workflows/`
- Repository-level build/test configuration and developer scripts
- Test-running sections of `README.txt`
- Dependency lock or environment documentation

Do not change product behavior, agent prompts, or domain rules.

## Work

- Document isolated Python environment creation and dependency installation.
- Add CI jobs for Python tests and .NET restore, build, test, and format verification.
- Enable current static analysis and NuGet audit without suppressing actionable findings.
- Cache dependencies without caching application data or credentials.
- Ensure hardware/provider tests are opt-in and excluded from the default gate.
- Publish useful test logs or result artifacts on failure.

## Acceptance criteria

- A fresh checkout can run the documented local gate successfully.
- CI runs `python -m pytest -q` and the complete `GoalKeeper.sln` test suite.
- CI verifies formatting and fails on compiler/analyzer or vulnerable-package errors selected by repository policy.
- CI contains no secrets and performs no camera, microphone, or hosted-model call.
- Existing Python and .NET test counts do not regress.

## Out of scope

- Deployment or hosting
- Live hardware/provider smoke tests
- New product functionality
