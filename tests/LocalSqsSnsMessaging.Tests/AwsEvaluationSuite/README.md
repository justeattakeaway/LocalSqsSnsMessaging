# AWS emulator evaluation suite (SQS and SNS)

These are C# ports of the SQS and SNS checks in AWS's
[emulator evaluation suite](https://github.com/aws/aws-emulator-evaluation-suite/tree/4df2823bafface2ada35f482c07ff6668cc078e1),
pinned at commit `4df2823`. Each upstream `check_<name>.py` was verified against real AWS. Each one
becomes one test file here, with the same steps, waits and assertion messages, so the two can be
compared side by side:

| Upstream | Here | Display name |
|---|---|---|
| [`behavioral/sqs/check_<name>.py`](https://github.com/aws/aws-emulator-evaluation-suite/tree/4df2823bafface2ada35f482c07ff6668cc078e1/behavioral/sqs) | `Sqs/<Name>.cs` | `sqs.<id>` |
| [`behavioral/sns/check_<name>.py`](https://github.com/aws/aws-emulator-evaluation-suite/tree/4df2823bafface2ada35f482c07ff6668cc078e1/behavioral/sns) | `Sns/<Name>.cs` | `sns.<id>` |
| [`behavioral/sns__sqs/check_<name>.py`](https://github.com/aws/aws-emulator-evaluation-suite/tree/4df2823bafface2ada35f482c07ff6668cc078e1/behavioral/sns__sqs) | `SnsSqs/<Name>.cs` | `sns__sqs.<id>` |

- **Same verdicts:** each test gets the same verdict the Python check got when it was run with boto3
  against `LocalSqsSnsMessaging.Server`.
- **Real AWS clients:** tests use real `AmazonSQSClient`s and `AmazonSimpleNotificationServiceClient`s
  over the in-memory HTTP handler, through `FidelityContext`, the C# version of the Python `ctx`.
- **Exactly the check's requests:** the test run's SDK tracing adds trace context to every request
  (a `traceparent` message attribute on Publish, an `AWSTraceHeader` on SendMessage), which boto3
  doesn't, so `FidelityContext` turns that off for the whole check.
- **Isolation:** every test gets its own `InMemoryAwsBus`, on its own virtual clock. The bus turns on
  the library's two strict modes, `IsolateRegions` and `KmsKeyAliases`, so it serves each region's
  queues separately and refuses unknown KMS aliases, as AWS does.
- **Left out:** checks that also need IAM, KMS, CloudFormation, CloudWatch, Lambda or STS role
  assumption are not ported, nor SNS checks about protocols other than SQS (see below). A check
  that only reads the account id from `sts:GetCallerIdentity` is ported, with `ctx.Account` (and
  `ctx.CallerArn`, the account's root user) in its place.

## SNS scope

The SNS ports cover SNS as a message bus for SQS: standard and FIFO topics, SQS subscriptions,
Publish and PublishBatch, message attributes, filter policies, raw delivery, the delivered envelope
and its signature, FIFO ordering and deduplication, subscription dead-letter queues, deliveries to
deleted or misconfigured queues, topic and subscription attributes, tags, permissions and
validation: 182 of the 257 checks in `behavioral/sns` and `behavioral/sns__sqs`.

These 75 checks are not ported, because they are about another protocol or need another service:

- **Other protocols: SMS, mobile push (platform applications and endpoints) and email** (30): `sns.check-phone-number-opted-out-invalid-fails`, `sns.create-platform-application-unknown-platform-fails`, `sns.create-platform-endpoint-nonexistent-app-fails`, `sns.create-sms-sandbox-invalid-number-rejected`, `sns.create-sms-sandbox-unsupported-country-code-rejected`, `sns.delete-endpoint-nonexistent-idempotent`, `sns.delete-platform-application-nonexistent-idempotent`, `sns.email-subscription-confirmation-state-attributes`, `sns.fifo-topic-rejects-email-protocol`, `sns.get-endpoint-attributes-not-found`, `sns.get-sms-attributes-unknown-name-rejected`, `sns.list-endpoints-by-nonexistent-application-fails`, `sns.list-platform-applications-invalid-next-token-rejected`, `sns.list-sms-sandbox-phone-numbers-max-results-over-limit-rejected`, `sns.opt-in-phone-number-invalid-rejected`, `sns.phone-number-opt-out-state`, `sns.platform-application-bogus-credential-rejected`, `sns.platform-application-custom-attribute-round-trip`, `sns.platform-application-not-found`, `sns.set-endpoint-attributes-not-found`, `sns.set-platform-application-attributes-unknown-key-rejected`, `sns.set-sms-attributes-invalid-name-rejected`, `sns.set-sms-attributes-invalid-sms-type-rejected`, `sns.sms-account-state-reads`, `sns.sms-attributes-custom-value-round-trip`, `sns.sms-attributes-filter-exactness-and-clear-via-empty`, `sns.sms-sandbox-delete-unknown-number`, `sns.subscribe-email-pending-confirmation`, `sns.verify-sms-sandbox-wrong-otp-rejected`, `sns__sqs.platform-application-event-endpoint-created-notifies-configured-topic`.
- **An email subscription, used to hold a subscription in `PendingConfirmation`** (7): `sns.pending-subscription-arn-placeholder-not-resolvable`, `sns.subscribe-to-nonexistent-topic-fails`, `sns.topic-attributes-subscription-counters-track-state`, `sns.topic-maximum-message-size-above-256kib-requires-eligible-subscriptions`, `sns.topic-policy-protocol-condition-blocks-a-real-signed-subscribe`, `sns.topic-subscription-counters-converge-after-concurrent-subscribes-and-unsubscribes`, `sns.unsubscribe-pending-subscription-rejected`.
- **HTTP/S or Lambda endpoints, which these checks build from Lambda functions and IAM roles** (13): `sns.authenticate-on-unsubscribe-refuses-the-anonymous-unsubscribe-url`, `sns.confirm-subscription-token-confirms-only-its-own-subscription-whatever-the-topic-arn`, `sns.confirm-subscription-token-is-reusable-until-unsubscribe-then-not-found`, `sns.cross-region-lambda-subscription-invokes-the-function-in-its-own-region`, `sns.https-endpoint-failing-with-500-is-retried-then-dead-lettered`, `sns.https-raw-message-delivery-posts-the-bare-body-with-the-rawdelivery-header`, `sns.https-subscription-receives-nothing-until-confirmed-then-gets-the-notification`, `sns.https-subscription-with-a-zero-retry-delivery-policy-attempts-a-failing-notification-exactly-once`, `sns.lambda-feedback-roles-log-the-invocation-delivery-record-and-the-uninvokable-function-failure`, `sns.lambda-function-error-is-retried-by-lambda-and-never-dead-lettered-by-sns`, `sns.lambda-subscription-invocation-carries-the-sns-records-envelope`, `sns.lambda-subscription-the-function-refuses-to-sns-dead-letters-into-the-subscription-dlq`, `sns.raw-message-delivery-drops-message-attributes-for-an-https-subscriber-but-keeps-them-for-sqs`.
- **Firehose subscriptions (Firehose, S3, IAM)** (4): `sns.firehose-raw-subscription-writes-the-bare-message-bodies`, `sns.firehose-subscribe-without-a-subscription-role-rejected`, `sns.firehose-subscription-role-without-put-record-dead-letters-until-granted`, `sns.firehose-subscription-writes-each-notification-envelope-into-the-stream-bucket`.
- **CloudFormation stacks** (8): `sns.deploy-cfn-fifo-topic-delivers-in-order-and-deduplicates-by-content`, `sns.deploy-cfn-kms-encrypted-topic-and-queue-deliver-plaintext`, `sns.deploy-cfn-removing-a-topic-inline-policy-restores-the-default-policy`, `sns.deploy-cfn-stack-update-changes-a-filter-policy-in-place`, `sns.deploy-cfn-subscription-filter-policy-behaves`, `sns.deploy-cfn-subscription-redrive-policy-dead-letters-an-undeliverable-message`, `sns.deploy-cfn-topic-policy-authorizes-only-the-named-role-and-follows-updates`, `sns.deploy-cfn-topic-publish-subscribe-flow`.
- **CloudWatch Logs delivery-status logging (IAM, Logs)** (4): `sns.feedback-role-without-logs-permission-still-delivers-and-writes-no-log-record`, `sns.sqs-failure-feedback-role-logs-the-denied-delivery-while-the-message-never-reaches-the-queue`, `sns.sqs-success-feedback-role-writes-the-delivery-record-to-cloudwatch-logs`, `sns.success-feedback-sample-rate-zero-writes-no-log-while-full-rate-logs-every-delivery`.
- **KMS keys** (2): `sns.encrypted-topic-whose-key-policy-withholds-usage-refuses-publish`, `sns.publish-to-topic-whose-key-is-pending-deletion-fails-kms-invalid-state`.
- **IAM roles and role assumption** (7): `sns.authorization-error-explicit-deny-returns-correct-code`, `sns.set-topic-attributes-cross-account-feedback-role-rejected`, `sns.set-topic-attributes-malformed-feedback-role-arn-rejected`, `sns.topic-feedback-role-arn-and-sample-rate-round-trip`, `sns.topic-policy-endpoint-condition-limits-which-queue-a-role-may-subscribe`, `sns.topic-policy-principal-arn-pattern-admits-only-matching-roles`, `sns.topic-policy-subscribe-only-grant-refuses-the-roles-publish`.

Checks that need SNS to be regional (a topic per region, a regional endpoint refusing another
region's topic ARN) are ported but stay known gaps (`FidelityGap.SnsRegions`): making SNS topics and
subscriptions regional is planned as separate work.

## Known gaps

A check we don't pass yet carries `[KnownFidelityGap(FidelityGap.X, "why")]` and is skipped, so the
suite stays green. To work on a gap, run the skipped tests too:

```bash
# Everything in the suite, known gaps included (they fail until fixed)
LOCALSQSSNS_RUN_KNOWN_GAPS=1 dotnet run --project tests/LocalSqsSnsMessaging.Tests -c Release -- \
  --treenode-filter "/*/LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.*/*/*"

# One service: .Sqs, .Sns or .SnsSqs
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
  in it still reaches its deadline.
- **Budgets and timeouts:** the runner's per-check budgets (`ctx.time_left()`) are in virtual
  time. Each test's `[Timeout]` is real time, and only stops a hung check.

SNS runs on the same clock: deliveries to SQS happen during the Publish, and a FIFO topic's
five-minute deduplication window and its message archive are measured on the bus's clock.

Write new waits with `FidelityTime`, never `Task.Delay` or `Stopwatch`: a real wait doesn't
move the bus's clock, so whatever it waits for never happens.

Checks whose subject is timing (`timing:real-time` upstream) are in the `TimeBased` category.

When a fix makes a skipped test pass, delete its attribute. To find every test for one root cause,
grep for its `FidelityGap` value: `grep -rl "FidelityGap.SnsFifo" .`.
