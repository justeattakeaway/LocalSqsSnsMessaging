# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

LocalSqsSnsMessaging is a .NET 8 library providing an in-memory drop-in replacement for AWS SDK SQS and SNS clients. It's designed for fast testing and local development without requiring LocalStack or external services.

Key features:
- In-memory message bus supporting both SQS queues and SNS topics
- Full support for `TimeProvider` to control time in tests (delays, visibility timeouts, message expiration)
- FIFO queue support with message groups and deduplication
- SNS-to-SQS subscriptions with raw message delivery
- Redrive policies and dead-letter queues

## Building and Testing

### Build and Test
```bash
./build.ps1
```

This PowerShell script:
- Installs the correct .NET SDK version (from global.json) if needed
- Builds and packs the NuGet package (src/LocalSqsSnsMessaging)
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

### Verification Tests (LocalStack)
The project includes verification tests that run against LocalStack to ensure correctness:
```bash
cd tests/LocalSqsSnsMessaging.Tests.Verification
dotnet run -c Release -- --timeout 2m --no-progress --log-level Warning
```

## Architecture

### Core Components

**InMemoryAwsBus** (src/LocalSqsSnsMessaging/InMemoryAwsBus.cs:8)
- Central message bus managing all queues, topics, subscriptions, and move tasks
- Configurable with `TimeProvider`, `CurrentAccountId`, and `CurrentRegion`
- Thread-safe using `ConcurrentDictionary` for all resource collections

**InMemorySqsClient** (src/LocalSqsSnsMessaging/SqsClient/InMemorySqsClient.cs)
- Implements AWS SQS SDK interface (`IAmazonSQS`)
- Supports standard and FIFO queues, message visibility, delays, redrive policies
- Uses `Channel<Message>` for message queuing

**InMemorySnsClient** (src/LocalSqsSnsMessaging/SnsClient/InMemorySnsClient.cs)
- Implements AWS SNS SDK interface (`IAmazonSimpleNotificationService`)
- Publishes messages to subscribed SQS queues
- Supports topic attributes and subscription filtering

### HttpMessageHandler Mode

**InMemoryAwsHttpMessageHandler** (src/LocalSqsSnsMessaging/Http/InMemoryAwsHttpMessageHandler.cs)
- `DelegatingHandler` that intercepts HTTP requests from real AWS SDK clients
- Deserializes requests, routes to in-memory clients, serializes responses
- Enables using concrete `AmazonSQSClient` and `AmazonSimpleNotificationServiceClient` types

**AWS Protocol Support:**

The HTTP mode implements two different AWS protocols:

1. **SQS - JSON Protocol:**
   - Content-Type: `application/x-amz-json-1.0`
   - Request/Response: JSON with camelCase properties
   - Operations identified by `X-Amz-Target` header
   - Example: `{"QueueUrl": "...", "MaxNumberOfMessages": 10}`

2. **SNS - Query Protocol:**
   - Content-Type: `application/x-www-form-urlencoded`
   - Request: Form-encoded query parameters (e.g., `Action=Publish&TopicArn=...`)
   - Response: XML format
   - Nested structures use indexed notation: `MessageAttributes.entry.1.Name=foo&MessageAttributes.entry.1.Value.DataType=String`

**Operation Handlers** (src/LocalSqsSnsMessaging/Http/Handlers/)
- Use reflection-based dispatch to map AWS operations to in-memory client methods
- `SqsOperationHandler`: Routes SQS operations (SendMessage, ReceiveMessage, etc.)
- `SnsOperationHandler`: Routes SNS operations (Publish, Subscribe, etc.)
- Operation cache built at runtime on first use for performance

**Extension Methods**
- `InMemoryAwsBusHttpExtensions.cs`: SDK client mode (HTTP interceptor)
  - `CreateSqsClient()`: Returns `AmazonSQSClient` with in-memory handler
  - `CreateSnsClient()`: Returns `AmazonSimpleNotificationServiceClient` with in-memory handler
- `InMemoryAwsBusExtensions.cs`: Direct mode (raw client)
  - `CreateRawSqsClient()`: Returns `InMemorySqsClient` directly
  - `CreateRawSnsClient()`: Returns `InMemorySnsClient` directly
- Both modes share the same `InMemoryAwsBus` instance and state

**Resource Models**
- `SqsQueueResource` (src/LocalSqsSnsMessaging/SqsQueueResource.cs:7): Queue state including messages, in-flight messages, message groups for FIFO
- `SnsTopicResource` (src/LocalSqsSnsMessaging/SnsTopicResource.cs:3): Topic state with publish actions
- `SnsSubscription` (src/LocalSqsSnsMessaging/SnsSubscription.cs): Links topics to queues

### Time Control

All time-dependent operations use `TimeProvider` from the bus:
- Message delays (`SendMessageRequest.DelaySeconds`)
- Visibility timeouts (`ReceiveMessageRequest.VisibilityTimeout`)
- In-flight message expiration
- Move task scheduling

This allows tests to use `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` to control time progression.

### Background Jobs

**SqsInflightMessageExpirationJob** (src/LocalSqsSnsMessaging/SqsInflightMessageExpirationJob.cs)
- Returns in-flight messages to queue when visibility timeout expires
- Uses `TimeProvider` for scheduling

**SqsMoveTaskJob** (src/LocalSqsSnsMessaging/SqsMoveTaskJob.cs)
- Moves messages from source queue to destination (e.g., DLQ to main queue)
- Tracks progress via `SqsMoveTask`

