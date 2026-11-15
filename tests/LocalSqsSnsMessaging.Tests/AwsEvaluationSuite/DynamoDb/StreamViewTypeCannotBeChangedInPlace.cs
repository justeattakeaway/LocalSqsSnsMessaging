// Translated from behavioral/dynamodb/check_stream_view_type_cannot_be_changed_in_place.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.stream-view-type-cannot-be-changed-in-place</summary>
[Category("AwsEvaluationSuite")]
public sealed class StreamViewTypeCannotBeChangedInPlace
{
    /// <summary>The error from a call that MUST be refused.</summary>
    private static async Task<AwsError> Error(Func<Task> call, string what) =>
        await FidelityContext.CaptureError(call) ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");

    /// <summary>
    /// A stream's StreamViewType is fixed once created: changing it requires disabling and recreating,
    /// so an in-place change is refused and the original view type survives.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.stream-view-type-cannot-be-changed-in-place")]
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

        var err = await Error(
            () => ddb.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = table,
                StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_AND_OLD_IMAGES },
            }),
            "changing StreamViewType in place");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        var expected = $"Table already has an enabled stream: TableName: {table}";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
        var spec = (await ddb.DescribeTableAsync(table)).Table.StreamSpecification;
        Ensure(
            spec.StreamViewType?.Value == "NEW_IMAGE",
            $"the original view type must survive the refusal, got {{'StreamEnabled': {Py.Repr(spec.StreamEnabled)}, 'StreamViewType': {Py.Repr(spec.StreamViewType?.Value)}}}");
    }
}
