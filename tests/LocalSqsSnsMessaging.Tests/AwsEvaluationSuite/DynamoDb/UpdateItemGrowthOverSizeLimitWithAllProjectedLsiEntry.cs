// Translated from behavioral/dynamodb/check_update_item_growth_over_size_limit_with_all_projected_lsi_entry.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-growth-over-size-limit-with-all-projected-lsi-entry</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemGrowthOverSizeLimitWithAllProjectedLsiEntry
{
    private const string CheckId = "update-item-growth-over-size-limit-with-all-projected-lsi-entry";
    private const int GrownBytes = 389800;
    private static readonly (string Code, string Message) OverLimit = ("ValidationException", "Item size to update has exceeded the maximum allowed size");

    /// <summary>
    /// UpdateItem SET growth is sized against the item plus its ALL-projected local secondary
    /// index entry combined: growing to ~390 KB succeeds with no local secondary index but is
    /// refused ValidationException with one, leaving the pre-growth item unchanged.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbDetail, "The item size limit ignores the ALL-projected local secondary index entry, so the ~390 KB growth is accepted")]
    [DisplayName("dynamodb.update-item-growth-over-size-limit-with-all-projected-lsi-entry")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var plainTable = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = plainTable,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.N),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(plainTable));

        var lsiTable = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = lsiTable,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.N),
                new AttributeDefinition("li", ScalarAttributeType.S),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("li", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(lsiTable));

        await ddb.WaitForTableExists(plainTable, delaySeconds: 2, maxAttempts: 90);
        await ddb.WaitForTableExists(lsiTable, delaySeconds: 2, maxAttempts: 90);

        var grownValue = new string('y', GrownBytes);

        // Positive control: growing a small item to ~390 KB is under the ceiling on its own and is
        // accepted and reads back whole on a table with no local secondary index.
        await ddb.PutItemAsync(plainTable, Item(("pk", S("upd-plain")), ("sk", N("1"))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = plainTable,
            Key = Item(("pk", S("upd-plain")), ("sk", N("1"))),
            UpdateExpression = "SET #d = :v",
            ExpressionAttributeNames = Names(("#d", "data")),
            ExpressionAttributeValues = Item((":v", S(grownValue))),
        });
        var got = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = plainTable,
            Key = Item(("pk", S("upd-plain")), ("sk", N("1"))),
            ConsistentRead = true,
        })).Item ?? [];
        Ensure(
            got.GetValueOrDefault("data")?.S == grownValue,
            $"growing to {GrownBytes} bytes must round-trip whole through UpdateItem, " +
            $"got {(got.GetValueOrDefault("data")?.S ?? "").Length} bytes back");

        // UpdateItem: the same small item, now on a table whose ALL-projected local secondary index
        // mirrors it, starts with an index entry already in place; growing it the same way is refused
        // because the combined (base item + index entry) size would exceed the 400 KB ceiling.
        await ddb.PutItemAsync(lsiTable, Item(("pk", S("upd-lsi")), ("sk", N("1")), ("li", S("idx"))));
        await ExpectError(
            () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = lsiTable,
                Key = Item(("pk", S("upd-lsi")), ("sk", N("1"))),
                UpdateExpression = "SET #d = :v",
                ExpressionAttributeNames = Names(("#d", "data")),
                ExpressionAttributeValues = Item((":v", S(grownValue))),
            }),
            OverLimit);
        var unchanged = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = lsiTable,
            Key = Item(("pk", S("upd-lsi")), ("sk", N("1"))),
            ConsistentRead = true,
        })).Item ?? [];
        Ensure(
            !unchanged.ContainsKey("data"),
            $"the refused growth must leave the pre-growth item unchanged, got {Py.Repr(unchanged.Keys.Order(StringComparer.Ordinal).ToList())}");
        Ensure(
            unchanged.GetValueOrDefault("li")?.S == "idx",
            $"the pre-growth item must still be intact, got li={Py.Repr(unchanged.GetValueOrDefault("li"))}");
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
        }

        Ensure(error.Code == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(error.Code)} ({Py.Repr(error.Message)})");
        Ensure(error.Message == expected.Message, $"unexpected message {Py.Repr(error.Message)}");
    }
}
