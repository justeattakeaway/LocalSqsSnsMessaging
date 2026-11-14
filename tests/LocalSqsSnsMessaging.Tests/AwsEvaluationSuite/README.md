# AWS emulator evaluation suite (SQS, SNS and EventBridge)

These are C# ports of the SQS, SNS and EventBridge checks in AWS's
[emulator evaluation suite](https://github.com/aws/aws-emulator-evaluation-suite/tree/4df2823bafface2ada35f482c07ff6668cc078e1),
pinned at commit `4df2823`. Each upstream `check_<name>.py` was verified against real AWS. Each one
becomes one test file here, with the same steps, waits and assertion messages, so the two can be
compared side by side:

| Upstream | Here | Display name |
|---|---|---|
| [`behavioral/sqs/check_<name>.py`](https://github.com/aws/aws-emulator-evaluation-suite/tree/4df2823bafface2ada35f482c07ff6668cc078e1/behavioral/sqs) | `Sqs/<Name>.cs` | `sqs.<id>` |
| [`behavioral/sns/check_<name>.py`](https://github.com/aws/aws-emulator-evaluation-suite/tree/4df2823bafface2ada35f482c07ff6668cc078e1/behavioral/sns) | `Sns/<Name>.cs` | `sns.<id>` |
| [`behavioral/sns__sqs/check_<name>.py`](https://github.com/aws/aws-emulator-evaluation-suite/tree/4df2823bafface2ada35f482c07ff6668cc078e1/behavioral/sns__sqs) | `SnsSqs/<Name>.cs` | `sns__sqs.<id>` |
| [`behavioral/events/check_<name>.py`](https://github.com/aws/aws-emulator-evaluation-suite/tree/4df2823bafface2ada35f482c07ff6668cc078e1/behavioral/events) | `Events/<Name>.cs` | `events.<id>` |
| [`behavioral/events__sqs/check_<name>.py`](https://github.com/aws/aws-emulator-evaluation-suite/tree/4df2823bafface2ada35f482c07ff6668cc078e1/behavioral/events__sqs) | `EventsSqs/<Name>.cs` | `events__sqs.<id>` |

- **Same verdicts:** each test gets the same verdict the Python check got when it was run with boto3
  against `LocalSqsSnsMessaging.Server`.
- **Real AWS clients:** tests use real `AmazonSQSClient`s, `AmazonSimpleNotificationServiceClient`s and
  `AmazonEventBridgeClient`s over the in-memory HTTP handler, through `FidelityContext`, the C# version of the Python `ctx`.
- **Exactly the check's requests:** the test run's SDK tracing adds trace context to every request
  (a `traceparent` message attribute on Publish, an `AWSTraceHeader` on SendMessage), which boto3
  doesn't, so `FidelityContext` turns that off for the whole check.
- **Isolation:** every test gets its own `InMemoryAwsBus`, on its own virtual clock. The bus turns on
  the library's two strict modes, `IsolateRegions` and `KmsKeyAliases`, so it serves each region's
  queues separately and refuses unknown KMS aliases, as AWS does.
- **Left out:** checks that also need IAM, KMS, CloudFormation, CloudWatch, Lambda or STS role
  assumption are not ported, nor SNS checks about protocols other than SQS, nor EventBridge checks
  about targets and features the library doesn't emulate (see below). A check that only reads the
  account id from `sts:GetCallerIdentity` is ported, with `ctx.Account` (and `ctx.CallerArn`, the
  account's root user) in its place.

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

## EventBridge scope

The EventBridge ports cover EventBridge as a message router in front of SQS and SNS: event buses,
rules, event patterns and TestEventPattern, PutEvents and its validation and partial failures,
targets to SQS (standard and FIFO), SNS and other event buses, Input, InputPath and
InputTransformer, target dead-letter queues and retry policies, bus resource policies, tags,
archives and replays, scheduled rules, listing and pagination, and error codes: 186 of the 296
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

These 110 checks are not ported:

- **EventBridge v2** (`eventbridgev2`, `eventbridgev2__iam__sqs`, `eventbridgev2__iam__sns__sqs`): a newer, separate API with buses addressed by ARN, subscribers, event sources, JSONata transformers and `PutRawEvents`, not the `events` API this library emulates (40): `eventbridgev2.create-event-bus-name-collision-different-token-rejected`, `eventbridgev2.create-event-bus-same-client-token-is-idempotent`, `eventbridgev2.create-subscriber-client-token-replay-idempotent-and-mismatch-rejected`, `eventbridgev2.delete-event-bus-blocked-by-event-source-until-revoked`, `eventbridgev2.delete-event-bus-with-subscriber-rejected-until-removed`, `eventbridgev2.event-bus-create-describe-list-delete-lifecycle`, `eventbridgev2.event-bus-tags-do-not-propagate-to-subscribers-or-event-sources`, `eventbridgev2.event-source-lifecycle-against-active-bus`, `eventbridgev2.point-in-time-starting-point-outside-allowed-range-rejected`, `eventbridgev2.put-events-partial-failure-reports-per-entry-error`, `eventbridgev2.recreated-event-bus-same-name-gets-new-identifier`, `eventbridgev2.resource-policy-conflicting-revision-rejected`, `eventbridgev2.resource-policy-put-get-list-round-trip`, `eventbridgev2.revoke-resource-marks-subscriber-terminal-and-outlives-bus-deletion`, `eventbridgev2.subscriber-name-collision-on-same-bus-rejected-but-allowed-on-another`, `eventbridgev2.subscriber-put-events-delivers-to-sqs`, `eventbridgev2.tag-resource-and-list-tags-round-trip`, `eventbridgev2__iam__sns__sqs.role-without-sns-publish-dead-letters-and-topic-receives-nothing`, `eventbridgev2__iam__sns__sqs.sns-topic-target-publishes-one-event-per-message`, `eventbridgev2__iam__sqs.dead-letter-record-names-access-denied-when-role-cannot-reach-target`, `eventbridgev2__iam__sqs.detail-pattern-matches-put-events-envelope-not-raw-payload`, `eventbridgev2__iam__sqs.event-bus-target-forwards-as-new-event-with-evaluated-metadata`, `eventbridgev2__iam__sqs.fifo-subscriber-delivers-event-group-in-publish-order`, `eventbridgev2__iam__sqs.horizon-subscriber-replays-retained-events-marked-replay`, `eventbridgev2__iam__sqs.jsonata-transformer-reads-octet-stream-data-as-base64`, `eventbridgev2__iam__sqs.jsonata-transformer-string-unquoted-and-object-serialized`, `eventbridgev2__iam__sqs.put-events-content-based-deduplication-suppresses-resend`, `eventbridgev2__iam__sqs.put-events-envelope-carries-resources-time-and-region`, `eventbridgev2__iam__sqs.put-events-explicit-deduplication-id-suppresses-repeat`, `eventbridgev2__iam__sqs.put-events-reserved-aws-source-refused-and-never-delivered`, `eventbridgev2__iam__sqs.put-raw-events-invalid-json-and-open-format-refused-per-entry`, `eventbridgev2__iam__sqs.raw-transformer-delivers-payload-as-sent-and-bytes-as-base64`, `eventbridgev2__iam__sqs.sequence-numbers-increase-with-ingestion-and-match-delivery`, `eventbridgev2__iam__sqs.sqs-message-attributes-evaluate-jsonata-and-keep-binary-literal`, `eventbridgev2__iam__sqs.subscriber-filters-combine-with-and-across-scopes`, `eventbridgev2__iam__sqs.subscriber-on-failure-configuration-writes-dead-letter-record-to-sqs`, `eventbridgev2__iam__sqs.system-metadata-content-type-filter-routes-by-content-type`, `eventbridgev2__iam__sqs.update-subscriber-replaces-filter-configuration-whole`, `eventbridgev2__iam__sqs.update-subscriber-transformer-applies-to-later-events`, `eventbridgev2__iam__sqs.with-metadata-transformer-delivers-full-event-with-string-system-metadata`.
- **IAM role and identity-policy enforcement**: each assumes an IAM role (`sts:AssumeRole`) and grades what that role's policies allow (8): `events.bus-policy-role-statement-admits-put-events-but-account-statement-leaves-bare-role-denied`, `events.identity-explicit-deny-overrides-a-bus-policy-allow-naming-the-same-role`, `events.identity-policy-events-source-condition-denies-put-events-for-any-other-source`, `events.policy-scoped-to-one-bus-rules-denies-put-targets-on-the-same-rule-name-on-another-bus`, `events.put-targets-with-role-arn-denied-without-iam-pass-role-until-granted`, `events.resource-tag-condition-scopes-disable-rule-to-rules-carrying-the-tag`, `events__iam__sns__sqs__sts.put-targets-target-arn-condition-restricts-to-sqs`, `events__iam__sts.put-rule-source-conditions-gate-the-event-pattern`.
- **API destinations and connections** (HTTP targets, some behind Lambda function URLs) (22): `events.api-destination-4xx-dead-letters-without-retry-while-5xx-is-retried-until-its-retry-attempts-are-used-up`, `events.api-destination-api-key-connection-adds-its-key-and-parameters-and-its-values-win-over-the-targets`, `events.api-destination-basic-connection-sends-the-basic-authorization-header-with-the-destinations-method-and-the-bare-envelope`, `events.api-destination-invocation-rate-limit-maximum-is-enforced`, `events.api-destination-lifecycle`, `events.api-destination-oauth-connection-fetches-a-client-credentials-token-and-sends-it-as-the-bearer-header`, `events.api-destination-rate-limit-defers-a-burst-it-cannot-absorb-and-still-delivers-every-event`, `events.api-destination-state-follows-connection-deauthorize-and-reauthorize`, `events.create-api-destination-accepts-non-url-endpoint`, `events.create-api-destination-invalid-endpoint-pattern-rejected`, `events.create-api-destination-requires-existing-connection`, `events.create-connection-missing-auth-parameters-rejected`, `events.create-connection-state-transition`, `events.deauthorize-connection-state-transition`, `events.delete-connection-eventually-not-found`, `events.delete-connection-in-use-by-api-destination-allowed`, `events.describe-connection-redacts-secret-auth-material`, `events.list-connections-filters-are-mutually-exclusive`, `events.oauth-connection-non-https-authorization-endpoint-rejected`, `events.oauth-connection-with-unreachable-endpoint-deauthorizes-and-its-destination-is-inactive`, `events.update-connection-authorization-type`, `events__sqs.api-destination-timeout-retries-are-bounded-by-maximum-retry-attempts`.
- **Global endpoints** (Route 53 health checks, failover and replication) (12): `events.concurrent-update-endpoint-conflict-detected`, `events.create-endpoint-duplicate-name-blocked`, `events.create-endpoint-malformed-routing-config-rejected`, `events.delete-endpoint-nonexistent-not-found`, `events.describe-endpoint-nonexistent-not-found`, `events.endpoint-failover-routing-config-managed`, `events.endpoint-lifecycle-create-active-delete-404`, `events.endpoint-routing-config-round-trip`, `events.put-events-endpoint-id-delivers-to-primary-and-replicates-to-secondary`, `events.put-events-endpoint-id-with-unrelated-event-bus-name-rejected`, `events.update-endpoint-changes-routing-config`, `events.update-endpoint-nonexistent-not-found`.
- **Partner event sources** (SaaS partner accounts) (12): `events.activate-deactivate-event-source-state-transition`, `events.activate-event-source-nonexistent-not-found`, `events.concurrent-activate-event-source-conflict-detected`, `events.deactivate-event-source-nonexistent-not-found`, `events.delete-partner-event-source-disabled-operation-blocked`, `events.describe-event-source-nonexistent-not-found`, `events.describe-partner-event-source-nonexistent-not-found`, `events.e2e-partner-event-source-activation-to-ingestion-flow`, `events.list-partner-event-source-accounts-unknown-source-not-found`, `events.list-partner-event-sources-disabled-account-denied`, `events.partner-event-source-lifecycle-create-rejected-non-partner`, `events.put-partner-events-non-partner-account-denied`.
- **Lambda targets** (3): `events.lambda-target-function-error-is-retried-by-lambda-and-never-dead-lettered-by-the-rule`, `events.lambda-target-is-invoked-asynchronously-and-its-on-success-destination-records-the-envelope`, `events__sqs.throttled-lambda-target-is-one-successful-attempt-with-no-retry-and-no-dlq`.
- **Step Functions targets** (1): `events.express-and-standard-state-machine-targets-start-with-the-envelope-as-input-named-after-the-event-id`.
- **Kinesis targets** (1): `events.kinesis-partition-key-path-renders-numbers-and-objects-as-json-and-an-absent-field-as-empty-object`.
- **CloudWatch Logs targets** (1): `events.log-group-target-dead-letters-events-timed-too-old-or-too-far-ahead-while-a-current-event-is-written`.
- **KMS keys** (customer-managed keys on buses, queues and topics) (5): `events.cmk-encrypted-bus-delivers-put-events-to-sqs-target`, `events.cmk-without-events-grant-rejected-on-create-and-update`, `events.disabled-bus-key-stops-delivery-dead-letters-encrypted-events-until-reenabled`, `events.sns-target-on-a-kms-encrypted-topic-dead-letters-as-no-permissions-until-the-key-policy-grants-events`, `events.sqs-target-encrypted-with-a-key-events-cannot-use-dead-letters-until-the-key-policy-grants-it`.
- **CloudFormation stacks** (5): `events.deploy-cfn-bus-target-with-a-stack-created-execution-role-forwards-events-to-the-destination-bus`, `events.deploy-cfn-event-bus-policy-admits-the-named-role-and-a-stack-update-removing-it-denies-again`, `events.deploy-cfn-schedule-rule-fires-into-its-target-and-a-stack-update-disabling-it-stops-firing`, `events.deploy-cfn-target-dead-letter-config-captures-undeliverable-events-until-a-stack-update-adds-the-queue-policy`, `events.deploy-eventbus-rule-target-cfn-then-put-events-delivers`.

Six EventBridge ports stay known gaps:

- **No grant for EventBridge** (4, `FidelityGap.EventBridgeUngrantedTargets`): a queue or topic
  whose policy says nothing about EventBridge still receives its deliveries, as SNS deliveries do,
  because most test setups add no policy. A policy that denies EventBridge, or admits it only for
  other rules, is enforced. Real AWS answers a queue with no policy as `NO_PERMISSIONS` in one check
  and as `NO_RESOURCE` in another.
- **Regions** (1, `FidelityGap.EventBridgeRegions`): a cross-region event bus target. EventBridge is
  regional only in a planned follow-up, together with SNS.
- **The runner's budget** (1, `FidelityGap.EmulatorBudget`):
  `events__sqs.events-archive-replay-rule-matching-replay-name-exists-false-is-not-redelivered`
  spends the runner's 60-second budget for an emulator in its own settle loops and drains, before
  it reaches the archive, so it fails against any emulator.

One port passes although its Python check failed against the server:
`events__sqs.custom-bus-fanout-is-at-least-once-and-order-independent`. Its own waits add up to
about 57 seconds, and the server's real request latency pushed it past the runner's 60-second budget.
On virtual time it finishes inside the budget, and every assertion holds.

## Known gaps

A check we don't pass yet carries `[KnownFidelityGap(FidelityGap.X, "why")]` and is skipped, so the
suite stays green. To work on a gap, run the skipped tests too:

```bash
# Everything in the suite, known gaps included (they fail until fixed)
LOCALSQSSNS_RUN_KNOWN_GAPS=1 dotnet run --project tests/LocalSqsSnsMessaging.Tests -c Release -- \
  --treenode-filter "/*/LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.*/*/*"

# One service: .Sqs, .Sns, .SnsSqs, .Events or .EventsSqs
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
scheduled rules.

Write new waits with `FidelityTime`, never `Task.Delay` or `Stopwatch`: a real wait doesn't
move the bus's clock, so whatever it waits for never happens.

Checks whose subject is timing (`timing:real-time` upstream) are in the `TimeBased` category.

When a fix makes a skipped test pass, delete its attribute. To find every test for one root cause,
grep for its `FidelityGap` value: `grep -rl "FidelityGap.SnsFifo" .`.
