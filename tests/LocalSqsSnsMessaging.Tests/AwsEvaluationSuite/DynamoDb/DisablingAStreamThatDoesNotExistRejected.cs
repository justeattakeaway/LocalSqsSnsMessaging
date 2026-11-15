// Translated from behavioral/dynamodb/check_disabling_a_stream_that_does_not_exist_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.disabling-a-stream-that-does-not-exist-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DisablingAStreamThatDoesNotExistRejected
{
    /// <summary>
    /// Disabling a stream on a table that has none is a ValidationException rather than a silent
    /// success -- the no-op reading is the over-permissive one.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.disabling-a-stream-that-does-not-exist-rejected")]
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
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var err = await Error(
            () => ddb.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = table,
                StreamSpecification = new StreamSpecification { StreamEnabled = false },
            }),
            "disabling a stream on a table with no stream");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code ?? "None"}");
        var expectedMessage = $"Table has no stream to disable: TableName: {table}";
        Ensure(err.Message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(err.Message)}");
    }

    /// <summary>The Error dict from a call that MUST be refused.</summary>
    private static async Task<AwsError> Error(Func<Task> call, string what) =>
        await FidelityContext.CaptureError(call) ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
}
