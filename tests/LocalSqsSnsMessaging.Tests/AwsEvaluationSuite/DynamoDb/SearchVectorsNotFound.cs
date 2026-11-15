// Translated from behavioral/dynamodb/check_search_vectors_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.search-vectors-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class SearchVectorsNotFound
{
    private const string CheckId = "search-vectors-not-found";
    private const string ErrNotFound = "ResourceNotFoundException";

    private static async Task<(string? Code, string? Message)> ErrorCode(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected a ClientError, call unexpectedly succeeded");
        }

        return (error.Code, error.Message);
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

    /// <summary>
    /// SearchVectors on a nonexistent table raises ResourceNotFoundException ("Requested resource not found") instead
    /// of an empty result, while on a real table with an 8-dimension vector index it returns results.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbVectorSearch, "CreateTable ignores VectorIndexes, so DescribeTable never reports the index and the wait for it runs out")]
    [DisplayName("dynamodb.search-vectors-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        const string indexName = "vec-idx";
        var tableName = FidelityContext.UniqueName("sim2real-behav-svnf-");
        List<AttributeValue> Vector() => [.. Enumerable.Range(0, 8).Select(_ => N("0.0"))];

        var createError = await FidelityContext.CaptureError(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = indexName,
                    Dimensions = 8,
                    DistanceFunction = VectorDistanceFunction.EUCLIDEAN,
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "Embedding" },
                },
            ],
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
        var describedDims = described.VectorIndexes[0].Dimensions;
        Ensure(describedDims == 8, $"expected vector index dimensions 8, got {describedDims}");

        await ddb.PutItemAsync(tableName, Item(("pk", S("near")), ("Embedding", new AttributeValue { L = Vector() })));

        // Positive control: SearchVectors against the real table+index it just wrote to succeeds.
        var positive = await PollUntil(
            async () => (await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = tableName,
                IndexName = indexName,
                SearchVector = Vector(),
                TopK = 1,
            })).SearchResults ?? [],
            r => r.Count >= 1,
            timeoutS: 180,
            intervalS: 5);
        Ensure(positive.Count > 0, "positive control returned no results against a table it just wrote to");

        var missing = tableName + "-absent";
        var (code, message) = await ErrorCode(() => ddb.SearchVectorsAsync(new SearchVectorsRequest
        {
            TableName = missing,
            IndexName = indexName,
            SearchVector = Vector(),
            TopK = 1,
        }));
        Ensure(code == ErrNotFound, $"expected {ErrNotFound}, got {code}");
        Ensure(
            message == "Requested resource not found",
            $"expected AWS's exact ResourceNotFoundException message, got {Py.Repr(message)}");
    }
}
