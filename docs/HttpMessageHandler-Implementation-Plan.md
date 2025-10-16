# Implementation Plan: HttpMessageHandler-Based Mode for LocalSqsSnsMessaging

## Overview

The goal is to create a mode where users can instantiate real `AmazonSQSClient` and `AmazonSimpleNotificationServiceClient` instances that are configured with a custom `HttpMessageHandler`. This handler will intercept HTTP requests, deserialize them, route to the existing in-memory implementations, and serialize responses back—all while maintaining AWS SDK JSON protocol compatibility.

## Architecture Design

### Core Components

- `InMemoryAwsHttpMessageHandler : DelegatingHandler` - Intercepts HTTP requests and routes to in-memory clients
- `AwsJsonRequestDeserializer` - Deserializes AWS JSON protocol requests
- `AwsJsonResponseSerializer` - Serializes responses to AWS JSON protocol
- Extension methods on `InMemoryAwsBus` to create SDK clients with the handler pre-configured

### Request/Response Flow

```
AmazonSQSClient (real SDK client)
  ↓ HTTP Request (JSON)
InMemoryAwsHttpMessageHandler
  ↓ Deserialize
InMemorySqsClient (existing)
  ↓ Execute operation
Response Object
  ↓ Serialize
InMemoryAwsHttpMessageHandler
  ↓ HTTP Response (JSON)
AmazonSQSClient returns to user
```

## Implementation Steps

### Phase 1: Foundation (Week 1)

#### 1.1 Create HttpMessageHandler Infrastructure
- Create `src/LocalSqsSnsMessaging/Http/InMemoryAwsHttpMessageHandler.cs`
  - Inherit from `DelegatingHandler`
  - Override `SendAsync` to intercept requests
  - Parse the request URL and headers to determine service (SQS/SNS) and operation
  - Route to appropriate service handler

#### 1.2 Operation Routing
- Create `src/LocalSqsSnsMessaging/Http/OperationRouter.cs`
  - Extract operation name from `x-amz-target` header or request body
  - Map operation names to method calls on InMemorySqsClient/InMemorySnsClient
  - Handle both SQS and SNS operations

#### 1.3 Request Context
- Create `src/LocalSqsSnsMessaging/Http/AwsRequestContext.cs`
  - Encapsulate service type (SQS/SNS), operation name, request body
  - Store headers and metadata needed for processing

### Phase 2: JSON Serialization Layer (Week 2)

#### 2.1 AWS Service Models (Code Generation Approach)
- Since AWS SDK already contains all Request/Response types, we don't need to generate those
- Create a build-time T4 template or source generator that:
  - Scans `AWSSDK.SQS.dll` and `AWSSDK.SimpleNotificationService.dll` at build time
  - Generates mapping code for operation names to request/response types
  - Creates serializer/deserializer method mappings

#### 2.2 Request Deserialization
- Create `src/LocalSqsSnsMessaging/Http/Serialization/AwsJsonRequestDeserializer.cs`
  - Use `System.Text.Json` with `JsonSerializerOptions` configured for AWS JSON protocol
  - Handle AWS-specific JSON formatting (PascalCase properties, AWS date formats)
  - Map JSON to SDK Request objects (e.g., `SendMessageRequest`)

#### 2.3 Response Serialization
- Create `src/LocalSqsSnsMessaging/Http/Serialization/AwsJsonResponseSerializer.cs`
  - Serialize SDK Response objects back to AWS JSON format
  - Include required AWS response metadata (RequestId, headers)
  - Use `Utf8JsonWriter` for zero-allocation serialization

#### 2.4 JSON Naming Policy
- Create custom `JsonNamingPolicy` that matches AWS conventions
  - Properties typically use PascalCase in AWS JSON protocol
  - Handle special AWS-specific attribute naming

### Phase 3: Operation Mapping (Week 2-3)

