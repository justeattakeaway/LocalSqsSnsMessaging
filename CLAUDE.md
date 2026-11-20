# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

InMemoryAws is an in-memory emulator of AWS SQS, SNS, EventBridge, DynamoDB (with DynamoDB Streams) and S3, for fast tests and local development without LocalStack or other external services. Real AWS SDK clients talk to it through an in-memory `HttpMessageHandler`, so it answers on the wire as AWS does.

It ships two ways, both compiled from the same source in `src/InMemoryAws.Shared/` (each project globs it in):
- `src/InMemoryAws`: the NuGet library (net8.0 and netstandard2.0). It depends on no AWS SDK package (net8.0 has no dependencies at all; netstandard2.0 only BCL backports); keep it that way. Consumers bring AWS SDK v3.7 or v4 themselves, and the code that touches the SDK is plain C# in `src/InMemoryAws/build/sources/`, which the package's `build/InMemoryAws.targets` adds to the *consumer's* compilation per AWS SDK assembly it references (see Public entry points)
- `src/InMemoryAws.Server`: a standalone ASP.NET Core server with a dashboard (`ASPNETCORE` defined), serving the AWS wire protocols over real HTTP

Build the whole solution (`InMemoryAws.slnx`) after changing shared code: netstandard2.0 has weaker nullable annotations (e.g. `string.IsNullOrEmpty` does not prove non-null), so a change that builds for the test project can still fail there.

Domain terms (send, service delivery, redrive, dead-letter queue, ARN resolution, resource policy) are defined in `GLOSSARY.md`.

## Building and Testing

### Build and Test
```bash
./build.ps1
```

This PowerShell script:
- Installs the correct .NET SDK version (from global.json) if needed
- Builds and packs the library and the server
- Runs tests with coverage (tests/InMemoryAws.Tests)

### Skip Tests
```bash
./build.ps1 -SkipTests
```

### Run Tests Only
```bash
dotnet run --project tests/InMemoryAws.Tests/InMemoryAws.Tests.csproj --configuration Release -- --coverage --coverage-output-format xml --timeout 2m
```

### Run Specific Tests

**IMPORTANT**: TUnit uses `--treenode-filter` (NOT `--filter`) for filtering tests. However, filters can be tricky - the easiest approach is usually to run all tests or use `--list-tests` to find the exact test name first.

```bash
# List all tests (helpful for finding exact test names)
dotnet run --project tests/InMemoryAws.Tests/InMemoryAws.Tests.csproj --configuration Release -- --list-tests

# Run all tests (usually fastest and most reliable)
dotnet run --project tests/InMemoryAws.Tests/InMemoryAws.Tests.csproj --configuration Release -- --timeout 30s

# Filter tests by pattern (use with caution - may not match anything if pattern is wrong)
dotnet run --project tests/InMemoryAws.Tests/InMemoryAws.Tests.csproj --configuration Release -- --treenode-filter "*/PartOfTestName*" --timeout 2m

# Grep for specific test results after running all tests
dotnet run --project tests/InMemoryAws.Tests/InMemoryAws.Tests.csproj --configuration Release -- --timeout 30s 2>&1 | grep "TestName"
```

**Common pitfalls:**
- Using `--filter` instead of `--treenode-filter` (wrong parameter name)
- Tree node filters often don't match - when in doubt, run all tests with `grep`
- Use absolute paths if running from different directories
- Always rebuild after code changes: `dotnet build tests/InMemoryAws.Tests/InMemoryAws.Tests.csproj --configuration Release`

