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

### Run Single Test
```bash
# TUnit uses different test filtering syntax
dotnet run --project tests/LocalSqsSnsMessaging.Tests/LocalSqsSnsMessaging.Tests.csproj --configuration Release -- --filter "FullyQualifiedName~TestMethodName"
```

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
- Deserializes JSON requests, routes to in-memory clients, serializes responses
- Enables using concrete `AmazonSQSClient` and `AmazonSimpleNotificationServiceClient` types

**Operation Handlers** (src/LocalSqsSnsMessaging/Http/Handlers/)
- Use reflection-based dispatch to map AWS operations to in-memory client methods
- `SqsOperationHandler`: Routes SQS operations (SendMessage, ReceiveMessage, etc.)
- `SnsOperationHandler`: Routes SNS operations (Publish, Subscribe, etc.)
- Operation cache built at runtime on first use for performance

**Extension Methods** (src/LocalSqsSnsMessaging/InMemoryAwsBusHttpExtensions.cs)
- `CreateSdkSqsClient()`: Returns `AmazonSQSClient` with in-memory handler
- `CreateSdkSnsClient()`: Returns `AmazonSimpleNotificationServiceClient` with in-memory handler
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

## Project Structure

```
src/LocalSqsSnsMessaging/          # Main library
  InMemoryAwsBus.cs                # Central bus
  InMemoryAwsBusExtensions.cs      # CreateSqsClient/CreateSnsClient (direct mode)
  InMemoryAwsBusHttpExtensions.cs  # CreateSdkSqsClient/CreateSdkSnsClient (HTTP mode)
  SqsClient/                       # SQS implementation
  SnsClient/                       # SNS implementation
  Http/                            # HttpMessageHandler infrastructure
    InMemoryAwsHttpMessageHandler.cs
    Handlers/
      SqsOperationHandler.cs
      SnsOperationHandler.cs
    Serialization/
      AwsJsonRequestDeserializer.cs
      AwsJsonResponseSerializer.cs
  *Resource.cs                     # Resource state models

tests/
  LocalSqsSnsMessaging.Tests/               # Main test suite (TUnit)
    SdkClient/                              # Tests for SDK client mode
  LocalSqsSnsMessaging.Tests.Verification/  # LocalStack verification tests
  LocalSqsSnsMessaging.Tests.Shared/        # Shared test utilities
```

## Testing Framework

This project uses **TUnit** (not xUnit or NUnit) for testing:
- Test projects have `OutputType=Exe` and are run via `dotnet run`
- Coverage enabled with `Microsoft.Testing.Extensions.CodeCoverage`
- Assertions use Shouldly library

## Package Management

Uses Central Package Management (Directory.Packages.props):
- All package versions defined centrally
- Test projects automatically get TUnit, coverage tools, and ReportGenerator via `IsTestProject=true`
