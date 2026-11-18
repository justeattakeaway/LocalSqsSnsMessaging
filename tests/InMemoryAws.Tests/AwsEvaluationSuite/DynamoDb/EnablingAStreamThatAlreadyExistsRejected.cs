// Translated from behavioral/dynamodb/check_enabling_a_stream_that_already_exists_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.enabling-a-stream-that-already-exists-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class EnablingAStreamThatAlreadyExistsRejected
{
    /// <summary>
    /// Enabling a stream on a table that already has one is a ValidationException, and the existing
    /// stream is left in place with its descriptor unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.enabling-a-stream-that-already-exists-rejected")]
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
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_IMAGE },
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var before = (await ddb.DescribeTableAsync(table)).Table.LatestStreamArn;
        var err = await Error(
            () => ddb.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = table,
                StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_IMAGE },
            }),
            "enabling a stream that is already enabled");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code ?? "None"}");
        var expectedMessage = $"Table already has an enabled stream: TableName: {table}";
        Ensure(err.Message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(err.Message)}");
        var after = (await ddb.DescribeTableAsync(table)).Table.LatestStreamArn;
        Ensure(after == before, $"the existing stream must be untouched: {before} -> {after}");
    }

    /// <summary>The Error dict from a call that MUST be refused.</summary>
    private static async Task<AwsError> Error(Func<Task> call, string what) =>
        await FidelityContext.CaptureError(call) ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
}
