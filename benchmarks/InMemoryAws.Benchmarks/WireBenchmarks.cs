using System.Net;
using Amazon.DynamoDBv2.Model;
using Amazon.EventBridge.Model;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using BenchmarkDotNet.Attributes;

namespace InMemoryAws.Benchmarks;

/// <summary>
/// One request (or, for SQS, a send-receive-delete round trip that keeps the queue empty) per
/// wire protocol, through each <see cref="Host"/>. Resources are made once, so each invocation
/// measures the request path: routing, reading the body, the operation, and writing the response.
/// </summary>
[MemoryDiagnoser]
public class WireBenchmarks
{
    private BenchmarkHost _host = null!;
    private string _queueUrl = null!;
    private string _queryQueueUrl = null!;
    private string _topicArn = null!;

    [ParamsAllValues]
    public Host Host { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _host = await BenchmarkHost.StartAsync(Host);
        _queueUrl = (await _host.Sqs.CreateQueueAsync("bench")).QueueUrl;
        _queryQueueUrl = (await _host.Sqs.CreateQueueAsync("bench-query")).QueueUrl;
        _topicArn = (await _host.Sns.CreateTopicAsync("bench")).TopicArn;
        await _host.DynamoDb.CreateTableAsync(new CreateTableRequest
        {
            TableName = "bench",
            KeySchema = [new KeySchemaElement("pk", Amazon.DynamoDBv2.KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", Amazon.DynamoDBv2.ScalarAttributeType.S)],
            BillingMode = Amazon.DynamoDBv2.BillingMode.PAY_PER_REQUEST,
        });

        // Run each benchmark once, so a broken setup fails here rather than measuring errors.
        await SqsJson_SendReceiveDelete();
        await SqsQuery_SendReceiveDelete();
        await Sns_Publish();
        await EventBridge_PutEvents();
        await DynamoDb_PutItem();
        await DynamoDb_GetItem();
    }

    [GlobalCleanup]
    public async Task CleanupAsync() => await _host.DisposeAsync();

    [Benchmark]
    public async Task SqsJson_SendReceiveDelete()
    {
        await _host.Sqs.SendMessageAsync(_queueUrl, "hello from the benchmark");
        var received = await _host.Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = _queueUrl,
            MaxNumberOfMessages = 1,
            MessageAttributeNames = ["All"],
        });
        await _host.Sqs.DeleteMessageAsync(_queueUrl, received.Messages![0].ReceiptHandle);
    }

    [Benchmark]
    public async Task SqsQuery_SendReceiveDelete()
    {
        await PostSqsQueryAsync($"Action=SendMessage&QueueUrl={Uri.EscapeDataString(_queryQueueUrl)}&MessageBody=hello%20from%20the%20benchmark");
        var received = await PostSqsQueryAsync($"Action=ReceiveMessage&QueueUrl={Uri.EscapeDataString(_queryQueueUrl)}&MaxNumberOfMessages=1");
        var start = received.IndexOf("<ReceiptHandle>", StringComparison.Ordinal) + "<ReceiptHandle>".Length;
        var handle = received[start..received.IndexOf("</ReceiptHandle>", start, StringComparison.Ordinal)];
        await PostSqsQueryAsync($"Action=DeleteMessage&QueueUrl={Uri.EscapeDataString(_queryQueueUrl)}&ReceiptHandle={Uri.EscapeDataString(handle)}");
    }

    [Benchmark]
    public Task Sns_Publish() => _host.Sns.PublishAsync(new PublishRequest
    {
        TopicArn = _topicArn,
        Message = "hello from the benchmark",
        MessageAttributes = new Dictionary<string, Amazon.SimpleNotificationService.Model.MessageAttributeValue>
        {
            ["kind"] = new() { DataType = "String", StringValue = "bench" },
        },
    });

    [Benchmark]
    public Task EventBridge_PutEvents() => _host.EventBridge.PutEventsAsync(new PutEventsRequest
    {
        Entries = [new PutEventsRequestEntry { Source = "bench", DetailType = "Benchmarked", Detail = """{"n":1}""" }],
    });

    [Benchmark]
    public Task DynamoDb_PutItem() => _host.DynamoDb.PutItemAsync("bench", new Dictionary<string, AttributeValue>
    {
        ["pk"] = new() { S = "k" },
        ["value"] = new() { S = "hello from the benchmark" },
        ["n"] = new() { N = "42" },
    });

    [Benchmark]
    public Task DynamoDb_GetItem() => _host.DynamoDb.GetItemAsync("bench", new Dictionary<string, AttributeValue>
    {
        ["pk"] = new() { S = "k" },
    });

    private async Task<string> PostSqsQueryAsync(string form)
    {
        using var content = new StringContent($"{form}&Version=2012-11-05", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        using var response = await _host.SqsQuery.PostAsync(new Uri("/", UriKind.Relative), content);
        var body = await response.Content.ReadAsStringAsync();
        return response.StatusCode == HttpStatusCode.OK
            ? body
            : throw new InvalidOperationException($"SQS Query request failed with {response.StatusCode}: {body}");
    }
}
