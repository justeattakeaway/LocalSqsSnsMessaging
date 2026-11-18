using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using BenchmarkDotNet.Attributes;

namespace InMemoryAws.Benchmarks;

/// <summary>
/// What a vector index costs: the same PutItem of an item carrying a vector, to a table with no
/// vector index and to one indexing that vector, and a SearchVectors over a populated index.
/// </summary>
[MemoryDiagnoser]
public class VectorBenchmarks
{
    private AmazonDynamoDBClient _client = null!;
    private Dictionary<string, AttributeValue> _item = null!;
    private List<AttributeValue> _query = null!;

    [Params(1536)]
    public int Dimensions { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var cloud = new InMemoryAwsCloud();
        _client = cloud.CreateDynamoDbClient();
        var random = new Random(42);
        List<AttributeValue> Vector() => [.. Enumerable.Range(0, Dimensions).Select(_ => new AttributeValue { N = (random.NextDouble() - 0.5).ToString("R", System.Globalization.CultureInfo.InvariantCulture) })];

        foreach (var (name, indexed) in new[] { ("plain", false), ("indexed", true) })
        {
            _client.CreateTableAsync(new CreateTableRequest
            {
                TableName = name,
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
                VectorIndexes = indexed
                    ?
                    [
                        new VectorIndex
                        {
                            IndexName = "vec",
                            VectorAttribute = new VectorAttributeDefinition { AttributeName = "v" },
                            Dimensions = Dimensions,
                            DistanceFunction = VectorDistanceFunction.COSINE,
                            Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                        },
                    ]
                    : null,
            }).GetAwaiter().GetResult();
        }

        for (var i = 0; i < 1000; i++)
        {
            _client.PutItemAsync("indexed", new Dictionary<string, AttributeValue> { ["pk"] = new($"seed-{i}"), ["v"] = new() { L = Vector() } }).GetAwaiter().GetResult();
        }

        _item = new Dictionary<string, AttributeValue> { ["pk"] = new("bench"), ["v"] = new() { L = Vector() } };
        _query = Vector();
    }

    [Benchmark(Baseline = true)]
    public Task<PutItemResponse> PutItem_NoVectorIndex() => _client.PutItemAsync("plain", _item);

    [Benchmark]
    public Task<PutItemResponse> PutItem_VectorIndex() => _client.PutItemAsync("indexed", _item);

    [Benchmark]
    public Task<SearchVectorsResponse> SearchVectors_1000Items_Top10() => _client.SearchVectorsAsync(new SearchVectorsRequest
    {
        TableName = "indexed",
        IndexName = "vec",
        SearchVector = _query,
        TopK = 10,
    });
}