### Verification Tests (Floci)
`tests/InMemoryAws.Tests.Verification` runs the shared test classes against a [Floci](https://github.com/floci/floci) container started through Aspire (needs Docker):
```bash
cd tests/InMemoryAws.Tests.Verification
dotnet run -c Release -- --timeout 2m --no-progress --log-level Warning
```

### Benchmarks
`benchmarks/InMemoryAws.Benchmarks` sends one SDK request per wire protocol through the library and through the server's middleware (on an in-memory `TestServer`), measuring allocations; `VectorBenchmarks` measures what a DynamoDB vector index adds to a write, and a search. CI never runs it. Run it on the commit before and after a change to a wire protocol or a host:
```bash
dotnet run -c Release --project benchmarks/InMemoryAws.Benchmarks -- --filter '*'
```
Allocated bytes are exact and comparable between runs; times are noisy on a shared machine, so run the two commits back to back.

## Architecture

### Core Components

**InMemoryAwsCloud** (`InMemoryAwsCloud.cs`)
- The root object, the **cloud**: one simulated AWS account across every region, holding all queues, topics, subscriptions, event buses, DynamoDB tables, S3 buckets and move tasks, and the settings they run on. Code calls it `cloud` (`_cloud` as a field); it was `InMemoryAwsBus` up to v1
- Configurable with `TimeProvider`, `CurrentAccountId`, and `CurrentRegion`
- Resources are regional: `AwsRegionResources` holds one region's queues, topics, subscriptions, event buses, archives and replays, and `cloud.Region(name)` gets (or creates) it. `cloud.Queues` is `CurrentRegion`'s queues (other services go through `cloud.Region(...)`). Each internal client (`InternalSqsClient`, `InternalSnsClient`, `InternalEventBridgeClient`) is constructed with the request's region and sees only that region. ARN-addressed deliveries (SNS to SQS, EventBridge targets, dead-letter queues, move tasks) resolve the ARN when they deliver, through `TryGetQueueByArn`/`TryGetTopicByArn`/`TryGetEventBusByArn`: the whole ARN (region, account, name) must match, the lookup is in the region the ARN names (so deliveries can cross regions), and it never creates a region (`TryGetRegion`). `Arn` splits an ARN; service-specific ARN validation stays with each service. Redrive keeps the dead-letter queue's ARN (`SqsQueueResource.DeadLetterTargetArn`), not the queue, so a recreated dead-letter queue receives again. The library HTTP handler and the server both read the region from the SigV4 credential scope, else an AWS endpoint host (`Http/AwsRequestRegion.cs`), else `CurrentRegion`. Move tasks stay cloud-wide, keyed by handle
- Thread-safe using `ConcurrentDictionary` for all resource collections

**Internal clients** (`SqsClient/InternalSqsClient*.cs`, `SnsClient/InternalSnsClient*.cs`, `EventBridgeClient/InternalEventBridgeClient*.cs`)
- One per service, taking and returning the generated request/response models in `Models/*.g.cs`. They are internal: the public way in is an AWS SDK client over HTTP
- A queue's messages are `SqsQueueResource`'s alone (see SQS Message Lifecycle); `InternalSqsClient` validates requests, calls the queue and shapes responses

### HTTP Mode

Each service has one **wire protocol** module (see GLOSSARY.md) in `Http/`, which both hosts call: `SqsWireProtocol` (`HandleJsonAsync`, and `HandleQueryAsync` in `SqsWireProtocol.Query.cs`), `SnsWireProtocol`, `EventBridgeWireProtocol`, `DynamoDbWireProtocol` (DynamoDB and DynamoDB Streams) and `S3WireProtocol` (which has its own request and response types, see S3).
- In: the operation (or the Query parameters), the body as memory, the client the host built for the request's region and SQS caller, and a pooled `ArrayBufferWriter<byte>` the host owns
- Out: the response body, written into that buffer, and a `WireResponse` (a struct: status, Content-Type, request id, SQS's `x-amzn-query-error`, DynamoDB's `x-amz-crc32`)
- It answers every failure in its own protocol (unknown operation, malformed body, `AwsServiceException`, anything else as an internal failure), resetting the buffer first, and records the operation's activity (`WireTracing`); only cancellation escapes
- The generator writes only each protocol's operation switch (see Code Generation)

The two hosts are adapters: each reads the body, decides which service and operation a request is for, builds the client, and copies the `WireResponse` onto its own response type.
- `InMemoryAwsHttpMessageHandler` (`Http/`): the library's `DelegatingHandler`, one per service (`AwsServiceType`), reading the body once into a pooled `PooledRequestBody`
- `AwsBridgeMiddleware` (`src/InMemoryAws.Server`): the server, which tells services apart by target prefix, Query `Version` or SigV4 scope; `CloudRegistry` gives each account (from the SigV4 access key) its own cloud

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
   - `Action` names the operation (in the body, or a GET's query string); a missing one is `MissingAction`, an unknown one `InvalidAction`

3. **EventBridge - JSON Protocol:** `application/x-amz-json-1.1`, operations named by an `X-Amz-Target` of `AWSEvents.<Operation>`

4. **DynamoDB and DynamoDB Streams - JSON Protocol:** `application/x-amz-json-1.0`, targets `DynamoDB_20120810.<Operation>` and `DynamoDBStreams_20120810.<Operation>`. `Http/DynamoDbWireProtocol.cs` serves both, building the client the target names: it parses the body, runs the generated dispatcher, writes errors (`__type` in `com.amazon.coral.validate` for `ValidationException`, `com.amazon.coral.service` for protocol errors, `com.amazonaws.dynamodb.v20120810` otherwise, plus modelled members such as `CancellationReasons`) and the `x-amz-crc32` header SDKs check

5. **S3 - REST-XML Protocol:** the method, the path and the query string's markers (`?tagging`, `?uploads`, `?list-type=2`) name the operation, and a request's members come from the path (bucket and key), query parameters, headers (`x-amz-meta-*` as a map) and the body (XML, or an object's bytes). The bucket comes from a virtual-hosted host (`bucket.s3.region.amazonaws.com`, any host with an `s3` label after the first, and S3 Express's zonal `s3express-{az}` hosts) or else the first path segment. `S3WireProtocol.HandleAsync` takes an `S3WireRequest` (built by each host from the raw path and query, so keys keep characters `Uri` would normalize) and returns an `S3WireResponse` (status, ordered headers, XML or an object's bytes; a HEAD keeps `Content-Length` and sends no body). It checks signatures (`S3RequestAuthentication`), decodes `aws-chunked` uploads (trailers become headers), routes CORS preflights and POST Object uploads, then runs the generated router and dispatcher, and answers errors as S3's `<Error>` document (status alone for HEAD), with `x-amz-request-id`/`x-amz-id-2`. The server sends a request to S3 when it is signed for `s3`, sent to an `s3` host, or claimed by no other protocol

**Public entry points**
- `src/InMemoryAws/build/sources/` (compiled into the consumer, not the library, so the library needs no AWS SDK): `InMemoryAws.AwsSdk.cs` (when the consumer references `AWSSDK.Core`) holds `InMemoryAwsSdkHttpClientFactory`, an AWS SDK `Amazon.Runtime.HttpClientFactory` (not .NET's `IHttpClientFactory`) that picks a handler by the client config's `RegionEndpointServiceName` (`AwsServiceEndpoints`), and `CreateAwsSdkHttpClientFactory()`; one file per service package adds `CreateSqsClient()`, `CreateSnsClient()`, `CreateEventBridgeClient()`, `CreateDynamoDbClient()`, `CreateDynamoDbStreamsClient()` and `CreateS3Client()` (path-style addressing) (`.DynamoDbStreams.V3.cs` when `AWSSDK.DynamoDBv2` is v3, which holds the Streams client), returning real `AmazonSQSClient` / `AmazonSimpleNotificationServiceClient` / `AmazonEventBridgeClient` / `AmazonDynamoDBClient` / `AmazonS3Client` instances wired to the cloud. The files must compile as C# 7.3 against AWS SDK v3.7 and v4, under any consumer settings: keep the `<auto-generated>` header and `#pragma warning disable`, `global::` usings, no nullable annotations. On .NET Framework the AWS SDK sends with `HttpWebRequest` and has no `HttpClientFactory`, so `.targets` adds `InMemoryAws.AwsSdk.NetFramework.cs` in place of `InMemoryAws.AwsSdk.cs`: it registers an `IRuntimePipelineCustomizer` with the SDK's `RuntimePipelineCustomizerRegistry` that, for clients built from a config `Create...Client()` registered, replaces the pipeline's `HttpHandler<Stream>` with one whose `IHttpRequest<Stream>` buffers the request and sends it to the handler (an error status becomes the `HttpErrorResponseException` the SDK's error handlers expect); `.NetFramework.V4.cs` adds the members AWSSDK.Core v4 added to those interfaces. `.targets` adds nothing when `InMemoryAwsSdkSources` is `false`. The repo's test and benchmark projects import the targets, so they use the files as a consumer does; `BringYourOwnSdkTests` runs them against AWS SDK v3 on .NET and .NET Framework (`tests/InMemoryAws.Tests.AwsSdkV3`, net10.0 and net48) and v4 on .NET Framework (`tests/InMemoryAws.Tests.NetFramework`, net48); `build.ps1` runs the net48 ones on Windows only
- `InMemoryAwsHttpMessageHandler` and `AwsServiceEndpoints` (in the library): the SDK-free pieces, for wiring a client by hand (README: "Without the build integration")
- Tests may construct an internal client directly (`new InternalEventBridgeClient(cloud)`); the server's dashboard does too. Such a client bypasses the HTTP layer and has no `Caller`, so queue policies never apply to it

**Resource Models**
- `SqsQueueResource`: Queue state including messages, in-flight messages, message groups for FIFO
- `SnsTopicResource`: a topic's attributes and tags, its subscriptions and its fan-out (see SNS Delivery)
- `SnsSubscription`: Links its `Topic` to an `sqs` queue or an `http`/`https` endpoint, and holds the subscription's `FilterPolicy`/`FilterPolicyScope`, `RedrivePolicy` (dead-letter queue) and `DeliveryPolicy`
- `EventBusResource`, `RuleResource`, `ArchiveResource`, `ReplayResource` (`EventBridgeClient/`)

### SNS Requests

`InternalSnsClient` is split into partial files under `SnsClient/` (topics, subscriptions, publish, permissions and tags, authorization, mobile push, SMS, unsupported operations). Requests are answered as SNS does:
- ARNs are checked by `SnsArn` before any lookup (malformed: `InvalidParameter`); a missing topic is `NotFound` "Topic does not exist", a missing subscription "Subscription does not exist"
- A subscription ARN is its topic ARN plus a uuid. Subscribe needs the topic, but not the SQS queue, to exist; subscribing the same endpoint again returns the existing subscription
- Subscription changes go through the topic: `Subscribe`, `Confirm`, `UpdateSubscription` (SetSubscriptionAttributes), `Unsubscribe` and `Delete` (DeleteTopic). The topic keeps its own set (found by protocol and endpoint), writes the region's `Subscriptions` index (subscription ARN to subscription, for ARN lookups and ListSubscriptions) under its gate, and enforces the `MaximumMessageSize` rules that depend on its subscriptions. Nothing else writes the index
- `SnsTopicAttributes` holds topic attribute names, defaults (default `Policy`, `EffectiveDeliveryPolicy`, ...) and validation; `SnsPublishValidation` and `SnsMessageStructure` validate Publish/PublishBatch and pick a `MessageStructure=json` branch per protocol
- SNS is regional: a topic ARN naming another region is refused with `InvalidParameter` "Invalid parameter: TopicArn", as a regional endpoint does
- A topic's `Policy` is enforced for callers by `InternalSnsClient.Authorize` (`.Authorization.cs`) after the topic lookup of Subscribe (with `sns:Protocol`/`sns:Endpoint`), Publish and PublishBatch (`sns:Publish`), Get/SetTopicAttributes, Add/RemovePermission and ListSubscriptionsByTopic: explicit Deny statements only, every request acting as the cloud account's root user; a denial is `AuthorizationError` (HTTP 403)
- Subscribe takes `sqs`, `http`/`https` and `email`/`email-json` (not on a FIFO topic: "Invalid protocol type"). Email subscriptions stay `PendingConfirmation` (the cloud sends no email; `ConfirmSubscription` with the token still confirms one), and a pending subscription can't be unsubscribed. Other protocols are `NotSupportedException`
- Mobile push (`.MobilePush.cs`): platform applications (`SnsPlatformApplication`, per region in `AwsRegionResources.PlatformApplications`) and their endpoints are kept and answered for, and `EventEndpointCreated`/`Deleted`/`Updated` topics are notified, but no push service is contacted: credentials and tokens are never checked, and a publish never reaches an endpoint. SMS (`.Sms.cs`, `SnsSmsAccount`): account attributes, opted-out numbers and the sandbox's numbers; no SMS is sent, so a sandbox number's one-time password is never revealed and the account stays in the sandbox with no origination numbers

### SNS Delivery

A topic publishes through `topic.Publish`/`PublishBatch` (also EventBridge's SNS targets). Its fan-out, `SnsFanOut` (split into `SnsFanOut*.cs`), is built once per topic and delivers to the topic's current `FanOutSubscriptions`, a snapshot of its confirmed subscriptions the topic replaces as a whole on every change, so a publish sees a subscription whole or not at all. Lock order lives in `SnsTopicResource`: its `SubscriptionGate` (subscriptions, attributes, tags), then a FIFO topic's `SnsFifoTopicState.Gate`; only the topic and its fan-out take the FIFO gate. Per subscription the fan-out:
- `SnsFilterPolicy` validates filter policies with AWS's messages (`.Syntax.cs` for SNS's JSON errors, the shared `EventRulerJsonReader`/`EventRulerGrammar` for Event Ruler's operator rules) and evaluates them (attribute or body scope) on top of `JsonPatternMatcher`, the same matcher EventBridge uses for event patterns
- FIFO topics (`SnsFanOut.Fifo.cs`, `SnsFifoTopicState`) assign sequence numbers and deduplicate for five minutes on the cloud `TimeProvider`; an `ArchivePolicy` archives messages, and a subscription's `ReplayPolicy` (`SnsReplayPolicy`) replays them. The topic replays whenever a subscription change leaves a replay `Pending` (subscribe, confirm, a new `ReplayPolicy`), joining the fan-out and replaying under the FIFO gate so no live message overtakes the replayed ones
- SQS endpoints go through `SqsServiceDelivery` (see below) at publish time; a deleted queue, or a queue `Policy` that denies SNS (`sns.amazonaws.com`, with the topic as `aws:SourceArn`) or addresses SNS deliveries without admitting this one, is a delivery failure; a policy that says nothing about SNS admits it
- Non-raw deliveries get the envelope `SnsEnvelope` writes, byte for byte as SNS lays it out and signed (`SnsSigningCertificate`: one self-signed key per process, served at the envelope's `SigningCertURL`; netstandard2.0 keeps `EXAMPLE` placeholders). Raw deliveries convert attributes (`SnsRawMessageAttributes`)
- HTTP/S and email subscriptions start `PendingConfirmation` and are excluded from fan-out until `ConfirmSubscription` (or, for HTTP/S, a GET on the `SubscribeURL`) presents the token to the topic (`topic.Confirm`). A confirmed email subscription is delivered nothing
- HTTP/S endpoints are POSTed in the background by `SnsHttpDelivery` using `cloud.HttpClient`, retried per `SnsDeliveryPolicy` with `TimeProvider`-driven delays
- Failed deliveries go to the subscription's dead-letter queue when a `RedrivePolicy` is set

### EventBridge

`InternalEventBridgeClient` (`EventBridgeClient/`) is split into partial files by area: event buses, rules, targets, events (PutEvents and TestEventPattern), tags, permissions, archives and replays, partner event sources, global endpoints, and connections and API destinations. EventBridge is regional: an event's `region` is its bus's region, and an event forwarded to a bus in another region keeps its origin `region` but gets a new `id`.
- Requests are validated as EventBridge validates them, with its codes and messages: PutEvents entries fail in place (`EventBridgePutEventsValidation`), PutTargets refuses the whole request (`EventBridgeTargetValidation`, and `EventBridgeTargetInput.Validate` for Input/InputPath/InputTransformer), schedule expressions are checked by `EventBridgeScheduleExpression`, and list operations page with `EventBridgePage`
- Event patterns are validated and matched as Event Ruler does (`EventBridgeEventPattern`): `EventRulerJsonReader` and `EventRulerGrammar` (shared with SNS filter policies) give Jackson-style refusals, and `JsonPatternMatcher` matches on the event's flattened values
- `EventBridgeEnvelope` builds each event's envelope; `EventBridgeEventDelivery` routes it through a bus's enabled rules and delivers it, during PutEvents, to each matching rule's targets: SQS queues (FIFO through the FIFO send path, deduplicated by event id), SNS topics (through the topic's publish action) and event buses in the same region and account (forwarded once); API destinations are called in the background (see below). `EventBridgeTargetInput` renders what each target receives
- A failed delivery (missing resource, a queue or topic `Policy` that denies `events.amazonaws.com` or admits it only for other rules, input that is not JSON) goes to the target's `DeadLetterConfig` queue with EventBridge's `ERROR_CODE`/`ERROR_MESSAGE`/`RULE_ARN`/`TARGET_ARN` attributes. A policy that says nothing about EventBridge admits it. These failures are not retried, as on AWS
- Connections and API destinations (`ConnectionResource`, `ApiDestinationResource`, `InternalEventBridgeClient.Connections.cs`): `API_KEY` and `BASIC` connections are `AUTHORIZED` at once; an OAuth connection is `AUTHORIZING` until its client-credentials token request (`EventBridgeHttp.AuthorizeAsync`) answers, then `AUTHORIZED` or `DEAUTHORIZED`. Describe never answers a secret. A destination is `ACTIVE` unless its connection exists and is not `AUTHORIZED`. Delivery (`EventBridgeEventDelivery.ApiDestinations.cs`) calls `cloud.HttpClient` in the background, with the connection's auth header and merged HTTP parameters, waits for a slot under the destination's rate limit, gives a call up after 5 s on the cloud `TimeProvider`, retries 401/407/409/429/5xx, timeouts and connection failures with backoff per the target's `RetryPolicy`, and dead-letters the rest (`EXHAUSTED_RETRY_CONDITION`, `RETRY_ATTEMPTS` once retries ran out)
- A bus `Policy` (PutPermission/RemovePermission) is enforced for explicit Deny statements on PutEvents (see Resource Policies)
- Archives (`ArchiveResource`) record events through a managed rule on their source bus; replays (`ReplayResource`) and rules with a `ScheduleExpression` (`ScheduledRuleJob`, `EventBridgeSchedule`) run on the cloud `TimeProvider`
- Partner event sources (`InternalEventBridgeClient.PartnerEventSources.cs`): the cloud's account is never a SaaS partner, so the partner-side operations are refused for the source's namespace (`ValidationException` "Caller is not whitelisted ..."), PutPartnerEvents fails each entry, and the customer side finds no source
- Global endpoints (`EndpointResource`, `InternalEventBridgeClient.Endpoints.cs`) are kept in their home region and are `ACTIVE` at once. Route 53 is not emulated, so the health check ARN is stored but never evaluated: PutEvents with `EndpointId` (found in any region) delivers to the endpoint's bus in its home region, and with replication enabled also to its bus in the failover region, both with the home `region` and the endpoint's ARN in `resources`
- Timestamps go on the wire as epoch seconds (`Http/Serialization/AwsJsonTimestamp.cs`)

### DynamoDB

`DynamoDbClient/` holds DynamoDB, in namespace `InMemoryAws.DynamoDb` (models in `.DynamoDb.Model`). Each region's tables, streams, backups and transaction tokens are in one `DynamoDbStore` (`AwsRegionResources.DynamoDb`) behind a lock (`Gate`) every region of the cloud shares, because a transaction spans tables and a global table's write spans regions; every operation holds it.
- **Values:** `DynamoValue` is an immutable attribute value; requests are read straight into it (`DynamoValueJson`; an `AttributeValue` with no type, or two, is refused there). A value is kept as sent until `DynamoValueRules` canonicalizes it where DynamoDB checks it: `DynamoNumber` (BigInteger mantissa and point, 38 digits, exponents -130 to 125, one canonical text per number), set rules, the 32-level and 400 KB item limits. Refusals are *enveloped* (`1 validation error detected: ...`) or bare depending on where the value came from, as DynamoDB words them
- **Storage:** `DynamoTable` keeps items in a dictionary plus a `SortedSet<DynamoKey>` (`DynamoKeyOrder`: strings by UTF-8 bytes, binaries unsigned, numbers numerically); each `DynamoIndex` is a sorted set of `DynamoIndexEntry` updated on every write, so sparse indexes need no extra code. A global index may be keyed on up to four HASH and four RANGE attributes (`DynamoKeySchema.Hash`/`Range`); a role with several is held as one composite list value, which `DynamoKeyOrder` compares element by element, so `DynamoKey`, the sorted sets and paging work unchanged (`ToAttributes` spreads it back out). `KeyCondition.Resolve` wants equality on every HASH attribute and on each RANGE attribute before the last one compared; PartiQL refuses such indexes outright Every write returns a `DynamoItemChange`, which feeds streams (`DynamoStream`), `ReturnValues` and the point-in-time log
- **Expressions** (`Expressions/`): one tokenizer and recursive-descent parser for condition, filter, projection and update expressions (`ExpressionParser`, AST in `Conditions.cs` and `Updates.cs`); key conditions have their own grammar (`KeyCondition`). `ExpressionAttributes.CheckAllReferenced` checks undefined and unused `#names`/`:values` before any expression is parsed. Legacy parameters (`Expected`, `KeyConditions`, `QueryFilter`, `ScanFilter`, `AttributeUpdates`, `AttributesToGet`) translate into the same AST (`LegacyConditions`, `UpdateProgram.FromAttributeUpdates`)
- **PartiQL** (`PartiQL/PartiQLParser.cs`): SELECT/INSERT/UPDATE/DELETE, lowered to the same conditions and `UpdateProgram`, so a WHERE clause evaluates as a `ConditionExpression` would
- **Vector search:** `DynamoVectorIndex` (beside `DynamoIndex` on `DynamoTable`, maintained by the same `Put`/`Remove`) keeps each item's vector by table key, read as 32-bit floats on the first search (a write only checks its numbers fit, so an unsearched index costs writes little); `InternalDynamoDbClient.VectorSearch.cs` defines indexes (CreateTable, UpdateTable), checks every write's vector attribute against the index's dimensions (`CheckIndexKeyTypes`), meters `VectorWriteRequestBytes` per index whose entry changed, and serves `SearchVectors`: an exact scan of the index, scored in float32, `SearchConditionExpression` (equality on `SearchSchema` attributes, joined by AND) applied before `TopK`. Capacity bills a vector at its name plus 4 bytes a dimension (`DynamoTable.BilledSize`), not its digits; the 400 KB limit still counts the stored text, and a vector's numbers are kept as written. An index UpdateTable adds stays `CREATING` (allocating, then backfilling) for `DynamoDbVectorIndexCreationDelay` (zero by default); only an `ACTIVE` index can be searched. A deleted index is removed at once, and UpdateTable's answer describes it `DELETING`
- **`InternalDynamoDbClient`** is split by area: tables, items, reads (Query/Scan: `Limit` before the filter, 1 MB pages, parallel scan segments), batches and transactions, PartiQL, time to live, backups, vector search, global tables (version 2019.11.21 through UpdateTable's `ReplicaUpdates`, `GlobalTableWitnessUpdates` and `MultiRegionConsistency`; the withdrawn 2017.11.29 operations answer as AWS now answers them, and don't see 2019.11.21 tables), and `Unsupported` (features of other services: answered as `UnknownOperationException` by the wire protocol). `DynamoDbRequestChecks` holds the request-shape refusals DynamoDB words differently from its model's constraints; the generated dispatcher calls it. `InternalDynamoDbStreamsClient` serves DynamoDB Streams over the same store
- **Global tables:** a replica is a same-named table in another region of the cloud. `DynamoReplicationGroup` (`DynamoTable.Replication`, shared by every member) holds the regional tables and any witness regions. `Changed` (and time to live) hands each write to `Replicate`, which applies it to every other replica and its stream at once, so replication has no lag and multi-Region strong consistency holds trivially. A replica's status is its regional table's, so it is `CREATING`/`DELETING` for `DynamoDbTransitionDelay`, and the other members are `UPDATING` meanwhile. `FindTable` settles the whole group, removing gone replicas from their regions and dissolving a group left with one table. DescribeTable lists the other regions as `Replicas`. The settings DynamoDB keeps the same in every region reach every replica: a new replica copies the table's, and UpdateTable's index, capacity and stream changes, and UpdateTimeToLive, apply to all of them (each replica runs its own time to live sweep). The region a replica was added from can't delete its table for 24 hours
- **Resource policies:** a table and its stream each keep a policy (`DynamoResourcePolicy`: text and revision, set by Put/DeleteResourcePolicy or CreateTable). `InternalDynamoDbClient.Authorize` (`.Authorization.cs`) enforces a table policy's explicit Deny statements through `ResourcePolicy` (`ResourcePolicyKind.Table`), right after the table lookup, so a missing table is still `ResourceNotFoundException`. A denial is `AccessDeniedException` (HTTP 400). Every request acts as the cloud account's root user (as EventBridge's PutEvents does) and passes no `dynamodb:` condition keys, so statements conditioned on them are skipped. It covers the item operations, Query, Scan and SearchVectors (against `{table}/index/{name}` when `IndexName` is given), and the table-level operations (DescribeTable, UpdateTable, DeleteTable, the time to live, tag, continuous backup, contributor insights operations, CreateBackup, DescribeKinesisStreamingDestination, DescribeTableReplicaAutoScaling). Not yet: batches, transactions, PartiQL, the resource-policy operations themselves, and stream policies
- **Time:** time to live sweeps run on the cloud `TimeProvider` (`DynamoTimeToLiveJob`, every `DynamoDbTimeToLiveSweepInterval`). Asynchronous changes take `DynamoDbTransitionDelay` (zero by default: done when the request returns): a table is `CREATING` (CreateTable, restores), `UPDATING` (a throughput or billing-mode change, or while a global or vector index is built or deleted) and `DELETING` (it serves reads and writes until it is gone), a global index UpdateTable adds or deletes `CREATING`/`DELETING`, contributor insights `ENABLING`/`DISABLING`. Each change stores when it is done, and `DynamoTable.Settle` moves the table on lazily whenever a request finds it (`FindTable`, `AllTables`), removing a deleted table or index once gone; nothing runs on a timer. The refusals that go with each status (`ResourceInUseException` while creating, deleting or changing throughput, one index build at a time, `BackupInUseException` while a restore from the backup is `CREATING`) are in the operations. A stream of a `CREATING` table is `ENABLING` with no shards. Vector indexes UpdateTable adds stay `CREATING` for `DynamoDbVectorIndexCreationDelay` instead. Provisioned throughput is reported, never enforced
- **SDK packages:** DynamoDB Streams is a separate package in v4 (`AWSSDK.DynamoDBStreams`), and part of `AWSSDK.DynamoDBv2` in v3; the build integration adds `CreateDynamoDbStreamsClient()` for either
- **Fidelity:** the measure is AWS's emulator evaluation suite (`behavioral/dynamodb`, `behavioral/dynamodbstreams`, 902 checks). 853 are ported to C# in `AwsEvaluationSuite/DynamoDb*` and run in memory with the other tests; to run the originals with boto3 against the server, clone [aws-emulator-evaluation-suite](https://github.com/aws/aws-emulator-evaluation-suite), `uv sync --locked --extra checks`, start the server, then `uv run fidelity-tests-runner run behavioral/dynamodb/check_<name>.py --endpoint http://127.0.0.1:5050 --auth off`. The CC0 Rust emulator in [aws-emulator](https://github.com/aws/aws-emulator/tree/main/services/service-com-amazonaws-dynamodb-dynamodb-20120810) is a reference for exact messages

### S3

`S3Client/` holds S3, in namespace `InMemoryAws.S3` (models in `.S3.Model`, generated). Bucket names are global, so the cloud has one `S3Store` (`cloud.S3`): every bucket by name, each recording the region it was created in, behind one lock (`Gate`) every operation holds.
- **Client:** `InternalS3Client` derives from the generated `S3ClientBase`, where every operation answers `NotImplemented` until overridden, and is split into partial files by area (buckets, objects, listing, multipart, tagging, object lock, ACLs, policy, authorization, CORS, website, lifecycle, bucket configurations, encryption and SSE-C, restore, replication, notifications, metadata, annotations, rename, directory buckets, ...). `GetBucket` finds a bucket, refuses a request signed for another region (`AuthorizationHeaderMalformed`, a HEAD's 301, with `x-amz-bucket-region`) and authorizes it (`Authorize`: explicit Deny statements of the bucket policy, as `ResourcePolicyKind.Bucket`; ACLs; Block Public Access; expected bucket owner; requester pays; S3 Express sessions)
- **Store:** `S3Bucket` (partial: each area adds its own state in its own file) keeps each key's versions newest first in a `SortedDictionary` ordered by `S3KeyComparer` (UTF-8 byte order), the multipart uploads in progress, and configurations as the request models that set them. `S3ObjectVersion` is a version or delete marker: bytes, ETag, metadata, checksum and its type, parts (of a multipart object), tags, lock settings. An unversioned or suspended bucket keeps one `null` version per key. Version ids (`S3VersionIds`) carry a check value, so a malformed id is `InvalidArgument` and a well-formed unknown one `NoSuchVersion`
- **Hooks:** partial methods connect the areas: `OnObjectWritten`/`OnObjectRemoved`/`OnObjectTagged`/`OnObjectAclPut` (notifications), `CheckDeletableCore` and `ApplyObjectLock` (Object Lock), `DescribeMore`, `CheckSseCustomerKey`, `OnUploadCreated`/`OnUploadCompleting`, `OnBucketCreated`
- **Checksums:** `S3Checksums` computes CRC32, CRC32C, CRC64NVME, SHA-1 and SHA-256, composite (`-N`) and full-object multipart checksums, and ETags; an object written without a checksum gets CRC64NVME. `IS3ChecksumMembers`/`IS3ObjectHeaders` (`S3ModelInterfaces.cs`) let one helper read and write the checksum and object-header members of every model that has them
- **Errors:** `S3Exception` (`S3Errors`) carries S3's code, message, status, the extra elements of its error document (`BucketName`, `Key`, `ArgumentName`, ...) and headers
- **Notifications:** PutBucketNotificationConfiguration validates each destination (exists, in the bucket's region, its policy grants `s3.amazonaws.com`) and sends `s3:TestEvent`; events (`S3EventMessage`, version 2.1 records) go during the request to SQS through `SqsServiceDelivery`, to SNS through the topic's publish, and to the default event bus through `InternalEventBridgeClient.PutServiceEvent`
- **Authentication:** off unless `cloud.SecretAccessKeys` names the request's access key: then SigV4 (headers, presigned URLs, chunk signatures) and a 15-minute clock skew against the cloud's `TimeProvider` are checked. The evaluation suite's context sets `test`/`test`
- **Time:** Object Lock retention, restores, presigned URL expiry, CreateSession credentials, and `S3MetadataTableCreationDelay` run on the cloud's `TimeProvider`; lifecycle rules are validated and answered (`x-amz-expiration`) but never run, and replication copies each write at once
- **Fidelity:** AWS's emulator evaluation suite (`behavioral/s3`, `s3__sqs`, `s3__sns__sqs`, `events__s3__sqs`, 537 checks): 517 are ported to C# in `AwsEvaluationSuite/S3*` and `EventsS3Sqs`, 513 pass and 4 are known gaps (EventBridge delivering to a queue with no policy, and `aws:SecureTransport` deny policies, since it is always true); the other 20 need CloudFormation, KMS keys, IAM roles or Lambda

### Time Control

All time-dependent operations use `TimeProvider` from the cloud:
- Message delays (`SendMessageRequest.DelaySeconds`)
- Visibility timeouts (`ReceiveMessageRequest.VisibilityTimeout`)
- In-flight message expiration
- Move task scheduling
- EventBridge replays and scheduled rules
- DynamoDB time to live, table and vector index creation delays, transaction token windows and stream iterator expiry
- S3 Object Lock retention, restores, presigned URL expiry, session credentials and metadata table creation

This allows tests to use `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` to control time progression.

### SQS Message Lifecycle

`SqsQueueResource` owns every message on a queue (the stages are in GLOSSARY.md), split into partial files: `SqsQueueResource.cs` (settings, sends, delays), `.Receive.cs`, `.Lifecycle.cs`. Its collections are private; callers use:
- `AcceptSent` / `AcceptRedriven` in (see SQS Callers below), `ReceiveAsync` out: one call for standard and FIFO queues, long polling, redrive at `maxReceiveCount`, and FIFO `ReceiveRequestAttemptId` replays. It returns copies with every attribute; the client trims them to the request
- `DeleteMessage` / `ChangeVisibility` by receipt handle, returning a `SqsReceiptHandleOutcome` that the client maps to SQS's error (`SqsReceiptHandle` mints and parses handles)
- `Purge`, `RemoveMessage` (the dashboard's delete), `MoveMessages` (move tasks), `CountMessages` (sweeps expired messages first) and `Peek` (the dashboard's view)

Inside, a standard queue's visible messages sit in `SqsMessageStore`, which hands each new message to one parked receive; a FIFO queue keeps one `FifoGroup` per message group, guarded by locking the group. Every way out of flight goes through `LeaveFlight` (timer stopped, FIFO group slot released under the group's lock), and every way out of the queue through `Forget` (receipt handle retired). Keep any new exit on those two paths; `SqsQueueLifecycleTests` checks each exit at the queue's interface. A receive starts its messages' visibility timers only as it returns, and nothing reads the clock while holding a group lock: a `FakeTimeProvider` with `AutoAdvanceAmount` runs due timers from inside `GetUtcNow`.

`SqsMoveTask` runs a move task on a cloud `TimeProvider` timer, once a second: it resolves its source and destination ARNs on each tick (a destination gone at a tick fails it), and a tick and `TryCancel` take the task's lock, so it settles on exactly one of COMPLETED, FAILED or CANCELLED.

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
- Policies apply in `InternalS3Client.Authorize` (bucket policies, `ResourcePolicyKind.Bucket`, and S3's public-policy rules for Block Public Access), `InternalSnsClient.Authorize`, `SqsServiceDelivery`, EventBridge's delivery to topics (`EventBridgeEventDelivery`), PutEvents (`AuthorizePutEvents`) and `InternalDynamoDbClient.Authorize` (see DynamoDB). `AwsAccountId.IsValid` is the one twelve-digit check

### SQS Callers

The generated SQS handlers build `InternalSqsClient` with a `Caller` (`SqsCaller`: the SigV4 access key and source IP). A client built without one (tests, the dashboard) is never subject to queue policies.
- `SenderId` comes from `SqsCaller.SenderId` (signed: `cloud.CurrentUserId`, or the account id for an account-id access key; unsigned: the account id)
- `InternalSqsClient.Authorization.cs`: `Authorize(queue, action)` runs after each queue lookup and enforces explicit Deny statements only (`ResourcePolicy`); `EnsureKmsKeyExists` refuses sends to a queue whose KMS alias is not in `cloud.KmsKeyAliases` (only when that is set; by default every key exists)
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

**Error Response Format** (each wire protocol writes its own):
- SQS over JSON and EventBridge: `{"__type": "com.amazonaws.sqs#{ErrorCode}", "message": "..."}` (`AwsJsonErrors`), with the service framework's errors (`UnknownOperationException`, `SerializationException`, `InternalFailure`) in `com.amazon.coral.service`; SQS adds the `x-amzn-query-error` header
- SNS and SQS over Query: an XML `ErrorResponse` (`Type` Sender, or Receiver for a 5xx) carrying the request id of its header
- DynamoDB: see AWS Protocol Support above
- S3: an XML `<Error>` with `Code`, `Message`, the error's own elements, `RequestId` and `HostId` (`S3Exception`); a HEAD gets the status alone

An exception that is not an `AwsServiceException` is answered as an internal failure (HTTP 500) in the protocol's own format. The server's `WriteErrorResponseAsync` answers only failures outside every wire protocol.

## Code Generation

`tools/GenerateHandlers` reads the AWS service models in `src/InMemoryAws.Shared/ServiceModels/*.normal.json` and writes:
- `Models/{Sqs,Sns,EventBridge}Models.g.cs`: the request/response models (`InternalTypeGenerator.cs`)
- `Http/Handlers/Generated/{Sns,EventBridge}WireProtocol.g.cs`, `SqsWireProtocol.Json.g.cs` and `SqsWireProtocol.Query.g.cs`: each wire protocol's operation switch (`TryDispatch*Async`: deserialize, call the internal client, serialize), behind its hand-written `Handle*Async` (`Program.cs`; SQS Query's from `SqsQueryCodeGenerator.cs`)
- `SqsJsonSerializers.g.cs`, `EventBridgeJsonSerializers.g.cs` (`JsonCodeGenerator.cs`; the deserializers parse the body in place and turn a body that is not JSON, or a value of the wrong type, into `SerializationException`) and `SnsQuerySerializers.g.cs` (`QueryCodeGenerator.cs`)
- `SqsQuerySerializers.g.cs`: the SQS Query protocol serializers (`SqsQueryCodeGenerator.cs`). The SQS model is the JSON one and has lost the Query wire names, so the generator restores them from its `WireNames` table
- S3 uses `RestXmlCodeGenerator.cs`: `Models/S3Models.g.cs` (shapes `Object` and `Error` become `S3Object` and `S3Error`), `S3XmlSerializers.g.cs` (`Read_X` refuses an element the model doesn't have as `MalformedXML`; `Write_X`), `S3WireProtocol.g.cs` (the router: among the operations for the method and path, the one matching the most query markers, then the most required query parameters and headers; deprecated twins and Object Lambda aren't routed; and the dispatcher, binding each member from its location) and `S3Client/S3ClientBase.g.cs`
- DynamoDB and DynamoDB Streams use `ShapeJsonCodeGenerator.cs`, which writes a method per shape (`Read_X`/`Write_X`/`Validate_X`) so recursive and nested shapes work: `Models/DynamoDb{,Streams}Models.g.cs` (collections null when absent, so `"L": []` stays distinct from a missing list; Java-style `ToString` for validation messages), `DynamoDb{,Streams}JsonSerializers.g.cs` (including the model's constraint checks, worded as AWS's request validation words them) and `DynamoDb{,Streams}OperationDispatcher.g.cs`. `AttributeValue` maps to the hand-written `DynamoValue`

**Regenerate** (the argument is the shared project directory):
```bash
dotnet run --project tools/GenerateHandlers/GenerateHandlers.csproj -- src/InMemoryAws.Shared
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

- `tests/InMemoryAws.Tests.Shared/`: abstract test classes (e.g. `SqsFifoTests`), compiled into two projects:
  - `InMemoryAws.Tests/LocalAwsMessaging/` subclasses run them in memory through `CreateSqsClient()` etc.
  - `InMemoryAws.Tests.Verification/ExternalVerification/` subclasses run them against Floci
- `InMemoryAws.Tests/AwsEvaluationSuite/`: C# ports of AWS's emulator evaluation suite (SQS, SNS, EventBridge, DynamoDB, DynamoDB Streams and S3), checks verified against real AWS (see its `README.md`); `KnownFidelityGap` marks the ones not passed yet. Its `UPSTREAM.md` snapshots every tracked upstream check at a pinned commit (ported, known gap, or why not); `tools/SyncEvaluationSuite` rewrites it and reports drift, and the build fails when it is out of step with the ports, so regenerate it after adding, moving or deleting a port (the `update-evaluation-suite` skill walks an upstream update). DynamoDB ports share `DynamoDbFidelity.cs` (boto3-style values, Python `repr`/`==`, waiters, raw requests); S3 ports share the `S3Fidelity*.cs` helpers (the checks' bucket creation and cleanup, and raw requests for what the .NET SDK hides: error headers and members, 404s it turns into empty successes)
- `InMemoryAws.Tests/DynamoDb/`: DynamoDB through the low-level client, `DynamoDBContext`, the Document Model and the Streams client
- `InMemoryAws.Tests/` (other folders): focused regression and behaviour tests, mostly through the SDK clients; a few reach internals (`InternalsVisibleTo`)
- `tests/InMemoryAws.Tests.Server/`: the server, through real SDK clients against Kestrel, and its dashboard API
- `tests/InMemoryAws.Tests.AwsSdkV3/` (net10.0, net48) and `tests/InMemoryAws.Tests.NetFramework/` (net48): `BringYourOwnSdkTests`, the build integration's clients against AWS SDK v3 (Renovate keeps its versions pinned) and v4 on .NET Framework

## Package Management

Uses Central Package Management (Directory.Packages.props):
- All package versions defined centrally
- Test projects automatically get TUnit and coverage tools via `IsTestProject=true`
