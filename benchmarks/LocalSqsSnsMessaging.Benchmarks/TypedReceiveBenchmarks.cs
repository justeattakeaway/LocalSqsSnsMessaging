using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.SQS;
using Amazon.SQS.Model;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using LocalSqsSnsMessaging.Generic;

namespace LocalSqsSnsMessaging.Benchmarks;

[JsonSerializable(typeof(TypedReceiveBenchmarks.OrderEvent))]
internal sealed partial class OrderEventJsonContext : JsonSerializerContext;

/// <summary>
/// Compares the standard receive-then-deserialize pattern against
/// <see cref="TypedAmazonSQSClient.ReceiveMessageAsync{T}"/>, which deserializes
/// the message body straight from the response stream.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 8)]
public class TypedReceiveBenchmarks
{
    public sealed record OrderEvent(
        int OrderId,
        string Customer,
        string Sku,
        decimal Total,
        DateTimeOffset CreatedAt,
        string Notes);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private InMemoryAwsBus _bus = null!;
    private AmazonSQSClient _stockClient = null!;
    private TypedAmazonSQSClient _typedClient = null!;
    private string _queueUrl = null!;
    private string _serializedPayload = null!;
    private byte[] _serializedPayloadUtf8 = null!;

    /// <summary>
    /// Total messages drained per benchmark invocation. Each invocation issues
    /// <c>MessagesPerInvocation / 10</c> ReceiveMessage calls with
    /// <c>MaxNumberOfMessages = 10</c>, then BDN normalises by
    /// <c>OperationsPerInvoke</c> to report per-message cost.
    /// </summary>
    private const int MessagesPerInvocation = 1000;

    /// <summary>Approximate JSON payload size on the wire.</summary>
    [Params("small", "medium")]
    public string PayloadSize { get; set; } = "small";

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _bus = new InMemoryAwsBus();
        _stockClient = _bus.CreateSqsClient();
        _typedClient = _bus.CreateTypedSqsClient();
        _queueUrl = (await _stockClient.CreateQueueAsync("bench-queue").ConfigureAwait(false)).QueueUrl;

        var notes = PayloadSize switch
        {
            "small" => "ok",
            "medium" => new string('x', 512),
            _ => "ok",
        };

