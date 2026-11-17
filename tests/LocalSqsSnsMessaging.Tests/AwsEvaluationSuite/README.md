# AWS emulator evaluation suite (SQS, SNS, EventBridge and DynamoDB)

These are C# ports of the SQS, SNS, EventBridge and DynamoDB checks in AWS's
[emulator evaluation suite](https://github.com/aws/aws-emulator-evaluation-suite), pinned at the
commit recorded in [UPSTREAM.md](UPSTREAM.md), which lists every upstream check in the tracked
directories and what became of it here (see [Keeping up with upstream](#keeping-up-with-upstream)). Each upstream `check_<name>.py` was verified against real AWS. Each one
becomes one test file here, with the same steps, waits and assertion messages, so the two can be
compared side by side:

| Upstream | Here | Display name |
|---|---|---|
| [`behavioral/sqs/check_<name>.py`](https://github.com/aws/aws-emulator-evaluation-suite/tree/main/behavioral/sqs) | `Sqs/<Name>.cs` | `sqs.<id>` |
| [`behavioral/sns/check_<name>.py`](https://github.com/aws/aws-emulator-evaluation-suite/tree/main/behavioral/sns) | `Sns/<Name>.cs` | `sns.<id>` |
| [`behavioral/sns__sqs/check_<name>.py`](https://github.com/aws/aws-emulator-evaluation-suite/tree/main/behavioral/sns__sqs) | `SnsSqs/<Name>.cs` | `sns__sqs.<id>` |
| [`behavioral/events/check_<name>.py`](https://github.com/aws/aws-emulator-evaluation-suite/tree/main/behavioral/events) | `Events/<Name>.cs` | `events.<id>` |
| [`behavioral/events__sqs/check_<name>.py`](https://github.com/aws/aws-emulator-evaluation-suite/tree/main/behavioral/events__sqs) | `EventsSqs/<Name>.cs` | `events__sqs.<id>` |
| [`behavioral/dynamodb/check_<name>.py`](https://github.com/aws/aws-emulator-evaluation-suite/tree/main/behavioral/dynamodb) | `DynamoDb/<Name>.cs` | `dynamodb.<id>` |
| [`behavioral/dynamodbstreams/check_<name>.py`](https://github.com/aws/aws-emulator-evaluation-suite/tree/main/behavioral/dynamodbstreams) | `DynamoDbStreams/<Name>.cs` | `dynamodbstreams.<id>` |

- **Same verdicts:** each test gets the same verdict the Python check got when it was run with boto3
  against `LocalSqsSnsMessaging.Server`.
- **Real AWS clients:** tests use real `AmazonSQSClient`s, `AmazonSimpleNotificationServiceClient`s,
  `AmazonEventBridgeClient`s, `AmazonDynamoDBClient`s and `AmazonDynamoDBStreamsClient`s over the in-memory HTTP handler, through `FidelityContext`, the C# version of the Python `ctx`.
- **Exactly the check's requests:** the test run's SDK tracing adds trace context to every request
  (a `traceparent` message attribute on Publish, an `AWSTraceHeader` on SendMessage), which boto3
  doesn't, so `FidelityContext` turns that off for the whole check.
- **Isolation:** every test gets its own `InMemoryAwsBus`, on its own virtual clock. The bus turns on
  the library's strict KMS mode, `KmsKeyAliases`, so it refuses unknown KMS aliases, as AWS does.
  Regions need no setting: the library always serves each region's resources separately.
- **Left out:** checks that also need IAM, KMS, CloudFormation, CloudWatch, Lambda or STS role
  assumption are not ported, nor SNS checks about Lambda, Firehose or HTTP/S endpoints built from
  them, nor EventBridge checks
  about targets and features the library doesn't emulate (see below). A check that only reads the
  account id from `sts:GetCallerIdentity` is ported, with `ctx.Account` (and `ctx.CallerArn`, the
  account's root user) in its place.

## Keeping up with upstream

[UPSTREAM.md](UPSTREAM.md) is a snapshot of every check in the tracked upstream directories at a
pinned upstream commit: the last commit to change each check, and whether it is ported (and where),
a known gap, or not ported (and why). `tools/SyncEvaluationSuite` writes it from upstream and the
ports; only the reasons are written by hand.

Each port records the upstream commit it was translated at, in its header:

```csharp
// Translated from behavioral/sqs/check_<name>.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
```

A port is current while upstream's file at the pinned commit is the same as at that commit, and
**stale** once upstream changes it.

```bash
# Is there drift upstream? (writes nothing; exits 1 if there is)
dotnet run --project tools/SyncEvaluationSuite -- --ref main --check

# Move the pin to upstream main: rewrites UPSTREAM.md and writes the work list
dotnet run --project tools/SyncEvaluationSuite -- --ref main

# Refresh UPSTREAM.md after changing ports or reasons (the pin stays)
dotnet run --project tools/SyncEvaluationSuite
```

The tool keeps a blob-less clone of upstream in `artifacts/evaluation-suite/upstream.git`, and
writes the work list to `artifacts/evaluation-suite/drift.md`: each new, stale, changed or removed
check, with the upstream diff, a local copy of its source and a suggested file for its port. To
update the suite, move the pin, then work through the list:

- **new:** port it (the header names the pinned commit), or write a reason into its row.
- **stale:** apply the diff to the port, and set the commit in its header to the pin.
- **review:** a check that isn't ported changed upstream. Check that its reason still holds, then
  set its status back to `not ported`.
- **removed upstream:** delete the port, or move it when the list says a new check is its rename.
- **new upstream directories:** add a `## behavioral/<directory>` heading to UPSTREAM.md to track
  one.

Run the new and changed tests, mark the ones that don't pass with `KnownFidelityGap`, run the tool
again until it reports nothing to do, and update the counts in this README and in `CLAUDE.md` from
UPSTREAM.md's summary. The build runs the tool with `--check`, so UPSTREAM.md always matches the
ports, and a weekly workflow (`evaluation-suite-drift`) runs `--ref main --check` to report drift.

## SQS scope

The SQS ports cover SQS without the services around it: 271 of the 306 checks in `behavioral/sqs`.
These 35 checks are not ported, because they need another service ([UPSTREAM.md](UPSTREAM.md)
gives each one's reason):

- **IAM roles, users and identity policies** (16): a check that assumes a role or creates a user,
  and grades what that identity's policies, or a queue policy naming it, allow.
- **CloudFormation stacks** (10).
- **KMS keys** (7): customer-managed keys for SSE-KMS.
- **CloudWatch alarms** (1).
- **Lambda functions, with IAM roles** (1).

## SNS scope

The SNS ports cover SNS as a message bus for SQS: standard and FIFO topics, SQS subscriptions,
Publish and PublishBatch, message attributes, filter policies, raw delivery, the delivered envelope
and its signature, FIFO ordering and deduplication, subscription dead-letter queues, deliveries to
deleted or misconfigured queues, topic and subscription attributes, tags, permissions and topic
policies, validation, email subscriptions (which stay pending confirmation), and the bookkeeping
of mobile push (platform applications and endpoints) and SMS (attributes, opt-outs, the sandbox):
219 of the 257 checks in `behavioral/sns` and `behavioral/sns__sqs`. One is a known gap, kept
deliberately: platform credentials are never checked against a push service, so tests can use
fake ones.

These 38 checks are not ported, because they need another service
([UPSTREAM.md](UPSTREAM.md) gives each one's reason):

- **HTTP/S or Lambda endpoints, which these checks build from Lambda functions and IAM roles** (13).
- **Firehose subscriptions (Firehose, S3, IAM)** (4).
- **CloudFormation stacks** (8).
- **CloudWatch Logs delivery-status logging (IAM, Logs)** (4).
- **KMS keys** (2).
- **IAM roles and role assumption** (7).


## EventBridge scope

The EventBridge ports cover EventBridge as a message router in front of SQS and SNS: event buses,
rules, event patterns and TestEventPattern, PutEvents and its validation and partial failures,
targets to SQS (standard and FIFO), SNS and other event buses, Input, InputPath and
InputTransformer, target dead-letter queues and retry policies, bus resource policies, tags,
archives and replays, scheduled rules, listing and pagination, and error codes: 186 of the 300
checks in `behavioral/events`, `behavioral/events__sqs`, the `events__iam__*` directories and the
`eventbridgev2*` directories.

Archives and replays are ported (31 checks): they are self-contained, need nothing outside the bus,
and run on the bus's clock like everything else.

IAM is not emulated, and the checks that touch it were decided one by one:

- **IAM only as setup** (5 checks): a check that creates a role only to put its ARN on a rule or
  target, and really asserts what EventBridge does with it, is ported. The port uses
  `ctx.RoleArn(name)`, a well-formed role ARN the bus stores and echoes but never assumes; the
  Python run's front proxy answers the IAM calls with a stub. Two of these also call
  `ec2:DescribeRegions` to pick a second region: the port uses `us-east-1`, as that call answers for
  `us-west-2`.
- **Resource policies** (queue and topic policies naming `events.amazonaws.com`, bus policies from
  PutPermission): ported. What the library enforces is described with each gap.
- **IAM enforcement** (8 checks): a check that assumes a role and grades what the role's own
  policies allow (a missing `iam:PassRole`, an identity-policy condition, a role denied by an
  explicit Deny) is not ported.

These 114 checks are not ported ([UPSTREAM.md](UPSTREAM.md) gives each one's reason):

- **EventBridge v2** (`eventbridgev2`, `eventbridgev2__iam__sqs`, `eventbridgev2__iam__sns__sqs`): a newer, separate API with buses addressed by ARN, subscribers, event sources, JSONata transformers and `PutRawEvents`, not the `events` API this library emulates (40).
- **IAM role and identity-policy enforcement**: each assumes an IAM role (`sts:AssumeRole`) and grades what that role's policies allow (8).
- **API destinations and connections** (HTTP targets, some behind Lambda function URLs) (22).
- **Global endpoints** (Route 53 health checks, failover and replication) (12).
- **Partner event sources** (SaaS partner accounts) (12).
- **Lambda targets** (4).
- **Step Functions targets** (4).
- **Kinesis targets** (1).
- **CloudWatch Logs targets** (1).
- **KMS keys** (customer-managed keys on buses, queues and topics) (5).
- **CloudFormation stacks** (5).

Five EventBridge ports stay known gaps:

- **No grant for EventBridge** (4, `FidelityGap.EventBridgeUngrantedTargets`): a queue or topic
  whose policy says nothing about EventBridge still receives its deliveries, as SNS deliveries do,
  because most test setups add no policy. A policy that denies EventBridge, or admits it only for
  other rules, is enforced. Real AWS answers a queue with no policy as `NO_PERMISSIONS` in one check
  and as `NO_RESOURCE` in another.
- **The runner's budget** (1, `FidelityGap.EmulatorBudget`):
  `events__sqs.events-archive-replay-rule-matching-replay-name-exists-false-is-not-redelivered`
  spends the runner's 60-second budget for an emulator in its own settle loops and drains, before
  it reaches the archive, so it fails against any emulator.

One port passes although its Python check failed against the server:
`events__sqs.custom-bus-fanout-is-at-least-once-and-order-independent`. Its own waits add up to
about 57 seconds, and the server's real request latency pushed it past the runner's 60-second budget.
On virtual time it finishes inside the budget, and every assertion holds.

## DynamoDB scope

The DynamoDB ports cover the whole of DynamoDB that needs no other service: tables and indexes,
item reads and writes, condition, update, projection and filter expressions and the legacy
parameters, Query and Scan, batches and transactions, PartiQL, consumed capacity, size and
validation limits, tags, resource policies, backups and point-in-time restore, time to live,
vector indexes and `SearchVectors`, global tables (replicas in other regions), DynamoDB Streams, the JSON wire protocol (`x-amz-crc32`, `__type`) and concurrency: 853 of the 902
checks in `behavioral/dynamodb` and `behavioral/dynamodbstreams`. 842 pass and 11 are known gaps.

`DynamoDbFidelity.cs` holds what the ports share:

- **Values as boto3 writes them:** `Ddb` builds attribute values and items
  (`Item(("pk", S("a")), ("n", N("1")))`), and `Py.Repr` and `Py.Equal` give Python's `repr()` and
  `==` over what boto3 would hand back, so assertion messages read as the Python's `{x!r}` does.
- **Waiters:** `WaitForTableExists` and `WaitForTableNotExists` are boto3's `table_exists` and
  `table_not_exists`, on the check's clock.
- **Raw requests:** `ctx.DynamoDbRaw(operation, json)` sends a hand-written body, for what the
  .NET SDK can't send (wrong JSON types, an attribute value with no type or two) and for checks
  that read the raw response.

The .NET SDK differs from boto3 in a few places the ports work around:

- **Requests boto3 refuses:** boto3 checks requests against the model before sending them. Checks
  that turn this off (`parameter_validation=False`) send through the ordinary client, which never
  checks. The SDK's Roslyn analyzers reject some literal constraint violations (`Limit = 0`, an
  empty table name), so those values come from fields.
- **Retries:** clients retry as the runner's do (standard mode, 3 attempts). The .NET SDK numbers
  every attempt `attempt=N; max=M` in `amz-sdk-request`, where botocore leaves `max` off the first,
  and the two retry checks assert the .NET form.
- **Refused calls:** an error throws, so a check's assertion that a refused call returned no
  `Items` stays as a comment.

The 10 checks that call STS only for the account id are ported with `ctx.Account`. These 49 checks
are not ported, because they need another service:

- **S3 (exports and imports)** (18).
- **Kinesis streaming destinations** (8).
- **CloudFormation stacks** (6).
- **Application Auto Scaling** (6).
- **KMS keys** (5).
- **IAM roles, role assumption and Lambda** (4).
- **CloudWatch metrics** (2).

The 11 known gaps, by root cause:

| `FidelityGap` | Checks | What the library doesn't do |
|---|---|---|
| `DynamoDbAuthentication` | 4 | Signature checks: an unsigned, incompletely signed or tampered request is served |
| `DynamoDbThroughput` | 4 | Throttling: provisioned throughput is reported, never enforced |
| `DynamoDbDetail` | 1 | Several constraint violations are listed in the model's member order, not in AWS's own order |
| `EmulatorBudget` | 2 | (see below) |

Two checks, `dynamodb.batch-write-item-and-delete-item-index-capacity-arms-follow-item-estate` and
`dynamodb.update-item-index-capacity-ladder-follows-key-vs-projected-change`, wait until
`time_left() - 60` seconds, which is no time at all under the runner's 60-second emulator budget,
so they fail against any emulator. With a longer budget the rest of the first one passes.

Every port gets its Python check's verdict, except the 10 checks that call STS only for the
account id: they errored in Python, because the server has no STS, and pass here (among them
`dynamodb.resource-policy-explicit-deny-on-getitem-rejected-put-still-allowed`, since a table
policy's explicit Deny statements are enforced).

## Known gaps

A check we don't pass yet carries `[KnownFidelityGap(FidelityGap.X, "why")]` and is skipped, so the
suite stays green. To work on a gap, run the skipped tests too:

```bash
# Everything in the suite, known gaps included (they fail until fixed)
LOCALSQSSNS_RUN_KNOWN_GAPS=1 dotnet run --project tests/LocalSqsSnsMessaging.Tests -c Release -- \
  --treenode-filter "/*/LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.*/*/*"

# One service: .Sqs, .Sns, .SnsSqs, .Events, .EventsSqs, .DynamoDb or .DynamoDbStreams
LOCALSQSSNS_RUN_KNOWN_GAPS=1 dotnet run --project tests/LocalSqsSnsMessaging.Tests -c Release -- \
  --treenode-filter "/*/LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns/*/*"

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
  (no request starting or finishing, no delay starting or ending) for 10 ms of real time, so it
  never moves under a check that is between two steps.
- **Request latency:** every request costs 1 ms of virtual time, so a polling loop with no wait
  in it still reaches its deadline. Concurrent requests also pause briefly in real time, so
  concurrent workers take turns; a check whose workers must interleave (Python threads) runs
  them inside `ctx.Concurrently()`, where every request pauses even if the thread pool hasn't
  scheduled the other workers yet.
- **Budgets and timeouts:** the runner's per-check budgets (`ctx.time_left()`) are in virtual
  time. Each test's `[Timeout]` is real time, and only stops a hung check.

SNS runs on the same clock: deliveries to SQS happen during the Publish, and a FIFO topic's
five-minute deduplication window and its message archive are measured on the bus's clock.
EventBridge does too: deliveries happen during PutEvents, and the clock drives replays and
scheduled rules. So does DynamoDB: time to live deletes, transaction token windows, stream
records' timestamps and point-in-time restore windows are all on the bus's clock. The context sets
`DynamoDbVectorIndexCreationDelay` to ten seconds, so a vector index UpdateTable adds is `CREATING`
(allocating, then backfilling) for a while before it is `ACTIVE`, as the checks expect of AWS, and
`DynamoDbTransitionDelay` to two seconds, so tables are `CREATING`, `UPDATING` (a throughput or
billing-mode change, an index build) and `DELETING`, global indexes `CREATING` and `DELETING`, and
contributor insights `ENABLING` and `DISABLING`, long enough for a check to see each status and be
refused what AWS refuses meanwhile.

Write new waits with `FidelityTime`, never `Task.Delay` or `Stopwatch`: a real wait doesn't
move the bus's clock, so whatever it waits for never happens.

Checks whose subject is timing (`timing:real-time` upstream) are in the `TimeBased` category.

When a fix makes a skipped test pass, delete its attribute. To find every test for one root cause,
grep for its `FidelityGap` value: `grep -rl "FidelityGap.SnsFifo" .`.
