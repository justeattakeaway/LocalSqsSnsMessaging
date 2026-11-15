// Translated from behavioral/dynamodb/check_transact_write_condition_check_and_conditional_put_billing.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-condition-check-and-conditional-put-billing</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteConditionCheckAndConditionalPutBilling
{
    /// <summary>
    /// In TransactWriteItems a Put whose condition passes costs the same 2 write units as an
    /// unconditional Put, and a ConditionCheck on its own table bills 2 write units, no read units.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/transaction-apis.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-condition-check-and-conditional-put-billing")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableCheck = await CreateTable(ctx, ddb, "sim2real-behav-ddb-txccb-chk-");
        var tablePlain = await CreateTable(ctx, ddb, "sim2real-behav-ddb-txccb-pln-");
        var tableCond = await CreateTable(ctx, ddb, "sim2real-behav-ddb-txccb-cnd-");

        Dictionary<string, AttributeValue> CheckedKey() => Item(("pk", S("watched")));
        await ddb.PutItemAsync(tableCheck, Item(("pk", S("watched")), ("status", S("ok"))));

        var resp = await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = tableCheck,
                        Key = CheckedKey(),
                        ConditionExpression = "#s = :ok",
                        ExpressionAttributeNames = Names(("#s", "status")),
                        ExpressionAttributeValues = Item((":ok", S("ok"))),
                    },
                },
                new TransactWriteItem { Put = new Put { TableName = tablePlain, Item = Item(("pk", S("row")), ("v", S("x"))) } },
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = tableCond,
                        Item = Item(("pk", S("row")), ("v", S("x"))),
                        ConditionExpression = "attribute_not_exists(pk)",
                    },
                },
            ],
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        var consumed = (resp.ConsumedCapacity ?? []).Select(Capacity).ToList();
        Ensure(consumed.Count == 3, $"expected one ConsumedCapacity entry per table, got {Py.Repr(consumed)}");

        var plainCap = Arm(consumed, tablePlain);
        var condCap = Arm(consumed, tableCond);
        var checkCap = Arm(consumed, tableCheck);

        Ensure(
            Py.Equal((plainCap.GetValueOrDefault("CapacityUnits"), plainCap.GetValueOrDefault("WriteCapacityUnits")), (2.0, 2.0)),
            $"the unconditional Put consumed {Py.Repr(plainCap)}, expected 2.0 write units");
        Ensure(!plainCap.ContainsKey("ReadCapacityUnits"), $"an unconditional Put reported read units: {Py.Repr(plainCap)}");

        Ensure(
            Py.Equal((condCap.GetValueOrDefault("CapacityUnits"), condCap.GetValueOrDefault("WriteCapacityUnits")), (2.0, 2.0)),
            $"the conditional Put consumed {Py.Repr(condCap)}, expected the same 2.0 write units as unconditional");
        Ensure(!condCap.ContainsKey("ReadCapacityUnits"), $"a passing conditional Put reported read units: {Py.Repr(condCap)}");
        var condBilling = condCap.Where(e => e.Key != "TableName").ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        var plainBilling = plainCap.Where(e => e.Key != "TableName").ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        Ensure(
            Py.Equal(condBilling, plainBilling),
            $"a passing condition changed what the Put billed: conditional {Py.Repr(condBilling)} vs plain {Py.Repr(plainBilling)}");

        Ensure(
            Py.Equal((checkCap.GetValueOrDefault("CapacityUnits"), checkCap.GetValueOrDefault("WriteCapacityUnits")), (2.0, 2.0)),
            $"the ConditionCheck consumed {Py.Repr(checkCap)}, expected 2.0 write units");
        Ensure(!checkCap.ContainsKey("ReadCapacityUnits"), $"a ConditionCheck reported read units: {Py.Repr(checkCap)}");
        Ensure(
            Py.Equal(checkCap.GetValueOrDefault("Table"), new Dictionary<string, object?> { ["CapacityUnits"] = 2.0, ["WriteCapacityUnits"] = 2.0 }),
            $"the ConditionCheck's table breakdown was {Py.Repr(checkCap.GetValueOrDefault("Table"))}");

        var plainItem = await Get(ddb, tablePlain, Item(("pk", S("row"))));
        Ensure(Py.Equal(plainItem, Item(("pk", S("row")), ("v", S("x")))), $"the unconditional Put stored {Py.Repr(plainItem)}");
        var condItem = await Get(ddb, tableCond, Item(("pk", S("row"))));
        Ensure(Py.Equal(condItem, Item(("pk", S("row")), ("v", S("x")))), $"the conditional Put stored {Py.Repr(condItem)}");
        var checkedItem = await Get(ddb, tableCheck, CheckedKey());
        Ensure(
            Py.Equal(checkedItem, Item(("pk", S("watched")), ("status", S("ok")))),
            $"the ConditionCheck's own item changed to {Py.Repr(checkedItem)}");
    }

    /// <summary>The single ConsumedCapacity entry for <paramref name="table"/> in a TransactWriteItems response.</summary>
    private static Dictionary<string, object?> Arm(List<Dictionary<string, object?>> consumed, string table)
    {
        var matches = consumed.Where(c => c.GetValueOrDefault("TableName") as string == table).ToList();
        Ensure(matches.Count == 1, $"expected exactly one ConsumedCapacity entry for {Py.Repr(table)}, got {Py.Repr(consumed)}");
        return matches[0];
    }

    /// <summary>A ConsumedCapacity entry as boto3 hands it back.</summary>
    private static Dictionary<string, object?> Capacity(ConsumedCapacity c)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (c.TableName is not null)
        {
            result["TableName"] = c.TableName;
        }

        if (c.CapacityUnits is not null)
        {
            result["CapacityUnits"] = c.CapacityUnits.Value;
        }

        if (c.ReadCapacityUnits is not null)
        {
            result["ReadCapacityUnits"] = c.ReadCapacityUnits.Value;
        }

        if (c.WriteCapacityUnits is not null)
        {
            result["WriteCapacityUnits"] = c.WriteCapacityUnits.Value;
        }

        if (c.Table is not null)
        {
            var arm = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (c.Table.ReadCapacityUnits is not null)
            {
                arm["ReadCapacityUnits"] = c.Table.ReadCapacityUnits.Value;
            }

            if (c.Table.WriteCapacityUnits is not null)
            {
                arm["WriteCapacityUnits"] = c.Table.WriteCapacityUnits.Value;
            }

            if (c.Table.CapacityUnits is not null)
            {
                arm["CapacityUnits"] = c.Table.CapacityUnits.Value;
            }

            result["Table"] = arm;
        }

        return result;
    }

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string[] notFound, double timeoutSeconds = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var clock = FidelityTime.StartNew();
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc?.TableStatus}");
    }

    /// <summary>Create an on-demand table keyed pk (S), with no index; wait ACTIVE.</summary>
    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb, string prefix)
    {
        var table = FidelityContext.UniqueName(prefix);
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, ctx, table);
        return table;
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
}
