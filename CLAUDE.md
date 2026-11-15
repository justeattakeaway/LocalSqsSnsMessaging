# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

LocalSqsSnsMessaging is an in-memory emulator of AWS SQS, SNS, EventBridge and DynamoDB (with DynamoDB Streams), for fast tests and local development without LocalStack or other external services. Real AWS SDK clients talk to it through an in-memory `HttpMessageHandler`, so it answers on the wire as AWS does.

It ships three ways, all compiled from the same source in `src/LocalSqsSnsMessaging.Shared/` (each project globs it in):
- `src/LocalSqsSnsMessaging`: the NuGet library, against the v4 AWS SDK (net8.0 and netstandard2.0). Its only dependencies are AWS SDK packages (plus netstandard2.0 polyfills); keep it that way
- `src/LocalSqsSnsMessaging.AWSSDKv3`: the same library against the 3.7.x AWS SDK (`AWS_SDK_V3` defined)
- `src/LocalSqsSnsMessaging.Server`: a standalone ASP.NET Core server with a dashboard (`ASPNETCORE` defined), serving the AWS wire protocols over real HTTP

Build the whole solution (`LocalSqsSnsMessaging.slnx`) after changing shared code: netstandard2.0 has weaker nullable annotations (e.g. `string.IsNullOrEmpty` does not prove non-null), so a change that builds for the test project can still fail there.

Domain terms (send, service delivery, redrive, dead-letter queue, ARN resolution, resource policy) are defined in `GLOSSARY.md`.

## Building and Testing

### Build and Test
```bash
./build.ps1
```

This PowerShell script:
- Installs the correct .NET SDK version (from global.json) if needed
- Builds and packs the library, the AWS SDK v3 library and the server
- Runs tests with coverage (tests/LocalSqsSnsMessaging.Tests)

### Skip Tests
```bash
./build.ps1 -SkipTests
```

### Run Tests Only
```bash
dotnet run --project tests/LocalSqsSnsMessaging.Tests/LocalSqsSnsMessaging.Tests.csproj --configuration Release -- --coverage --coverage-output-format xml --timeout 2m
```

### Run Specific Tests

**IMPORTANT**: TUnit uses `--treenode-filter` (NOT `--filter`) for filtering tests. However, filters can be tricky - the easiest approach is usually to run all tests or use `--list-tests` to find the exact test name first.

```bash
# List all tests (helpful for finding exact test names)
dotnet run --project tests/LocalSqsSnsMessaging.Tests/LocalSqsSnsMessaging.Tests.csproj --configuration Release -- --list-tests

# Run all tests (usually fastest and most reliable)
dotnet run --project tests/LocalSqsSnsMessaging.Tests/LocalSqsSnsMessaging.Tests.csproj --configuration Release -- --timeout 30s

# Filter tests by pattern (use with caution - may not match anything if pattern is wrong)
dotnet run --project tests/LocalSqsSnsMessaging.Tests/LocalSqsSnsMessaging.Tests.csproj --configuration Release -- --treenode-filter "*/PartOfTestName*" --timeout 2m

# Grep for specific test results after running all tests
dotnet run --project tests/LocalSqsSnsMessaging.Tests/LocalSqsSnsMessaging.Tests.csproj --configuration Release -- --timeout 30s 2>&1 | grep "TestName"
```

**Common pitfalls:**
- Using `--filter` instead of `--treenode-filter` (wrong parameter name)
- Tree node filters often don't match - when in doubt, run all tests with `grep`
- Use absolute paths if running from different directories
- Always rebuild after code changes: `dotnet build tests/LocalSqsSnsMessaging.Tests/LocalSqsSnsMessaging.Tests.csproj --configuration Release`