#### 3.1 SQS Operation Mapper
- Create `src/LocalSqsSnsMessaging/Http/Handlers/SqsOperationHandler.cs`
  - Map SQS operations (e.g., "SendMessage", "ReceiveMessage", "CreateQueue") to `InMemorySqsClient` methods
  - Handle parameter mapping from JSON to method calls
  - Use reflection or generated code for efficient dispatch

#### 3.2 SNS Operation Mapper
- Create `src/LocalSqsSnsMessaging/Http/Handlers/SnsOperationHandler.cs`
  - Map SNS operations (e.g., "Publish", "Subscribe", "CreateTopic") to `InMemorySnsClient` methods
  - Similar pattern to SQS handler

#### 3.3 Code Generation for Mappers
- Build-time code generation (MSBuild task or T4):
  ```csharp
  // Generated code example
  public static class SqsOperationDispatcher
  {
      public static async Task<object> DispatchAsync(
          string operationName,
          JsonDocument requestBody,
          InMemorySqsClient client,
          CancellationToken ct)
      {
          return operationName switch
          {
              "SendMessage" => await HandleSendMessage(requestBody, client, ct),
              "ReceiveMessage" => await HandleReceiveMessage(requestBody, client, ct),
              // ... all operations
          };
      }

      private static async Task<SendMessageResponse> HandleSendMessage(
          JsonDocument body,
          InMemorySqsClient client,
          CancellationToken ct)
      {
          var request = JsonSerializer.Deserialize<SendMessageRequest>(body);
          return await client.SendMessageAsync(request, ct);
      }
  }
  ```

### Phase 4: Extension Methods & Configuration (Week 3)

#### 4.1 New Extension Methods
- Add to `InMemoryAwsBusExtensions.cs` or create new file:
  ```csharp
  public static class InMemoryAwsBusHttpExtensions
  {
      public static AmazonSQSClient CreateSdkSqsClient(this InMemoryAwsBus bus)
      {
          var handler = new InMemoryAwsHttpMessageHandler(bus, ServiceType.SQS);
          var config = new AmazonSQSConfig
          {
              ServiceURL = "https://sqs.us-east-1.amazonaws.com",
              HttpClientFactory = new InMemoryHttpClientFactory(handler)
          };
          return new AmazonSQSClient(new AnonymousAWSCredentials(), config);
      }

      public static AmazonSimpleNotificationServiceClient CreateSdkSnsClient(
          this InMemoryAwsBus bus)
      {
          var handler = new InMemoryAwsHttpMessageHandler(bus, ServiceType.SNS);
          var config = new AmazonSimpleNotificationServiceConfig
          {
              ServiceURL = "https://sns.us-east-1.amazonaws.com",
              HttpClientFactory = new InMemoryHttpClientFactory(handler)
          };
          return new AmazonSimpleNotificationServiceClient(
              new AnonymousAWSCredentials(), config);
      }
  }
  ```

#### 4.2 HttpClientFactory Implementation
- Create `src/LocalSqsSnsMessaging/Http/InMemoryHttpClientFactory.cs`
  - Implements `IHttpClientFactory` or configure AWS SDK to use the handler
  - Returns `HttpClient` instances with our custom handler

### Phase 5: Testing & Validation (Week 4)

#### 5.1 Adapt Existing Tests
- Create new test class that runs existing tests against SDK clients
- Pattern:
  ```csharp
  public class SqsSdkClientTests : SqsTestsBase
  {
      protected override IAmazonSQS CreateClient(InMemoryAwsBus bus)
      {
          return bus.CreateSdkSqsClient(); // New extension method
      }
  }
  ```

#### 5.2 Protocol Validation
- Use LocalStack verification tests to capture real request/response payloads
- Compare serialization output with actual AWS JSON format
- Validate edge cases (empty collections, null values, special characters)

#### 5.3 Performance Testing
- Benchmark the HTTP handler overhead vs direct in-memory client
- Optimize serialization hot paths using `Utf8JsonWriter` and pooling

