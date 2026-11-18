using System.Net;
using System.Net.Sockets;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using InMemoryAws.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace InMemoryAws.Tests.Server;

/// <summary>DynamoDB through the real server, over HTTP, with a real <see cref="AmazonDynamoDBClient"/>.</summary>
public sealed class DynamoDbServerTests : IAsyncDisposable
{
    private WebApplication? _app;
    private int _port;

    [Before(Test)]
    public async Task Setup()
    {
        _port = GetAvailablePort();
        var registry = new CloudRegistry("000000000000", "us-east-1", new Uri($"http://localhost:{_port}"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = ["--hostBuilder:reloadConfigOnChange=false"] });
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(_port));
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        _app = builder.Build();
        var middleware = new AwsBridgeMiddleware(registry);
        _app.Map("{**path}", middleware.InvokeAsync);
        await _app.StartAsync();
    }

    [After(Test)]
    public async Task Cleanup()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public async ValueTask DisposeAsync() => await Cleanup();

    private AmazonDynamoDBClient Client(string accessKey = "000000000000", string region = "us-east-1") =>
        new(new BasicAWSCredentials(accessKey, "fake"), new AmazonDynamoDBConfig
        {
            ServiceURL = $"http://localhost:{_port}",
            AuthenticationRegion = region,
            MaxErrorRetry = 0,
        });

    [Test]
    public async Task CreatePutQueryAndTransact()
    {
        using var client = Client();
        await client.CreateTableAsync(new CreateTableRequest
        {
            TableName = "orders",
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });

        for (var i = 1; i <= 3; i++)
        {
            await client.PutItemAsync("orders", new Dictionary<string, AttributeValue> { ["pk"] = new("c1"), ["sk"] = new() { N = i.ToString(System.Globalization.CultureInfo.InvariantCulture) } });
        }

        var query = await client.QueryAsync(new QueryRequest
        {
            TableName = "orders",
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":pk"] = new("c1") },
            ScanIndexForward = false,
        });
        query.Items.Select(i => i["sk"].N).ShouldBe(["3", "2", "1"]);

        var cancelled = await Should.ThrowAsync<TransactionCanceledException>(() => client.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Put = new Put { TableName = "orders", Item = new Dictionary<string, AttributeValue> { ["pk"] = new("c2"), ["sk"] = new() { N = "1" } } } },
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = "orders",
                        Key = new Dictionary<string, AttributeValue> { ["pk"] = new("c1"), ["sk"] = new() { N = "1" } },
                        ConditionExpression = "attribute_not_exists(pk)",
                    },
                },
            ],
        }));
        cancelled.CancellationReasons.Select(r => r.Code).ShouldBe(["None", "ConditionalCheckFailed"]);
        (await client.GetItemAsync("orders", new Dictionary<string, AttributeValue> { ["pk"] = new("c2"), ["sk"] = new() { N = "1" } })).IsItemSet.ShouldBeFalse();
    }

    [Test]
    public async Task EachAccountAndRegionHasItsOwnTables()
    {
        using var account1 = Client("111111111111");
        using var account2 = Client("222222222222");
        using var otherRegion = Client("111111111111", "eu-west-1");
        await account1.CreateTableAsync(new CreateTableRequest
        {
            TableName = "tbl",
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });

        (await account1.ListTablesAsync(new ListTablesRequest())).TableNames.ShouldBe(["tbl"]);
        (await account2.ListTablesAsync(new ListTablesRequest())).TableNames.ShouldBeEmpty();
        (await otherRegion.ListTablesAsync(new ListTablesRequest())).TableNames.ShouldBeEmpty();
        (await account1.DescribeTableAsync("tbl")).Table.TableArn.ShouldBe("arn:aws:dynamodb:us-east-1:111111111111:table/tbl");
    }

    [Test]
    public async Task SearchVectorsReachesTheServerInTheSignedRegion()
    {
        // The SDK sends SearchVectors to a search endpoint of its own unless ServiceURL is set; with
        // it, the request still comes here, and the signed region picks the tables.
        using var client = Client(region: "eu-west-1");
        await client.CreateTableAsync(new CreateTableRequest
        {
            TableName = "docs",
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = "vec",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "v" },
                    Dimensions = 2,
                    DistanceFunction = VectorDistanceFunction.DOT_PRODUCT,
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
        });
        await client.PutItemAsync("docs", new Dictionary<string, AttributeValue> { ["pk"] = new("a"), ["v"] = new() { L = [new() { N = "1" }, new() { N = "2" }] } });
        await client.PutItemAsync("docs", new Dictionary<string, AttributeValue> { ["pk"] = new("b"), ["v"] = new() { L = [new() { N = "3" }, new() { N = "0" }] } });

        var response = await client.SearchVectorsAsync(new SearchVectorsRequest
        {
            TableName = "docs",
            IndexName = "vec",
            SearchVector = [new() { N = "1" }, new() { N = "1" }],
            TopK = 2,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });

        response.SearchResults.Select(r => (r.Item["pk"].S, r.Score)).ShouldBe([("a", (double?)3.0), ("b", 3.0)]);
        response.ConsumedCapacity.VectorSearchRequestBytes.ShouldBe(1024);
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