### Verification Tests (Floci)
`tests/LocalSqsSnsMessaging.Tests.Verification` runs the shared test classes against a [Floci](https://github.com/floci/floci) container started through Aspire (needs Docker):
```bash
cd tests/LocalSqsSnsMessaging.Tests.Verification
dotnet run -c Release -- --timeout 2m --no-progress --log-level Warning
```

## Architecture

### Core Components

**InMemoryAwsBus** (`InMemoryAwsBus.cs`)
- Central message bus managing all queues, topics, subscriptions, event buses and move tasks
- Configurable with `TimeProvider`, `CurrentAccountId`, and `CurrentRegion`
- Resources are regional: `AwsRegionResources` holds one region's queues, topics, subscriptions, event buses, archives and replays, and `bus.Region(name)` gets (or creates) it. `bus.Queues` is `CurrentRegion`'s queues (other services go through `bus.Region(...)`). Each internal client (`InternalSqsClient`, `InternalSnsClient`, `InternalEventBridgeClient`) is constructed with the request's region and sees only that region. ARN-addressed deliveries (SNS to SQS, EventBridge targets, dead-letter queues, move tasks) resolve the ARN when they deliver, through `TryGetQueueByArn`/`TryGetTopicByArn`/`TryGetEventBusByArn`: the whole ARN (region, account, name) must match, the lookup is in the region the ARN names (so deliveries can cross regions), and it never creates a region (`TryGetRegion`). `Arn` splits an ARN; service-specific ARN validation stays with each service. Redrive keeps the dead-letter queue's ARN (`SqsQueueResource.DeadLetterTargetArn`), not the queue, so a recreated dead-letter queue receives again. The library HTTP handler and the server both read the region from the SigV4 credential scope, else an AWS endpoint host (`Http/AwsRequestRegion.cs`), else `CurrentRegion`. Move tasks stay bus-wide, keyed by handle
- Thread-safe using `ConcurrentDictionary` for all resource collections

**Internal clients** (`SqsClient/InternalSqsClient*.cs`, `SnsClient/InternalSnsClient*.cs`, `EventBridgeClient/InternalEventBridgeClient*.cs`)
- One per service, taking and returning the generated request/response models in `Models/*.g.cs`. They are internal: the public way in is an AWS SDK client over HTTP
- A queue's messages are `SqsQueueResource`'s alone (see SQS Message Lifecycle); `InternalSqsClient` validates requests, calls the queue and shapes responses

### HTTP Mode

**InMemoryAwsHttpMessageHandler** (`Http/InMemoryAwsHttpMessageHandler.cs`)
- `DelegatingHandler` that intercepts HTTP requests from real AWS SDK clients
- Deserializes requests, routes to the internal clients, serializes responses
- `AwsBridgeMiddleware` (`src/LocalSqsSnsMessaging.Server`) does the same for the server over real HTTP, and `BusRegistry` gives each account (from the SigV4 access key) its own bus

**AWS Protocol Support:**

1. **SQS - JSON Protocol:**
   - Content-Type: `application/x-amz-json-1.0`
   - Request/Response: JSON with camelCase properties
   - Operations identified by `X-Amz-Target` header
   - Example: `{"QueueUrl": "...", "MaxNumberOfMessages": 10}`
   - SQS also serves the AWS Query protocol (it is `awsQueryCompatible`): form-encoded `Action=...&Version=2012-11-05`, XML responses in the `http://queue.amazonaws.com/doc/2012-11-05/` namespace, flattened lists/maps (`Attribute.1.Name`, `MessageAttribute.1.Value.DataType`). Both protocols share queue state. `SqsWireProtocol` holds the routing rules (Content-Type picks the protocol; a JSON POST without a known target, or a target with another Content-Type, is refused as `UnknownOperationException`; a body over 2 MiB once decoded gets HTTP 413) for both the in-memory handler and the server. The server tells SQS Query from SNS Query by `Version` (or the SigV4 scope).

2. **SNS - Query Protocol:**
   - Content-Type: `application/x-www-form-urlencoded`
   - Request: Form-encoded query parameters (e.g., `Action=Publish&TopicArn=...`)
   - Response: XML format
   - Nested structures use indexed notation: `MessageAttributes.entry.1.Name=foo&MessageAttributes.entry.1.Value.DataType=String`

3. **EventBridge - JSON Protocol:** `application/x-amz-json-1.1`, operations named by an `X-Amz-Target` of `AWSEvents.<Operation>`

4. **DynamoDB and DynamoDB Streams - JSON Protocol:** `application/x-amz-json-1.0`, targets `DynamoDB_20120810.<Operation>` and `DynamoDBStreams_20120810.<Operation>`. `Http/DynamoDbWireProtocol.cs` serves both, transport-neutral, for the library handler and the server: it parses the body, runs the generated dispatcher, writes errors (`__type` in `com.amazon.coral.validate` for `ValidationException`, `com.amazon.coral.service` for protocol errors, `com.amazonaws.dynamodb.v20120810` otherwise, plus modelled members such as `CancellationReasons`) and the `x-amz-crc32` header SDKs check

**Operation Handlers** (`Http/Handlers/Generated/`)
- Generated per service: a `switch` on the operation name that deserializes the request, calls the internal client and serializes the response or error (see Code Generation)

**Public entry points**
- `InMemoryAwsBusHttpExtensions.cs`: `CreateSqsClient()`, `CreateSnsClient()`, `CreateEventBridgeClient()`, `CreateDynamoDbClient()` return real `AmazonSQSClient` / `AmazonSimpleNotificationServiceClient` / `AmazonEventBridgeClient` / `AmazonDynamoDBClient` instances wired to the bus
- `InMemoryAwsHttpClientFactory`: an AWS SDK `HttpClientFactory` for clients configured elsewhere (e.g. dependency injection)
- Tests may construct an internal client directly (`new InternalEventBridgeClient(bus)`); the server's dashboard does too. Such a client bypasses the HTTP layer and has no `Caller`, so queue policies never apply to it

**Resource Models**
- `SqsQueueResource`: Queue state including messages, in-flight messages, message groups for FIFO
- `SnsTopicResource`: a topic's attributes and tags, its subscriptions and its fan-out (see SNS Delivery)
- `SnsSubscription`: Links its `Topic` to an `sqs` queue or an `http`/`https` endpoint, and holds the subscription's `FilterPolicy`/`FilterPolicyScope`, `RedrivePolicy` (dead-letter queue) and `DeliveryPolicy`
- `EventBusResource`, `RuleResource`, `ArchiveResource`, `ReplayResource` (`EventBridgeClient/`)

### SNS Requests

`InternalSnsClient` is split into partial files under `SnsClient/` (topics, subscriptions, publish, permissions and tags, unsupported operations). Requests are answered as SNS does:
- ARNs are checked by `SnsArn` before any lookup (malformed: `InvalidParameter`); a missing topic is `NotFound` "Topic does not exist", a missing subscription "Subscription does not exist"
- A subscription ARN is its topic ARN plus a uuid. Subscribe needs the topic, but not the SQS queue, to exist; subscribing the same endpoint again returns the existing subscription
- Subscription changes go through the topic: `Subscribe`, `Confirm`, `UpdateSubscription` (SetSubscriptionAttributes), `Unsubscribe` and `Delete` (DeleteTopic). The topic keeps its own set (found by protocol and endpoint), writes the region's `Subscriptions` index (subscription ARN to subscription, for ARN lookups and ListSubscriptions) under its gate, and enforces the `MaximumMessageSize` rules that depend on its subscriptions. Nothing else writes the index
- `SnsTopicAttributes` holds topic attribute names, defaults (default `Policy`, `EffectiveDeliveryPolicy`, ...) and validation; `SnsPublishValidation` and `SnsMessageStructure` validate Publish/PublishBatch and pick a `MessageStructure=json` branch per protocol
- SNS is regional: a topic ARN naming another region is refused with `InvalidParameter` "Invalid parameter: TopicArn", as a regional endpoint does

### SNS Delivery

A topic publishes through `topic.Publish`/`PublishBatch` (also EventBridge's SNS targets). Its fan-out, `SnsFanOut` (split into `SnsFanOut*.cs`), is built once per topic and delivers to the topic's current `FanOutSubscriptions`, a snapshot of its confirmed subscriptions the topic replaces as a whole on every change, so a publish sees a subscription whole or not at all. Lock order lives in `SnsTopicResource`: its `SubscriptionGate` (subscriptions, attributes, tags), then a FIFO topic's `SnsFifoTopicState.Gate`; only the topic and its fan-out take the FIFO gate. Per subscription the fan-out:
- `SnsFilterPolicy` validates filter policies with AWS's messages (`.Syntax.cs` for SNS's JSON errors, the shared `EventRulerJsonReader`/`EventRulerGrammar` for Event Ruler's operator rules) and evaluates them (attribute or body scope) on top of `JsonPatternMatcher`, the same matcher EventBridge uses for event patterns
- FIFO topics (`SnsFanOut.Fifo.cs`, `SnsFifoTopicState`) assign sequence numbers and deduplicate for five minutes on the bus `TimeProvider`; an `ArchivePolicy` archives messages, and a subscription's `ReplayPolicy` (`SnsReplayPolicy`) replays them. The topic replays whenever a subscription change leaves a replay `Pending` (subscribe, confirm, a new `ReplayPolicy`), joining the fan-out and replaying under the FIFO gate so no live message overtakes the replayed ones
- SQS endpoints go through `SqsServiceDelivery` (see below) at publish time; a deleted queue, or a queue `Policy` that denies SNS (`sns.amazonaws.com`, with the topic as `aws:SourceArn`) or addresses SNS deliveries without admitting this one, is a delivery failure; a policy that says nothing about SNS admits it
- Non-raw deliveries get the envelope `SnsEnvelope` writes, byte for byte as SNS lays it out and signed (`SnsSigningCertificate`: one self-signed key per process, served at the envelope's `SigningCertURL`; netstandard2.0 keeps `EXAMPLE` placeholders). Raw deliveries convert attributes (`SnsRawMessageAttributes`)
- HTTP/S subscriptions start `PendingConfirmation` and are excluded from fan-out until `ConfirmSubscription` (or a GET on the `SubscribeURL`) presents the token to the topic (`topic.Confirm`)
- HTTP/S endpoints are POSTed in the background by `SnsHttpDelivery` using `bus.HttpClient`, retried per `SnsDeliveryPolicy` with `TimeProvider`-driven delays
- Failed deliveries go to the subscription's dead-letter queue when a `RedrivePolicy` is set

### EventBridge

`InternalEventBridgeClient` (`EventBridgeClient/`) is split into partial files by area: event buses, rules, targets, events (PutEvents and TestEventPattern), tags, permissions, and archives and replays. EventBridge is regional: an event's `region` is its bus's region, and an event forwarded to a bus in another region keeps its origin `region` but gets a new `id`.
- Requests are validated as EventBridge validates them, with its codes and messages: PutEvents entries fail in place (`EventBridgePutEventsValidation`), PutTargets refuses the whole request (`EventBridgeTargetValidation`, and `EventBridgeTargetInput.Validate` for Input/InputPath/InputTransformer), schedule expressions are checked by `EventBridgeScheduleExpression`, and list operations page with `EventBridgePage`
- Event patterns are validated and matched as Event Ruler does (`EventBridgeEventPattern`): `EventRulerJsonReader` and `EventRulerGrammar` (shared with SNS filter policies) give Jackson-style refusals, and `JsonPatternMatcher` matches on the event's flattened values
- `EventBridgeEnvelope` builds each event's envelope; `EventBridgeEventDelivery` routes it through a bus's enabled rules and delivers it, during PutEvents, to each matching rule's targets: SQS queues (FIFO through the FIFO send path, deduplicated by event id), SNS topics (through the topic's publish action) and event buses in the same region and account (forwarded once). `EventBridgeTargetInput` renders what each target receives
- A failed delivery (missing resource, a queue or topic `Policy` that denies `events.amazonaws.com` or admits it only for other rules, input that is not JSON) goes to the target's `DeadLetterConfig` queue with EventBridge's `ERROR_CODE`/`ERROR_MESSAGE`/`RULE_ARN`/`TARGET_ARN` attributes. A policy that says nothing about EventBridge admits it. These failures are not retried, as on AWS
- A bus `Policy` (PutPermission/RemovePermission) is enforced for explicit Deny statements on PutEvents (see Resource Policies)
- Archives (`ArchiveResource`) record events through a managed rule on their source bus; replays (`ReplayResource`) and rules with a `ScheduleExpression` (`ScheduledRuleJob`, `EventBridgeSchedule`) run on the bus `TimeProvider`
- Timestamps go on the wire as epoch seconds (`Http/Serialization/AwsJsonTimestamp.cs`)

### DynamoDB

`DynamoDbClient/` holds DynamoDB, in namespace `LocalSqsSnsMessaging.DynamoDb` (models in `.DynamoDb.Model`). Each region's tables, streams, backups and transaction tokens are in one `DynamoDbStore` (`AwsRegionResources.DynamoDb`) behind a single lock (`Gate`), because a transaction spans tables; every operation holds it.
- **Values:** `DynamoValue` is an immutable attribute value; requests are read straight into it (`DynamoValueJson`; an `AttributeValue` with no type, or two, is refused there). A value is kept as sent until `DynamoValueRules` canonicalizes it where DynamoDB checks it: `DynamoNumber` (BigInteger mantissa and point, 38 digits, exponents -130 to 125, one canonical text per number), set rules, the 32-level and 400 KB item limits. Refusals are *enveloped* (`1 validation error detected: ...`) or bare depending on where the value came from, as DynamoDB words them
- **Storage:** `DynamoTable` keeps items in a dictionary plus a `SortedSet<DynamoKey>` (`DynamoKeyOrder`: strings by UTF-8 bytes, binaries unsigned, numbers numerically); each `DynamoIndex` is a sorted set of `DynamoIndexEntry` updated on every write, so sparse indexes need no extra code. Every write returns a `DynamoItemChange`, which feeds streams (`DynamoStream`), `ReturnValues` and the point-in-time log
- **Expressions** (`Expressions/`): one tokenizer and recursive-descent parser for condition, filter, projection and update expressions (`ExpressionParser`, AST in `Conditions.cs` and `Updates.cs`); key conditions have their own grammar (`KeyCondition`). `ExpressionAttributes.CheckAllReferenced` checks undefined and unused `#names`/`:values` before any expression is parsed. Legacy parameters (`Expected`, `KeyConditions`, `QueryFilter`, `ScanFilter`, `AttributeUpdates`, `AttributesToGet`) translate into the same AST (`LegacyConditions`, `UpdateProgram.FromAttributeUpdates`)
- **PartiQL** (`PartiQL/PartiQLParser.cs`): SELECT/INSERT/UPDATE/DELETE, lowered to the same conditions and `UpdateProgram`, so a WHERE clause evaluates as a `ConditionExpression` would
- **`InternalDynamoDbClient`** is split by area: tables, items, reads (Query/Scan: `Limit` before the filter, 1 MB pages, parallel scan segments), batches and transactions, PartiQL, time to live, backups, and `Unsupported` (features of other services: answered as `UnknownOperationException` by the wire protocol). `DynamoDbRequestChecks` holds the request-shape refusals DynamoDB words differently from its model's constraints; the generated dispatcher calls it. `InternalDynamoDbStreamsClient` serves DynamoDB Streams over the same store
- **Resource policies:** a table and its stream each keep a policy (`DynamoResourcePolicy`: text and revision, set by Put/DeleteResourcePolicy or CreateTable). `InternalDynamoDbClient.Authorize` (`.Authorization.cs`) enforces a table policy's explicit Deny statements through `ResourcePolicy` (`ResourcePolicyKind.Table`), right after the table lookup, so a missing table is still `ResourceNotFoundException`. A denial is `AccessDeniedException` (HTTP 400). Every request acts as the bus account's root user (as EventBridge's PutEvents does) and passes no `dynamodb:` condition keys, so statements conditioned on them are skipped. It covers the item operations, Query and Scan (against `{table}/index/{name}` when `IndexName` is given), and the table-level operations (DescribeTable, UpdateTable, DeleteTable, the time to live, tag, continuous backup, contributor insights operations, CreateBackup, DescribeKinesisStreamingDestination, DescribeTableReplicaAutoScaling). Not yet: batches, transactions, PartiQL, the resource-policy operations themselves, and stream policies
- **Time:** time to live sweeps run on the bus `TimeProvider` (`DynamoTimeToLiveJob`, every `DynamoDbTimeToLiveSweepInterval`); tables are ACTIVE immediately unless `DynamoDbTableCreationDelay` is set. Provisioned throughput is reported, never enforced
- **SDK v4 packages:** DynamoDB Streams is a separate package in v4 (`AWSSDK.DynamoDBStreams`) that the library doesn't reference; `InMemoryAwsHttpClientFactory` recognises its config by type name
- **Fidelity:** the measure is AWS's emulator evaluation suite (`behavioral/dynamodb`, `behavioral/dynamodbstreams`, 902 checks). 853 are ported to C# in `AwsEvaluationSuite/DynamoDb*` and run in memory with the other tests; to run the originals with boto3 against the server, clone [aws-emulator-evaluation-suite](https://github.com/aws/aws-emulator-evaluation-suite), `uv sync --locked --extra checks`, start the server, then `uv run fidelity-tests-runner run behavioral/dynamodb/check_<name>.py --endpoint http://127.0.0.1:5050 --auth off`. The CC0 Rust emulator in [aws-emulator](https://github.com/aws/aws-emulator/tree/main/services/service-com-amazonaws-dynamodb-dynamodb-20120810) is a reference for exact messages

### Time Control

All time-dependent operations use `TimeProvider` from the bus:
- Message delays (`SendMessageRequest.DelaySeconds`)
- Visibility timeouts (`ReceiveMessageRequest.VisibilityTimeout`)
- In-flight message expiration
- Move task scheduling
- EventBridge replays and scheduled rules
- DynamoDB time to live, table creation delays, transaction token windows and stream iterator expiry

This allows tests to use `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` to control time progression.

### SQS Message Lifecycle

`SqsQueueResource` owns every message on a queue (the stages are in GLOSSARY.md), split into partial files: `SqsQueueResource.cs` (settings, sends, delays), `.Receive.cs`, `.Lifecycle.cs`. Its collections are private; callers use:
- `AcceptSent` / `AcceptRedriven` in (see SQS Callers below), `ReceiveAsync` out: one call for standard and FIFO queues, long polling, redrive at `maxReceiveCount`, and FIFO `ReceiveRequestAttemptId` replays. It returns copies with every attribute; the client trims them to the request
- `DeleteMessage` / `ChangeVisibility` by receipt handle, returning a `SqsReceiptHandleOutcome` that the client maps to SQS's error (`SqsReceiptHandle` mints and parses handles)
- `Purge`, `RemoveMessage` (the dashboard's delete), `MoveMessages` (move tasks), `CountMessages` (sweeps expired messages first) and `Peek` (the dashboard's view)

Inside, a standard queue's visible messages sit in `SqsMessageStore`, which hands each new message to one parked receive; a FIFO queue keeps one `FifoGroup` per message group, guarded by locking the group. Every way out of flight goes through `LeaveFlight` (timer stopped, FIFO group slot released under the group's lock), and every way out of the queue through `Forget` (receipt handle retired). Keep any new exit on those two paths; `SqsQueueLifecycleTests` checks each exit at the queue's interface. A receive starts its messages' visibility timers only as it returns, and nothing reads the clock while holding a group lock: a `FakeTimeProvider` with `AutoAdvanceAmount` runs due timers from inside `GetUtcNow`.

`SqsMoveTask` runs a move task on a bus `TimeProvider` timer, once a second: it resolves its source and destination ARNs on each tick (a destination gone at a tick fails it), and a tick and `TryCancel` take the task's lock, so it settles on exactly one of COMPLETED, FAILED or CANCELLED.

### FIFO Queue Handling

FIFO queues (.fifo suffix) have special handling:
- Messages within a message group are processed sequentially: a group with a message in flight delivers nothing further (a locked message group)
- Deduplication IDs prevent duplicate messages within 5-minute windows
- Each message group has its own queue (`FifoGroup`, private to `SqsQueueResource`)

### Resource Policies

A queue's, topic's or event bus's `Policy`, or a DynamoDB table's policy, is a **resource policy** (`ResourcePolicies/`). `ResourcePolicy` owns the document and its evaluation; callers never edit the JSON themselves:
- `ResourcePolicy.Parse(kind, json)` (`null` for no policy), `Empty`/`ParseOrEmpty` for the document a service writes from scratch. `Statement` may be one object or a list; every operation reads both, and an edit writes a list
- Edits by statement id return a new policy: `HasStatement`, `StatementCount`, `WithStatement` (adds, or replaces the same `Sid`), `WithoutStatement`. Each service keeps its own errors (duplicate label, over the limit, missing label) and decides what an emptied policy becomes (SQS removes it, SNS keeps `[]`, EventBridge clears it). Statements are built by the service, because each writes its values differently (SQS a single value as a string, SNS always lists)
- `Normalize` (SQS stores a bare account-id principal as its root ARN) and `AreEquivalent` (CreateQueue/CreateTopic on an existing name)
- Evaluation: `DeniesExplicitly` and `GrantsToEveryone` for a caller, `EvaluateServiceDelivery` for a service principal (admitted / explicitly denied / not allowed). Only explicit Deny statements are enforced for callers; a statement the library cannot evaluate is skipped
- `ResourcePolicyKind` (`Queue`, `Topic`, `EventBus`, `Table`) holds what differs: the action prefix (`sqs`, `sns`, `events`, `dynamodb`), the empty document (SNS: `2008-10-17`, `__default_policy_ID`; SQS: `<arn>/SQSDefaultPolicy`), and SQS's storage and comparison rules
- Policies apply in `InternalSqsClient.Authorize`, `SqsServiceDelivery`, EventBridge's delivery to topics (`EventBridgeEventDelivery`), PutEvents (`AuthorizePutEvents`) and `InternalDynamoDbClient.Authorize` (see DynamoDB). `AwsAccountId.IsValid` is the one twelve-digit check

### SQS Callers

The generated SQS handlers build `InternalSqsClient` with a `Caller` (`SqsCaller`: the SigV4 access key, source IP, TLS). A client built without one (tests, the dashboard) is never subject to queue policies.
- `SenderId` comes from `SqsCaller.SenderId` (signed: `bus.CurrentUserId`, or the account id for an account-id access key; unsigned: the account id)
- `InternalSqsClient.Authorization.cs`: `Authorize(queue, action)` runs after each queue lookup and enforces explicit Deny statements only (`ResourcePolicy`); `EnsureKmsKeyExists` refuses sends to a queue whose KMS alias is not in `bus.KmsKeyAliases` (only when that is set; by default every key exists)
- Receipt handles carry the queue's `Incarnation`, so a handle from a deleted queue is refused by a same-name successor
- A **service delivery** (SNS subscription, EventBridge target, and both services' dead-letter queues) goes through `SqsServiceDelivery.Deliver`: it resolves the queue by ARN, evaluates the queue policy for the service principal (`ResourcePolicy.EvaluateServiceDelivery`, a verdict of admitted / explicitly denied / not allowed), and returns an `Outcome`. It sets no `SenderId` (AWS documents none) and skips `SendMessage`'s KMS and size checks
- Only the queue enqueues: `SqsQueueResource.AcceptSent` (sends and service deliveries: stamps `SentTimestamp`, applies delay, FIFO group and deduplication scope) and `AcceptRedriven` (into a dead-letter queue or back by a move task: no deduplication claim, no delay, FIFO retention restarts)

### Exception Handling in HTTP Mode

Exceptions thrown by the internal clients must include the `ErrorCode` property for the AWS SDK to deserialize them correctly.

The library's internal exception types (in `Models/Exceptions.cs`) are deliberately prefixed with `Internal` (e.g. `InternalResourceNotFoundException`) so they do **not** collide with the identically-purposed AWS SDK types. `ErrorCode` is the AWS wire error code the SDK maps back to its own exception type — it is set explicitly and is independent of the internal class name:

```csharp
// ✅ Correct - SDK will recognize this as Amazon's ResourceNotFoundException
throw new InternalResourceNotFoundException("Queue not found")
{
    ErrorCode = "ResourceNotFoundException",  // AWS wire error code (not the C# class name)
    StatusCode = HttpStatusCode.BadRequest
};

// ❌ Wrong - SDK receives generic AmazonSQSException
throw new InternalResourceNotFoundException("Queue not found");
```

**Error Response Format:**
- SQS (JSON): `{"__type": "com.amazonaws.sqs#{ErrorCode}", "message": "..."}`
- SNS (Query/XML): Standard AWS Query protocol error format

The generated handlers format `ErrorCode` into each protocol's error response. An exception that is not an `AwsServiceException` (e.g. `NotSupportedException` from an unsupported operation) escapes to a generic fallback (`CreateErrorResponse` in the handler, `WriteErrorResponseAsync` in the server) that always answers in JSON.

## Code Generation

`tools/GenerateHandlers` reads the AWS service models in `src/LocalSqsSnsMessaging.Shared/ServiceModels/*.normal.json` and writes:
- `Models/{Sqs,Sns,EventBridge}Models.g.cs`: the request/response models (`InternalTypeGenerator.cs`)
- `Http/Handlers/Generated/{Sqs,Sns,EventBridge}OperationHandler.g.cs`: the library handlers, and `*.HttpContext.g.cs` twins of each for the server (`Program.cs`)
- `SqsJsonSerializers.g.cs`, `EventBridgeJsonSerializers.g.cs` (`JsonCodeGenerator.cs`) and `SnsQuerySerializers.g.cs` (`QueryCodeGenerator.cs`)
- `SqsQuerySerializers.g.cs` / `SqsQueryOperationHandler.g.cs`: the SQS Query protocol serializers and its one transport-neutral dispatcher (`SqsQueryCodeGenerator.cs`). The SQS model is the JSON one and has lost the Query wire names, so the generator restores them from its `WireNames` table
- DynamoDB and DynamoDB Streams use `ShapeJsonCodeGenerator.cs`, which writes a method per shape (`Read_X`/`Write_X`/`Validate_X`) so recursive and nested shapes work: `Models/DynamoDb{,Streams}Models.g.cs` (collections null when absent, so `"L": []` stays distinct from a missing list; Java-style `ToString` for validation messages), `DynamoDb{,Streams}JsonSerializers.g.cs` (including the model's constraint checks, worded as AWS's request validation words them) and `DynamoDb{,Streams}OperationDispatcher.g.cs`. `AttributeValue` maps to the hand-written `DynamoValue`

**Regenerate** (the argument is the shared project directory):
```bash
dotnet run --project tools/GenerateHandlers/GenerateHandlers.csproj -- src/LocalSqsSnsMessaging.Shared
```

**Important**:
- When AWS SDK request formats don't match expectations, check the generated deserializers first. The AWS SDK may use different parameter names than documented (e.g., `MessageAttributes.entry.1.Name` vs `MessageAttributes.entry.1.key`).
- After modifying the code generator, regenerate before testing; edit the generator, never a `.g.cs` file.

## Testing Framework

This project uses **TUnit** (not xUnit or NUnit) for testing:
- Test projects have `OutputType=Exe` and are run via `dotnet run`
- Coverage enabled with `Microsoft.Testing.Extensions.CodeCoverage`
- Assertions use Shouldly library

### Test Organization

- `tests/LocalSqsSnsMessaging.Tests.Shared/`: abstract test classes (e.g. `SqsFifoTests`), compiled into two projects:
  - `LocalSqsSnsMessaging.Tests/LocalAwsMessaging/` subclasses run them in memory through `CreateSqsClient()` etc.
  - `LocalSqsSnsMessaging.Tests.Verification/ExternalVerification/` subclasses run them against Floci
- `LocalSqsSnsMessaging.Tests/AwsEvaluationSuite/`: C# ports of AWS's emulator evaluation suite (SQS, SNS, EventBridge, DynamoDB and DynamoDB Streams), checks verified against real AWS (see its `README.md`); `KnownFidelityGap` marks the ones not passed yet. DynamoDB ports share `DynamoDbFidelity.cs` (boto3-style values, Python `repr`/`==`, waiters, raw requests)
- `LocalSqsSnsMessaging.Tests/DynamoDb/`: DynamoDB through the low-level client, `DynamoDBContext`, the Document Model and the Streams client
- `LocalSqsSnsMessaging.Tests/` (other folders): focused regression and behaviour tests, mostly through the SDK clients; a few reach internals (`InternalsVisibleTo`)
- `tests/LocalSqsSnsMessaging.Tests.Server/`: the server, through real SDK clients against Kestrel, and its dashboard API

## Package Management

Uses Central Package Management (Directory.Packages.props):
- All package versions defined centrally
- Test projects automatically get TUnit and coverage tools via `IsTestProject=true`