### Phase 6: Code Generation Implementation

#### Option A: MSBuild Task (Recommended)
- Create `src/LocalSqsSnsMessaging.CodeGen` project
- MSBuild task that runs before compilation:
  1. Loads AWSSDK assemblies using reflection
  2. Scans for all Request/Response types
  3. Generates dispatcher classes with operation mappings
  4. Outputs `.g.cs` files into obj directory

#### Option B: T4 Templates
- Simpler but less flexible
- `OperationDispatcher.tt` template file
- Manually list operations or use simple reflection

#### Option C: Runtime Reflection with Caching (Prototype First)
- No code generation needed
- Build operation dispatch dictionary at runtime (first call)
- Cache the mappings in static dictionary
- Slightly slower startup but simpler implementation

**Recommendation: Start with Option C for prototyping, migrate to Option A for production**

## Project Structure

```
src/LocalSqsSnsMessaging/
  Http/
    InMemoryAwsHttpMessageHandler.cs
    InMemoryHttpClientFactory.cs
    AwsRequestContext.cs
    OperationRouter.cs
    Serialization/
      AwsJsonRequestDeserializer.cs
      AwsJsonResponseSerializer.cs
      AwsJsonNamingPolicy.cs
    Handlers/
      SqsOperationHandler.cs
      SnsOperationHandler.cs
  InMemoryAwsBusHttpExtensions.cs

tests/LocalSqsSnsMessaging.Tests/
  SdkClient/
    SqsSdkClientTests.cs
    SnsSdkClientTests.cs
```

## Key Technical Decisions

1. **Serialization**: Use `System.Text.Json` with custom converters for AWS-specific types
2. **Operation Dispatch**: Start with reflection + caching, optionally add source generation later
3. **HttpClient Configuration**: Use AWS SDK's configuration system (`AmazonSQSConfig`)
4. **Error Handling**: Map exceptions from in-memory clients to HTTP error responses
5. **Async**: Full async support throughout the handler pipeline

## Testing Strategy

1. **Unit Tests**: Test each serializer/deserializer independently
2. **Integration Tests**: Reuse all existing tests with SDK clients
3. **Compatibility Tests**: Use LocalStack verification to validate JSON format
4. **Performance Tests**: Ensure overhead is minimal (<5% compared to direct client)

## Migration Path for Users

**Before (Current)**:
```csharp
var bus = new InMemoryAwsBus();
var sqs = bus.CreateSqsClient(); // Returns InMemorySqsClient
```

**After (New Mode)**:
```csharp
var bus = new InMemoryAwsBus();
var sqs = bus.CreateSdkSqsClient(); // Returns AmazonSQSClient with handler
```

Both modes can coexist. Users choose based on their needs.

## Risks & Mitigation

1. **Risk**: AWS JSON protocol complexity
   - **Mitigation**: Start with core operations, use LocalStack for validation

2. **Risk**: Performance overhead from serialization
   - **Mitigation**: Use `Utf8JsonWriter`, benchmark, optimize hot paths

3. **Risk**: Operation mapping maintenance
   - **Mitigation**: Code generation or comprehensive reflection system

4. **Risk**: SDK version compatibility
   - **Mitigation**: Target specific SDK version range, document compatibility

## Success Criteria

1. All existing tests pass when using SDK clients via HttpMessageHandler
2. JSON protocol matches AWS format (validated against LocalStack)
3. Performance overhead < 10% compared to direct in-memory client
4. No new NuGet dependencies required
5. Documentation shows both usage modes

## Timeline Estimate

- **Week 1**: Foundation & HTTP handler infrastructure
- **Week 2**: Serialization layer & JSON protocol
- **Week 3**: Operation mapping & code generation
- **Week 4**: Testing, validation, documentation

**Total: 3-4 weeks for full implementation**
