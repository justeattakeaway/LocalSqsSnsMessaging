// Translated from behavioral/dynamodb/check_put_item_vector_of_wrong_dimension_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-vector-of-wrong-dimension-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemVectorOfWrongDimensionRejected
{
    /// <summary>The error the call raised (<c>exc.response['Error']</c>); fails if it was accepted.</summary>
    private static async Task<AwsError> ErrorCode(Func<Task> call, string what)
    {
        var error = await FidelityContext.CaptureError(call);
        return error ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
    }

    /// <summary>
    /// A write whose embedding length contradicts the index dimensions is refused at PutItem, not
    /// accepted and silently left out of the index.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbVectorSearch, "Vector indexes are not emulated: CreateTable ignores VectorIndexes, so DescribeTable reports none")]
    [DisplayName("dynamodb.put-item-vector-of-wrong-dimension-rejected")]
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
                    DistanceFunction = "COSINE",
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
        while (clock.Elapsed.TotalSeconds < 120 && described[0].IndexStatus?.ToString() != "ACTIVE")
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
            described = (await ddb.DescribeTableAsync(table)).Table.VectorIndexes ?? [];
            Ensure(described.Count > 0, "the vector index disappeared while it was backfilling");
        }

        Ensure(
            described[0].IndexStatus?.ToString() == "ACTIVE",
            $"the vector index never became ACTIVE: {Py.Repr(described[0].IndexStatus?.ToString())}");

        var err = await ErrorCode(
            () => ddb.PutItemAsync(table, Item(("pk", S("bad")), ("embedding", L(N("1"), N("0"))))),
            "a PutItem whose embedding has the wrong dimension");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        var expected = "One or more parameter values were invalid. Invalid size for parameter embedding, "
            + "Expected: 3, Actual: 2 IndexName: cos";
        Ensure(err.Message == expected, $"unexpected message {Py.Repr(err.Message)}");
        Ensure(
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("bad"))), ConsistentRead = true })).Item is null,
            "the refused write must not create the item");

        // A vector list holding a non-number element.
        var err2 = await ErrorCode(
            () => ddb.PutItemAsync(table, Item(("pk", S("bad2")), ("embedding", L(N("1"), S("x"), N("0"))))),
            "a PutItem whose embedding has a non-number element");
        Ensure(err2.Code == "ValidationException", $"expected ValidationException, got {err2.Code}");
        var expected2 = "One or more parameter values were invalid. Invalid type for parameter embedding[1], "
            + "Expected: 32-bit floating point number, Actual: S. IndexName: cos";
        Ensure(err2.Message == expected2, $"unexpected message {Py.Repr(err2.Message)}");
        Ensure(
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("bad2"))), ConsistentRead = true })).Item is null,
            "the refused write must not create the item");

        // A vector attribute that is not a list at all.
        var err3 = await ErrorCode(
            () => ddb.PutItemAsync(table, Item(("pk", S("bad3")), ("embedding", S("not-a-vector")))),
            "a PutItem whose embedding is not a list");
        Ensure(err3.Code == "ValidationException", $"expected ValidationException, got {err3.Code}");
        var expected3 = "One or more parameter values were invalid. Invalid type for parameter embedding, "
            + "Expected: 32-bit floating point number list IndexName: cos";
        Ensure(err3.Message == expected3, $"unexpected message {Py.Repr(err3.Message)}");
        Ensure(
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("bad3"))), ConsistentRead = true })).Item is null,
            "the refused write must not create the item");
    }
}
