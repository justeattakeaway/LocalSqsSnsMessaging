// Translated from behavioral/dynamodb/check_update_item_without_actions_upserts_key_only_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-without-actions-upserts-key-only-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemWithoutActionsUpsertsKeyOnlyItem
{
    /// <summary>
    /// UpdateItem with only a Key and no update actions creates a key-only item for a new key,
    /// leaves an existing item unchanged, and returns no Attributes in either case.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateItem.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-without-actions-upserts-key-only-item")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-noact-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        Ensure(await Get(ddb, table, "fresh") is null, "the new key already holds an item before the update");
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest { TableName = table, Key = Item(("pk", S("fresh"))) });
        Ensure(resp.Attributes is null, $"action-free UpdateItem on a new key returned {Py.Repr(resp.Attributes)}");
        var created = await Get(ddb, table, "fresh");
        Ensure(
            Py.Equal(created, Item(("pk", S("fresh")))),
            $"action-free UpdateItem on a new key must create a key-only item, got {Py.Repr(created)}");

        var seeded = Item(("pk", S("existing")), ("v", S("keep")), ("n", N("7")));
        await ddb.PutItemAsync(table, seeded);
        resp = await ddb.UpdateItemAsync(new UpdateItemRequest { TableName = table, Key = Item(("pk", S("existing"))) });
        Ensure(
            resp.Attributes is null,
            $"action-free UpdateItem on an existing item returned {Py.Repr(resp.Attributes)}");
        var after = await Get(ddb, table, "existing");
        Ensure(Py.Equal(after, seeded), $"action-free UpdateItem changed the existing item to {Py.Repr(after)}");

        var count = (await ddb.ScanAsync(new ScanRequest { TableName = table, Select = Select.COUNT, ConsistentRead = true })).Count;
        Ensure(count == 2, $"expected exactly the two items, the table holds {count}");
    }

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            try
            {
                await undo();
                return;
            }
            catch (AmazonDynamoDBException ex)
            {
                if (notFound.Contains(ex.ErrorCode, StringComparer.Ordinal))
                {
                    return;
                }

                if (ex.ErrorCode != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(300))
                {
                    throw;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0));
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
}
