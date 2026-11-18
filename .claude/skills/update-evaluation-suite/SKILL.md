---
name: update-evaluation-suite
description: Brings the C# ports of AWS's emulator evaluation suite up to date with upstream. Use when asked to update, sync or check the evaluation suite for drift, or to port its new checks.
---

# Update the evaluation suite

The ports live in `tests/InMemoryAws.Tests/AwsEvaluationSuite/`. Its `README.md` is the reference: the translation conventions (top), virtual time, known gaps, and "Keeping up with upstream" (how `UPSTREAM.md` and the tool work). Read those sections before porting anything.

## 1. Move the pin

```bash
dotnet run --project tools/SyncEvaluationSuite -- --ref main
```

Done when it prints the to-do counts and `Wrote ...UPSTREAM.md` (or reports nothing to do: then stop and say so). The work list is `artifacts/evaluation-suite/drift.md`.

## 2. Work every item in drift.md

Each section states its action; the upstream source of every new, stale or changed check is copied under `artifacts/evaluation-suite/upstream/`.

- **Porting a new check:** open a sibling port in the suggested folder that calls the same operations, and mirror its shape: header naming the pinned commit, `[Category("AwsEvaluationSuite")]`, `[DisplayName("<check id>")]`, `[Timeout(FidelityContext.HangTimeoutMilliseconds)]`, `FidelityContext`, and the Python's steps, waits (`FidelityTime`) and assertion messages kept line for line.
- **Not porting:** only when the check needs a service the library doesn't emulate. The reason reuses an existing reason from UPSTREAM.md's summary when one fits.
- **Stale port:** apply the diff's meaning to the C#, then bump the header commit.

Done when `dotnet run --project tools/SyncEvaluationSuite` reports no to-do rows.

## 3. Make the tests green

Build, then run each new and changed port, gaps included:

```bash
dotnet build tests/InMemoryAws.Tests -c Release
INMEMORYAWS_RUN_KNOWN_GAPS=1 dotnet run --project tests/InMemoryAws.Tests -c Release --no-build -- \
  --treenode-filter "/*/*/<ClassName>/*" --timeout 2m
```

A failure is a library bug to fix, or a known gap: `[KnownFidelityGap(FidelityGap.X, "why")]` with the closest root cause (add a `FidelityGap` value and its README entry only for a new root cause). A previously skipped test that now passes loses its attribute. Done when the whole `AwsEvaluationSuite` filter passes without `INMEMORYAWS_RUN_KNOWN_GAPS`.

## 4. Record it

Rerun the tool (gaps change the summary), then bring every count in `AwsEvaluationSuite/README.md` and the fidelity paragraph of `CLAUDE.md` into line with UPSTREAM.md's summary. Done when `dotnet run --project tools/SyncEvaluationSuite -- --check` exits 0.