### FIFO Queue Handling

FIFO queues (.fifo suffix) have special handling:
- Messages within a message group are processed sequentially
- Deduplication IDs prevent duplicate messages within 5-minute windows
- Message group locks ensure ordering (`SqsQueueResource.MessageGroupLocks`)
- Each message group has its own queue (`SqsQueueResource.MessageGroups`)

### Exception Handling in HTTP Mode

When using SDK clients (`CreateSqsClient`/`CreateSnsClient`), exceptions must include the `ErrorCode` property for the AWS SDK to deserialize them correctly:

```csharp
// ✅ Correct - SDK will recognize as ResourceNotFoundException
throw new ResourceNotFoundException("Queue not found")
{
    ErrorCode = "ResourceNotFoundException",  // Must match exception type name
    StatusCode = HttpStatusCode.BadRequest
};

// ❌ Wrong - SDK receives generic AmazonSQSException
throw new ResourceNotFoundException("Queue not found");
```

**Error Response Format:**
- SQS (JSON): `{"__type": "com.amazonaws.sqs#{ErrorCode}", "message": "..."}`
- SNS (Query/XML): Standard AWS Query protocol error format

The `ErrorCode` property is automatically formatted into the AWS error type string by the handler's `CreateErrorResponse` method.

## Code Generation

### Handler Generation

Operation handlers in `src/LocalSqsSnsMessaging/Http/Handlers/Generated/` are auto-generated:
- `SqsOperationHandler.g.cs` - JSON protocol handler for SQS operations
- `SnsOperationHandler.g.cs` - Query protocol handler for SNS operations
- `SnsQuerySerializers.g.cs` - Query string deserializers for SNS request parameters
- `SqsQuerySerializers.g.cs` - Query string deserializers for SQS request parameters

**Regenerate handlers:**
```bash
pwsh tools/GenerateHandlers/generate-handlers.ps1
# or
dotnet run --project tools/GenerateHandlers/GenerateHandlers.csproj -- src/LocalSqsSnsMessaging

# Use absolute paths to avoid ambiguity:
dotnet run --project /Users/stuart/git/github/LocalSqsSnsMessaging/tools/GenerateHandlers/GenerateHandlers.csproj -- /Users/stuart/git/github/LocalSqsSnsMessaging/src/LocalSqsSnsMessaging
```

**Important**:
- When AWS SDK request formats don't match expectations, check the generated deserializers first. The AWS SDK may use different parameter names than documented (e.g., `MessageAttributes.entry.1.Name` vs `MessageAttributes.entry.1.key`).
- After modifying the code generator in `tools/GenerateHandlers/`, always regenerate the handlers before testing.
- The generator is located at `tools/GenerateHandlers/JsonCodeGenerator.cs` (for SQS/JSON) and `QueryCodeGenerator.cs` (for SNS/Query).

## Project Structure

```
src/LocalSqsSnsMessaging/          # Main library
  InMemoryAwsBus.cs                # Central bus
  InMemoryAwsBusExtensions.cs      # CreateRawSqsClient/CreateRawSnsClient (direct mode)
  InMemoryAwsBusHttpExtensions.cs  # CreateSqsClient/CreateSnsClient (SDK client mode)
  SqsClient/                       # SQS implementation
  SnsClient/                       # SNS implementation
  Http/                            # HttpMessageHandler infrastructure
    InMemoryAwsHttpMessageHandler.cs
    Handlers/
      Generated/                   # Auto-generated operation handlers
        SqsOperationHandler.g.cs
        SnsOperationHandler.g.cs
        SnsQuerySerializers.g.cs
    Serialization/
      AwsJsonRequestDeserializer.cs
      AwsJsonResponseSerializer.cs
  *Resource.cs                     # Resource state models

tools/
  GenerateHandlers/                # Code generator for HTTP handlers

tests/
  LocalSqsSnsMessaging.Tests/               # Main test suite (TUnit)
    LocalAwsMessaging/                      # Tests using SDK client mode (HTTP interceptor)
    SdkClient/                              # Smoke tests for SDK client mode
  LocalSqsSnsMessaging.Tests.Verification/  # LocalStack verification tests
  LocalSqsSnsMessaging.Tests.Shared/        # Shared test utilities
```

## Testing Framework

This project uses **TUnit** (not xUnit or NUnit) for testing:
- Test projects have `OutputType=Exe` and are run via `dotnet run`
- Coverage enabled with `Microsoft.Testing.Extensions.CodeCoverage`
- Assertions use Shouldly library

### Test Organization

Test classes are organized by client mode:
- **LocalAwsMessaging/** - Tests using `CreateSqsClient()`/`CreateSnsClient()` (SDK client mode with HTTP interceptor)
  - Example: `SqsReceiveMessageAsyncTestsLocalAwsMessaging`
  - These exercise the full HTTP serialization/deserialization pipeline
- **Direct/** (if present) - Tests using `CreateRawSqsClient()`/`CreateRawSnsClient()` (direct mode)
  - Bypass HTTP layer entirely
- **SdkClient/** - Smoke tests verifying SDK client mode works end-to-end

Both modes share the same `InMemoryAwsBus` instance but exercise different code paths for serialization.

## Package Management

Uses Central Package Management (Directory.Packages.props):
- All package versions defined centrally
- Test projects automatically get TUnit, coverage tools, and ReportGenerator via `IsTestProject=true`
