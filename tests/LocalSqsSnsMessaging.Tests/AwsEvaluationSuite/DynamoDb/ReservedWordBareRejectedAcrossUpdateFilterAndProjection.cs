// Translated from behavioral/dynamodb/check_reserved_word_bare_rejected_across_update_filter_and_projection.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.reserved-word-bare-rejected-across-update-filter-and-projection</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReservedWordBareRejectedAcrossUpdateFilterAndProjection
{
    private const string Check = "reserved-word-bare-rejected-across-update-filter-and-projection";
    private const string ErrValidation = "ValidationException";

    /// <summary>The (code, message) of the ClientError fn() raises. RAISES when fn() unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// A bare reserved word as an attribute name is rejected with ValidationException in Update,
    /// Filter and Projection expressions, each with its own message; a rejected update writes nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.reserved-word-bare-rejected-across-update-filter-and-projection")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-rsvd-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("rsvd-1")));
        var original = Item(("pk", S("rsvd-1")), ("status", S("active")), ("name", S("widget")), ("note", S("x")));
        await ddb.PutItemAsync(table, original);

        // --- UpdateExpression: bare `status` is rejected; the item is untouched. ---
        var (code, message) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET status = :v",
            ExpressionAttributeValues = Item((":v", S("changed"))),
        }));
        Ensure(code == ErrValidation, $"UpdateExpression bare reserved word answered {Py.Repr(code)} ({Py.Repr(message)})");
        const string expectedUpdate =
            "1 validation error detected: Invalid UpdateExpression: Attribute name is a reserved " +
            "keyword; reserved keyword: status";
        Ensure(message == expectedUpdate, $"expected message {Py.Repr(expectedUpdate)}, got {Py.Repr(message)}");
        var afterUpdate = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(afterUpdate, original),
            $"item after the rejected UpdateItem is {Py.Repr(afterUpdate)}, expected it unchanged at {Py.Repr(original)}");

        // --- FilterExpression: bare `name` is rejected. ---
        (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = "name = :v",
            ExpressionAttributeValues = Item((":v", S("widget"))),
        }));
        Ensure(code == ErrValidation, $"FilterExpression bare reserved word answered {Py.Repr(code)} ({Py.Repr(message)})");
        const string expectedFilter = "Invalid FilterExpression: Attribute name is a reserved keyword; reserved keyword: name";
        Ensure(message == expectedFilter, $"expected message {Py.Repr(expectedFilter)}, got {Py.Repr(message)}");

        // --- ProjectionExpression: bare `status` is rejected. ---
        (code, message) = await Error(() => ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ProjectionExpression = "status" }));
        Ensure(code == ErrValidation, $"ProjectionExpression bare reserved word answered {Py.Repr(code)} ({Py.Repr(message)})");
        const string expectedProjection = "Invalid ProjectionExpression: Attribute name is a reserved keyword; reserved keyword: status";
        Ensure(message == expectedProjection, $"expected message {Py.Repr(expectedProjection)}, got {Py.Repr(message)}");

        // Positive control: all three mechanisms work once the reserved word is aliased, so the
        // rejections above are attributable to the bare keyword, not to Update/Filter/Projection
        // being broken outright.
        var updated = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET #s = :v",
            ExpressionAttributeNames = Names(("#s", "status")),
            ExpressionAttributeValues = Item((":v", S("changed"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Py.Equal(updated.Attributes, Item(("status", S("changed")))),
            $"aliased UpdateExpression returned {Py.Repr(updated.Attributes)}, expected status=changed");
        var filtered = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = "#n = :v",
            ExpressionAttributeNames = Names(("#n", "name")),
            ExpressionAttributeValues = Item((":v", S("widget"))),
            ConsistentRead = true,
        });
        Ensure(filtered.Count == 1, $"aliased FilterExpression matched {Py.Repr(filtered.Count)}, expected 1");
        var projected = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Key(),
            ProjectionExpression = "#s",
            ExpressionAttributeNames = Names(("#s", "status")),
            ConsistentRead = true,
        });
        Ensure(
            Py.Equal(projected.Item, Item(("status", S("changed")))),
            $"aliased ProjectionExpression returned {Py.Repr(projected.Item)}, expected only status=changed");
    }
}
