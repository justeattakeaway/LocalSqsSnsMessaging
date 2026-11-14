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
- **Isolation:** every test gets its own `InMemoryAwsBus` on the real clock.
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

Checks that wait out a real AWS period (for example the 60 s minimum `MessageRetentionPeriod`)
are in the `TimeBased` category. Leave them out for a fast loop:
`--treenode-filter "/**[Category!=TimeBased]"`.

When a fix makes a skipped test pass, delete its attribute. To find every test for one root cause,
grep for its `FidelityGap` value: `grep -l "FidelityGap.Validation" Sqs/*.cs`.
