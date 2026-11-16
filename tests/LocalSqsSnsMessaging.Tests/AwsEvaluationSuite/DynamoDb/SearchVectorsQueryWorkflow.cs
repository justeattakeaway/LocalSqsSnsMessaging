// Translated from behavioral/dynamodb/check_search_vectors_query_workflow.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.search-vectors-query-workflow</summary>
[Category("AwsEvaluationSuite")]
public sealed class SearchVectorsQueryWorkflow
{
    /// <summary>
    /// SearchVectors on a vector-enabled table returns the nearest-neighbor
    /// items to a query vector, ranked by increasing Euclidean distance, matching
    /// the vectors written via PutItem.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.search-vectors-query-workflow")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-vec-");
        const string indexName = "vec-idx";

        var createError = await FidelityContext.CaptureError(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = indexName,
                    Dimensions = 4,
                    DistanceFunction = VectorDistanceFunction.EUCLIDEAN,
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "Embedding" },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = "search-vectors-query-workflow" }],
        }));
        if (createError is not null)
        {
            throw Fail($"vector indexes not available in this account/region/SDK: {createError}");
        }

        ctx.OnCleanup(() => DeleteTableIgnore(ddb, tableName));

        var described = await PollUntil(
            async () => (await ddb.DescribeTableAsync(tableName)).Table,
            t => t.TableStatus == TableStatus.ACTIVE
                && t.VectorIndexes is { Count: > 0 }
                && t.VectorIndexes[0].IndexStatus == IndexStatus.ACTIVE,
            timeoutS: 300,
            intervalS: 5);
        var dims = described.VectorIndexes[0].Dimensions;
        Ensure(dims == 4, $"expected vector index dimensions 4, got {dims}");

        static AttributeValue Vec(params double[] values) =>
            new() { L = [.. values.Select(v => N(Py.Repr(v)))] };

        await ddb.PutItemAsync(tableName, Item(("pk", S("near")), ("Embedding", Vec(0.0, 0.0, 0.0, 0.0))));
        await ddb.PutItemAsync(tableName, Item(("pk", S("mid")), ("Embedding", Vec(5.0, 5.0, 5.0, 5.0))));
        await ddb.PutItemAsync(tableName, Item(("pk", S("far")), ("Embedding", Vec(100.0, 100.0, 100.0, 100.0))));

        async Task<List<SearchResultItem>> Search() =>
            (await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = tableName,
                IndexName = indexName,
                SearchVector = Vec(0.0, 0.0, 0.0, 0.0).L,
                TopK = 3,
            })).SearchResults ?? [];

        var results = await PollUntil(Search, r => r.Count == 3, timeoutS: 180, intervalS: 5);
        var orderedPks = results.Select(r => r.Item["pk"].S).ToList();
        Ensure(orderedPks.SequenceEqual(["near", "mid", "far"]), $"unexpected nearest-neighbor ordering: {Py.Repr(orderedPks)}");
        var scores = results.Select(r => r.Score ?? 0).ToList();
        Ensure(scores[0] == 0.0, $"expected the exact-match item's score to be 0.0, got {Py.Repr(scores[0])}");
        Ensure(scores[0] < scores[1] && scores[1] < scores[2], $"expected strictly increasing distance scores, got {Py.Repr(scores)}");

        await ddb.DeleteTableAsync(tableName);

        async Task<(string? Code, string? Message)> DescribeAfterDelete()
        {
            var (response, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(tableName));
            return error is null ? (response!.Table.TableStatus?.Value, null) : (error.Code, error.Message);
        }

        var (code, message) = await PollUntil(
            DescribeAfterDelete,
            r => r.Code == "ResourceNotFoundException",
            timeoutS: 180,
            intervalS: 5);
        Ensure(code == "ResourceNotFoundException", $"expected ResourceNotFoundException, got {code}");
        var expected = $"Requested resource not found: Table: {tableName} not found";
        Ensure(message == expected, $"unexpected DescribeTable message {Py.Repr(message)}");

        var putError = await FidelityContext.CaptureError(() => ddb.PutItemAsync(tableName, Item(("pk", S("after-delete")))));
        if (putError is null)
        {
            throw Fail("PutItem succeeded against a deleted table");
        }

        Ensure(
            putError.Code == "ResourceNotFoundException",
            $"expected PutItem on a deleted table to be rejected as ResourceNotFoundException, got {putError.Code}");
        var putMessage = putError.Message;
        Ensure(putMessage == "Requested resource not found", $"unexpected PutItem message {Py.Repr(putMessage)}");
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS)
    {
        var clock = FidelityTime.StartNew();
        T last = default!;
        object? lastRepr = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            lastRepr = last;
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"condition not met within {Py.Repr(timeoutS)}s, last value: {Py.Repr(lastRepr)}");
    }

    private static async Task DeleteTableIgnore(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
        {
            throw error.Exception;
        }
    }
}
