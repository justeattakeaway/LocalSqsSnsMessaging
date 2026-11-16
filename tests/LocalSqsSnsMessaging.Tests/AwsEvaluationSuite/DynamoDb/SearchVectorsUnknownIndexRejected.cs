// Translated from behavioral/dynamodb/check_search_vectors_unknown_index_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.search-vectors-unknown-index-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SearchVectorsUnknownIndexRejected
{
    /// <summary>SearchVectors against an index the table does not have is refused, naming the index.</summary>
    [Test]
    [DisplayName("dynamodb.search-vectors-unknown-index-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = "cos",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "embedding" },
                    Dimensions = 3,
                    DistanceFunction = VectorDistanceFunction.COSINE,
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        // Fail FAST when CreateTable ignored `VectorIndexes` outright: an index that is absent
        // immediately after create is never going to appear. Poll only while the
        // index EXISTS but is still backfilling, which is the state a real create passes through.
        var described = (await ddb.DescribeTableAsync(table)).Table.VectorIndexes ?? [];
        Ensure(
            described.Count > 0,
            "CreateTable accepted VectorIndexes but DescribeTable reports none, so the vector index was silently dropped");
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(120) && described[0].IndexStatus?.Value != "ACTIVE")
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
            described = (await ddb.DescribeTableAsync(table)).Table.VectorIndexes ?? [];
            Ensure(described.Count > 0, "the vector index disappeared while it was backfilling");
        }

        Ensure(
            described[0].IndexStatus?.Value == "ACTIVE",
            $"the vector index never became ACTIVE: {Py.Repr(described[0].IndexStatus?.Value)}");

        var err = await FidelityContext.CaptureError(() => ddb.SearchVectorsAsync(new SearchVectorsRequest
        {
            TableName = table,
            IndexName = "nope",
            TopK = 1,
            SearchVector = [N("1"), N("0"), N("0")],
        }));
        if (err is null)
        {
            throw Fail("SearchVectors naming a nonexistent index: the call was ACCEPTED; real AWS rejects it");
        }

        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        const string expected = "The table does not have the specified index: nope";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
    }
}
