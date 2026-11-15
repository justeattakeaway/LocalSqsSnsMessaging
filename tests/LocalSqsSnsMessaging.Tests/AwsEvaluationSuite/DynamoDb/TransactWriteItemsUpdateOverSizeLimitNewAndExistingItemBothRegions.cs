// Translated from behavioral/dynamodb/check_transact_write_items_update_over_size_limit_new_and_existing_item_both_regions.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-update-over-size-limit-new-and-existing-item-both-regions</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsUpdateOverSizeLimitNewAndExistingItemBothRegions
{
    private const string Check = "transact-write-items-update-over-size-limit-new-and-existing-item-both-regions";
    private const int LimitBytes = 409600;
    private static readonly (string Code, string Message) OversizedUpdate = ("ValidationException", "Item size to update has exceeded the maximum allowed size");

    // region-pinned: the claim is that AWS answers this one way in both of these Regions, so they
    // are named directly rather than derived from the target.
    private static readonly string[] Regions = ["us-west-2", "us-east-1"];

    /// <summary>Assert <c>fn()</c> is refused with exactly (code, message); a success raises too.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, err) = await FidelityContext.Capture(fn);
        if (err is not null)
        {
            Ensure(err.Code == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(err.Code)}: {Py.Repr(err.Message)}");
            Ensure(err.Message == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(err.Message)}");
            return;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }

    /// <summary>A string value that puts the item exactly one byte over LIMIT_BYTES for pk=key.</summary>
    private static string OversizedValue(string key)
    {
        var overhead = 2 + Encoding.UTF8.GetByteCount(key) + 4;
        return new string('g', LimitBytes + 1 - overhead);
    }

    private static async Task WaitActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 120)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var status = (await ddb.DescribeTableAsync(tableName)).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return;
            }

            Ensure(clock.Elapsed.TotalSeconds < timeoutS, $"table {Py.Repr(tableName)} still {Py.Repr(status)} after {timeoutS}s");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static Task<TransactWriteItemsResponse> Update(IAmazonDynamoDB ddb, string tableName, string key, string value) =>
        ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = tableName,
                        Key = Item(("pk", S(key))),
                        UpdateExpression = "SET #d = :v",
                        ExpressionAttributeNames = Names(("#d", "data")),
                        ExpressionAttributeValues = Item((":v", S(value))),
                    },
                },
            ],
        });

    /// <summary>Run the positive control, the existing-item grow and the fresh-item upsert in one region.</summary>
    private static async Task ProbeRegion(IAmazonDynamoDB ddb, string tableName)
    {
        await ddb.PutItemAsync(tableName, Item(("pk", S("control")), ("data", S("start"))));
        await Update(ddb, tableName, "control", "grown-but-still-small");
        var controlGot = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("control"))), ConsistentRead = true });
        Ensure(
            controlGot.Item?.GetValueOrDefault("data")?.S == "grown-but-still-small",
            $"positive control TransactWriteItems Update failed, got {Py.Repr(controlGot.Item)}");

        var original = new string('o', 1024);
        await ddb.PutItemAsync(tableName, Item(("pk", S("existing")), ("data", S(original))));
        await ExpectError(() => Update(ddb, tableName, "existing", OversizedValue("existing")), OversizedUpdate);
        var existingAfter = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("existing"))), ConsistentRead = true });
        Ensure(
            existingAfter.Item?.GetValueOrDefault("data")?.S == original,
            $"a refused TransactWriteItems Update must leave the existing item unchanged, got {Py.Repr(existingAfter.Item)}");

        await ExpectError(() => Update(ddb, tableName, "fresh", OversizedValue("fresh")), OversizedUpdate);
        var freshAfter = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("fresh"))), ConsistentRead = true });
        Ensure(
            freshAfter.Item is null,
            $"a refused TransactWriteItems Update on a key that never existed must not create it, got {Py.Repr(freshAfter.Item)}");
    }

    /// <summary>
    /// A TransactWriteItems Update that would grow an item one byte past the 409600-byte size
    /// limit is refused as a top-level ValidationException, never a TransactionCanceledException,
    /// for an existing item and for a key that never existed, the same way in us-west-2 and us-east-1.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-update-over-size-limit-new-and-existing-item-both-regions")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        foreach (var region in Regions)
        {
            var ddb = ctx.DynamoClient(region: region);
            var tableName = FidelityContext.UniqueName("sim2real-behav-ddbsz-");
            await ddb.CreateTableAsync(new CreateTableRequest
            {
                TableName = tableName,
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
                Tags = [new Tag { Key = "sim2real", Value = Check }],
            });
            ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
            await WaitActive(ddb, tableName);
            await ProbeRegion(ddb, tableName);
        }
    }
}
