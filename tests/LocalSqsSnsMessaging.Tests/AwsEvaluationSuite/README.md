# AWS emulator evaluation suite (SQS)

These are C# ports of the SQS-only checks in AWS's
[emulator evaluation suite](https://github.com/aws/aws-emulator-evaluation-suite/tree/4df2823bafface2ada35f482c07ff6668cc078e1/behavioral/sqs),
pinned at commit `4df2823`. Each upstream `check_<name>.py` was verified against real AWS. Each one
becomes `Sqs/<Name>.cs` here, with the same steps, waits and assertion messages, so the two can be
compared side by side.

- **Same verdicts:** each test gets the same verdict the Python check got when it was run with boto3
  against `LocalSqsSnsMessaging.Server`.
- **Real AWS clients:** tests use real `AmazonSQSClient`s over the in-memory HTTP handler, through
  `FidelityContext`, the C# version of the Python `ctx`.
- **Isolation:** every test gets its own `InMemoryAwsBus`, on its own virtual clock. The bus turns on
  the library's two strict modes, `IsolateRegions` and `KmsKeyAliases`, so it serves each region's
  queues separately and refuses unknown KMS aliases, as AWS does.
- **Left out:** checks that also need IAM, KMS, CloudFormation, CloudWatch, Lambda or STS role
  assumption are not ported.

## Known gaps

A check we don't pass yet carries `[KnownFidelityGap(FidelityGap.X, "why")]` and is skipped, so the
suite stays green. To work on a gap, run the skipped tests too:

```bash
# Everything in the suite, known gaps included (they fail until fixed)
LOCALSQSSNS_RUN_KNOWN_GAPS=1 dotnet run --project tests/LocalSqsSnsMessaging.Tests -c Release -- \
  --treenode-filter "/*/LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs/*/*"

# One check
LOCALSQSSNS_RUN_KNOWN_GAPS=1 dotnet run --project tests/LocalSqsSnsMessaging.Tests -c Release -- \
  --treenode-filter "/*/*/QueueLevelWaitTime/*"
```

## Virtual time

The checks wait out real SQS periods: visibility timeouts, delays, long polls, the 60-second
minimum retention, the five-minute deduplication interval. Each runs on virtual time instead,
so the whole suite takes seconds:

- **The clock:** the bus's `TimeProvider` is a `FakeTimeProvider`. Checks wait and measure with
  `FidelityTime.Delay`, `FidelityTime.StartNew()` and `FidelityTime.UtcNow`, the virtual
  counterparts of `Task.Delay`, `Stopwatch.StartNew()` and `DateTimeOffset.UtcNow`.
- **What moves it:** time stands still while a check is working, and moves only while something
  waits on it: a `FidelityTime.Delay`, or a `ReceiveMessage` blocked in a long poll. While either
  is pending, `FidelityContext` advances the clock at 100 times real speed, in steps of at most
  a second that never pass the soonest pending deadline, so waits end exactly on time on every OS.
  Requests that are merely slow never move it, and the clock holds until the check has been quiet
  (no request starting or finishing) for 10 ms of real time, so it never moves under a check that
  is between two steps.
- **Request latency:** every request costs 1 ms of virtual time, so a polling loop with no wait
  in it still reaches its deadline.
- **Budgets and timeouts:** the runner's per-check budgets (`ctx.time_left()`) are in virtual
  time. Each test's `[Timeout]` is real time, and only stops a hung check.

Write new waits with `FidelityTime`, never `Task.Delay` or `Stopwatch`: a real wait doesn't
move the bus's clock, so whatever it waits for never happens.

Checks whose subject is timing (`timing:real-time` upstream) are in the `TimeBased` category.

When a fix makes a skipped test pass, delete its attribute. To find every test for one root cause,
grep for its `FidelityGap` value: `grep -l "FidelityGap.Validation" Sqs/*.cs`.
