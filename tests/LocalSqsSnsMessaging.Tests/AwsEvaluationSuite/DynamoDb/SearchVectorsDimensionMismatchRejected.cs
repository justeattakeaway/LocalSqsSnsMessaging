// Translated from behavioral/dynamodb/check_search_vectors_dimension_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.search-vectors-dimension-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SearchVectorsDimensionMismatchRejected
{
    private static async Task<AwsError> ErrorCode(Func<Task> call, string what)
    {
        var error = await FidelityContext.CaptureError(call);
        return error ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
    }

    /// <summary>
    /// A search vector whose length differs from the index dimensions is refused, and the message
    /// states both dimensions so a caller can fix the call.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.search-vectors-dimension-mismatch-rejected")]
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
        while (clock.Elapsed < TimeSpan.FromSeconds(120) && described[0].IndexStatus != IndexStatus.ACTIVE)
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
            described = (await ddb.DescribeTableAsync(table)).Table.VectorIndexes ?? [];
            Ensure(described.Count > 0, "the vector index disappeared while it was backfilling");
        }

        Ensure(described[0].IndexStatus == IndexStatus.ACTIVE, $"the vector index never became ACTIVE: {Py.Repr(described[0].IndexStatus?.Value)}");
        var err = await ErrorCode(
            () => ddb.SearchVectorsAsync(new SearchVectorsRequest { TableName = table, IndexName = "cos", TopK = 1, SearchVector = [N("1")] }),
            "a SearchVectors whose vector has the wrong dimension");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        const string expected = "Input search vector dimension 1 does not match vector index dimension 3";
        Ensure(err.Message == expected, $"the message must state both dimensions, got {Py.Repr(err.Message)}");
    }
}