        var payload = new OrderEvent(
            OrderId: 42,
            Customer: "Alice O'Brien",
            Sku: "SKU-12345",
            Total: 19.99m,
            CreatedAt: new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero),
            Notes: notes);
        _serializedPayload = JsonSerializer.Serialize(payload, Options);
        _serializedPayloadUtf8 = JsonSerializer.SerializeToUtf8Bytes(payload, OrderEventJsonContext.Default.OrderEvent);
    }

    [IterationSetup]
    public void RefillQueue()
    {
        // Drain whatever is left over from the previous iteration, then top up to
        // exactly MessagesPerInvocation. Refill cost is paid by both benchmark
        // methods equally, so it doesn't influence the comparison.
        while (true)
        {
            var resp = _stockClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = _queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 0,
                VisibilityTimeout = 0,
            }).GetAwaiter().GetResult();
            if (resp.Messages is null || resp.Messages.Count == 0) break;
            foreach (var m in resp.Messages)
            {
                _ = _stockClient.DeleteMessageAsync(_queueUrl, m.ReceiptHandle).GetAwaiter().GetResult();
            }
        }

        for (var i = 0; i < MessagesPerInvocation; i++)
        {
            _ = _stockClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = _queueUrl,
                MessageBody = _serializedPayload,
            }).GetAwaiter().GetResult();
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _stockClient.Dispose();
        _typedClient.Dispose();
    }

    /// <summary>
    /// Stock SDK path: receive, then JsonSerializer.Deserialize&lt;T&gt;(message.Body).
    /// Each message body is materialized as a .NET string before being parsed.
    /// </summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = MessagesPerInvocation)]
    public async Task<OrderEvent?> Standard_ReceiveAndDeserialize()
    {
        OrderEvent? last = null;
        var remaining = MessagesPerInvocation;
        while (remaining > 0)
        {
            var response = await _stockClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = _queueUrl,
                MaxNumberOfMessages = Math.Min(10, remaining),
            }).ConfigureAwait(false);

            if (response.Messages is null || response.Messages.Count == 0) break;
            foreach (var message in response.Messages)
            {
                last = JsonSerializer.Deserialize<OrderEvent>(message.Body!, Options);
            }
            remaining -= response.Messages.Count;
        }
        return last;
    }

    /// <summary>
    /// Typed path with reflection-based metadata: ReceiveMessageAsync&lt;T&gt; via
    /// custom unmarshaller. Body bytes flow from the response buffer straight
    /// into <see cref="JsonSerializer"/> with no intermediate System.String.
    /// </summary>
    [Benchmark(OperationsPerInvoke = MessagesPerInvocation)]
    public async Task<OrderEvent?> Typed_ReceiveOfT()
    {
        OrderEvent? last = null;
        var remaining = MessagesPerInvocation;
        while (remaining > 0)
        {
            var response = await _typedClient.ReceiveMessageAsync<OrderEvent>(new ReceiveMessageRequest
            {
                QueueUrl = _queueUrl,
                MaxNumberOfMessages = Math.Min(10, remaining),
            }).ConfigureAwait(false);

            if (response.Messages.Count == 0) break;
            foreach (var message in response.Messages)
            {
                last = message.Body;
            }
            remaining -= response.Messages.Count;
        }
        return last;
    }

    /// <summary>
    /// Typed path with source-generated metadata: same fast unmarshaller, but
    /// deserialization uses the supplied <c>JsonTypeInfo&lt;T&gt;</c> from a
    /// generated <see cref="JsonSerializerContext"/> — trim/AOT-safe.
    /// </summary>
    [Benchmark(OperationsPerInvoke = MessagesPerInvocation)]
    public async Task<OrderEvent?> Typed_ReceiveOfT_SourceGen()
    {
        OrderEvent? last = null;
        var remaining = MessagesPerInvocation;
        while (remaining > 0)
        {
            var response = await _typedClient.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = _queueUrl,
                    MaxNumberOfMessages = Math.Min(10, remaining),
                },
                OrderEventJsonContext.Default.OrderEvent).ConfigureAwait(false);

            if (response.Messages.Count == 0) break;
            foreach (var message in response.Messages)
            {
                last = message.Body;
            }
            remaining -= response.Messages.Count;
        }
        return last;
    }

    /// <summary>
    /// Reference: pure deserialization cost. Repeatedly deserializes a
    /// pre-captured UTF-8 byte buffer using source-generated metadata, with
    /// zero AWS SDK involvement. Establishes the floor below which no
    /// receive-and-deserialize benchmark can drop.
    /// </summary>
    [Benchmark(OperationsPerInvoke = MessagesPerInvocation)]
    public OrderEvent? Reference_PureDeserialize_SourceGen()
    {
        OrderEvent? last = null;
        var span = _serializedPayloadUtf8.AsSpan();
        for (var i = 0; i < MessagesPerInvocation; i++)
        {
            last = JsonSerializer.Deserialize(span, OrderEventJsonContext.Default.OrderEvent);
        }
        return last;
    }

    /// <summary>
    /// Reference: receive only, no deserialization. Calls the stock SDK
    /// ReceiveMessageAsync and walks the messages but never touches Body.
    /// Isolates the AWS SDK pipeline cost (HTTP plumbing, request/response
    /// objects, marshalling everything other than Body) from the deserialize
    /// cost. Standard − Reference_ReceiveOnly ≈ deserialization-and-string-body cost.
    /// </summary>
    [Benchmark(OperationsPerInvoke = MessagesPerInvocation)]
    public async Task<int> Reference_ReceiveOnly()
    {
        var count = 0;
        var remaining = MessagesPerInvocation;
        while (remaining > 0)
        {
            var response = await _stockClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = _queueUrl,
                MaxNumberOfMessages = Math.Min(10, remaining),
            }).ConfigureAwait(false);

            if (response.Messages is null || response.Messages.Count == 0) break;
            count += response.Messages.Count;
            remaining -= response.Messages.Count;
        }
        return count;
    }
}
