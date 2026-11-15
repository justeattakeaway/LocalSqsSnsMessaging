// Translated from behavioral/dynamodb/check_get_item_unaliased_reserved_word_in_a_projection_path_segment_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.get-item-unaliased-reserved-word-in-a-projection-path-segment-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetItemUnaliasedReservedWordInAProjectionPathSegmentRejected
{
    private const string CheckId = "get-item-unaliased-reserved-word-in-a-projection-path-segment-rejected";

    /// <summary>
    /// GetItem's ProjectionExpression rejects a reserved word used bare inside a nested path
    /// segment with ValidationException naming the keyword; aliased, the path resolves to the
    /// nested value.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.get-item-unaliased-reserved-word-in-a-projection-path-segment-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbresv-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        static Dictionary<string, AttributeValue> Key() => Item(("pk", S("rec1")));
        static AttributeValue InfoMap() => M(("status", S("ok")));
        await ddb.PutItemAsync(table, Item(("pk", S("rec1")), ("info", InfoMap())));

        var (code, message) = await Error(() => ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Key(),
            ConsistentRead = true,
            ProjectionExpression = "info.status",
        }));
        Ensure(
            code == "ValidationException",
            $"an unaliased reserved word inside a projection path segment raised {Py.Repr(code)}, expected 'ValidationException'");
        const string expected = "Invalid ProjectionExpression: Attribute name is a reserved keyword; reserved keyword: status";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        // Aliased: the placeholder resolves "status" inside the "info" map without complaint.
        var ok = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Key(),
            ConsistentRead = true,
            ProjectionExpression = "info.#s",
            ExpressionAttributeNames = Names(("#s", "status")),
        });
        Ensure(
            Py.Equal(ok.Item, Item(("info", InfoMap()))),
            $"aliased PE 'info.#s' returned {Py.Repr(ok.Item)}, expected the whole info map {{'info': {Py.Repr(InfoMap())}}}");
    }

    /// <summary>The (Error.Code, Error.Message) fn() raises. RAISES when fn() unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"the call SUCCEEDED and returned {Py.Repr(result)}; real AWS rejects it");
        }

        return (error.Code, error.Message);
    }
}
