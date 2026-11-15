# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

LocalSqsSnsMessaging is an in-memory emulator of AWS SQS, SNS and EventBridge, for fast tests and local development without LocalStack or other external services. Real AWS SDK clients talk to it through an in-memory `HttpMessageHandler`, so it answers on the wire as AWS does.

It ships three ways, all compiled from the same source in `src/LocalSqsSnsMessaging.Shared/` (each project globs it in):
- `src/LocalSqsSnsMessaging`: the NuGet library, against the v4 AWS SDK (net8.0 and netstandard2.0)
- `src/LocalSqsSnsMessaging.AWSSDKv3`: the same library against the 3.7.x AWS SDK (`AWS_SDK_V3` defined)
- `src/LocalSqsSnsMessaging.Server`: a standalone ASP.NET Core server with a dashboard (`ASPNETCORE` defined), serving the AWS wire protocols over real HTTP

Build the whole solution (`LocalSqsSnsMessaging.slnx`) after changing shared code: netstandard2.0 has weaker nullable annotations (e.g. `string.IsNullOrEmpty` does not prove non-null), so a change that builds for the test project can still fail there.

Domain terms (send, service delivery, redrive, dead-letter queue, ARN resolution) are defined in `GLOSSARY.md`.

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
- A queue's messages live in `SqsMessageStore` (standard) or per-group queues (FIFO), owned by `SqsQueueResource`

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

**Operation Handlers** (`Http/Handlers/Generated/`)
- Generated per service: a `switch` on the operation name that deserializes the request, calls the internal client and serializes the response or error (see Code Generation)

**Public entry points**
- `InMemoryAwsBusHttpExtensions.cs`: `CreateSqsClient()`, `CreateSnsClient()`, `CreateEventBridgeClient()` return real `AmazonSQSClient` / `AmazonSimpleNotificationServiceClient` / `AmazonEventBridgeClient` instances wired to the bus
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
- A bus `Policy` (PutPermission/RemovePermission) is enforced for explicit Deny statements on PutEvents, through `SqsQueuePolicy`
- Archives (`ArchiveResource`) record events through a managed rule on their source bus; replays (`ReplayResource`) and rules with a `ScheduleExpression` (`ScheduledRuleJob`, `EventBridgeSchedule`) run on the bus `TimeProvider`
- Timestamps go on the wire as epoch seconds (`Http/Serialization/AwsJsonTimestamp.cs`)

### Time Control

All time-dependent operations use `TimeProvider` from the bus:
- Message delays (`SendMessageRequest.DelaySeconds`)
- Visibility timeouts (`ReceiveMessageRequest.VisibilityTimeout`)
- In-flight message expiration
- Move task scheduling
- EventBridge replays and scheduled rules

This allows tests to use `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` to control time progression.

### Background Jobs

**SqsInflightMessageExpirationJob** (`SqsInflightMessageExpirationJob.cs`)
- Returns in-flight messages to queue when visibility timeout expires
- Uses `TimeProvider` for scheduling

**SqsMoveTaskJob** (`SqsMoveTaskJob.cs`)
- Moves messages from source queue to destination (e.g., DLQ to main queue)
- Tracks progress via `SqsMoveTask`

### FIFO Queue Handling

FIFO queues (.fifo suffix) have special handling:
- Messages within a message group are processed sequentially
- Deduplication IDs prevent duplicate messages within 5-minute windows
- Message group locks ensure ordering (`SqsQueueResource.MessageGroupLocks`)
- Each message group has its own queue (`SqsQueueResource.MessageGroups`)

### SQS Callers and Queue Policies

The generated SQS handlers build `InternalSqsClient` with a `Caller` (`SqsCaller`: the SigV4 access key, source IP, TLS). A client built without one (tests, the dashboard) is never subject to policies.
- `SenderId` comes from `SqsCaller.SenderId` (signed: `bus.CurrentUserId`, or the account id for an account-id access key; unsigned: the account id)
- `InternalSqsClient.Authorization.cs`: `Authorize(queue, action)` runs after each queue lookup and enforces explicit Deny statements only (`SqsQueuePolicy`); `EnsureKmsKeyExists` refuses sends to a queue whose KMS alias is not in `bus.KmsKeyAliases` (only when that is set; by default every key exists)
- Receipt handles carry the queue's `Incarnation`, so a handle from a deleted queue is refused by a same-name successor
- A **service delivery** (SNS subscription, EventBridge target, and both services' dead-letter queues) goes through `SqsServiceDelivery.Deliver`: it resolves the queue by ARN, evaluates the queue policy for the service principal (`SqsQueuePolicy.EvaluateServiceDelivery`, a verdict of admitted / explicitly denied / not allowed), and returns an `Outcome`. It sets no `SenderId` (AWS documents none) and skips `SendMessage`'s KMS and size checks
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
- `LocalSqsSnsMessaging.Tests/AwsEvaluationSuite/`: C# ports of AWS's emulator evaluation suite, checks verified against real AWS (see its `README.md`); `KnownFidelityGap` marks the ones not passed yet
- `LocalSqsSnsMessaging.Tests/` (other folders): focused regression and behaviour tests, mostly through the SDK clients; a few reach internals (`InternalsVisibleTo`)
- `tests/LocalSqsSnsMessaging.Tests.Server/`: the server, through real SDK clients against Kestrel, and its dashboard API

## Package Management

Uses Central Package Management (Directory.Packages.props):
- All package versions defined centrally
- Test projects automatically get TUnit and coverage tools via `IsTestProject=true`
