# Verification summary

Blockparty was built as a series of small production packages. Each package
had a written acceptance contract, a test suite written before the code, and a
machine-checked result record. This file summarises what was verified. The
full verification evidence lives in the development repository.

## Packages

- 152 production packages carry a machine-checked result record.
- 149 passed on first verification, 1 passed after a verified repair
  (save, accounts and food), and 1 is partial: live voice needs a signed-in
  Meta account at run time, so its offline path and fallbacks were verified
  instead.

## Test suites

- 81 C# test suites under `tests/production/`, one per package or feature
  area (for example `SAVE`, `ACCESS`, `CONTROLS-PLAY`, `AUDIO-MIX`, `HUD`,
  `CAMERA`, `SOAK`, `PERFORMANCE`, `LIVE-AI`, `NARRATION-AUDIO`).
- Most suites are plain C# with no Unity dependency and run against the
  deterministic simulation directly. Engine-backed suites run the Unity editor
  or the standalone player.
- Suites were written before the product code: red first, then green.

## Long-running sessions

- A one-hour soak of the standalone player with no input passed: the block
  kept running on its own for the full hour.

## What is not in this repository

Test suites that check recorded evidence (captures, logs, result records)
need that evidence, which is kept in the development repository. The game
itself builds and runs from this repository alone.
