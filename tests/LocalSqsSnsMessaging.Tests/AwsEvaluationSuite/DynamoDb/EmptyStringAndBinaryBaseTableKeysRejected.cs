// Translated from behavioral/dynamodb/check_empty_string_and_binary_base_table_keys_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.empty-string-and-binary-base-table-keys-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class EmptyStringAndBinaryBaseTableKeysRejected
{
    private const string EmptySMsg =
        "One or more parameter values are not valid. The AttributeValue for a key attribute cannot "
        + "contain an empty string value. Key: pk";

    private const string EmptyBMsg =
        "One or more parameter values are not valid. The AttributeValue for a key attribute cannot "
        + "contain an empty binary value. Key: pk";

    /// <summary>
    /// An empty S or B base-table partition key is refused by PutItem, GetItem, DeleteItem,
    /// UpdateItem and both BatchWriteItem request kinds, naming the key; nothing is written.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/HowItWorks.NamingRulesDataTypes.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.empty-string-and-binary-base-table-keys-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var sTable = await Create(ctx, ddb, "S");
        var bTable = await Create(ctx, ddb, "B");
        await WaitActive(ddb, ctx, sTable);
        await WaitActive(ddb, ctx, bTable);

        // Positive controls: non-empty keys of both types round-trip.
        await ddb.PutItemAsync(sTable, Item(("pk", S("ok")), ("v", S("s"))));
        await ddb.PutItemAsync(bTable, Item(("pk", B("ok")), ("v", S("b"))));
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = sTable, Key = Item(("pk", S("ok"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(got, Item(("pk", S("ok")), ("v", S("s")))), $"S-keyed control item read back as {Py.Repr(got)}");
        got = (await ddb.GetItemAsync(new GetItemRequest { TableName = bTable, Key = Item(("pk", B("ok"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(got, Item(("pk", B("ok")), ("v", S("b")))), $"B-keyed control item read back as {Py.Repr(got)}");

        (string Table, Func<AttributeValue> Empty, string Msg)[] shapes =
        [
            (sTable, () => S(""), EmptySMsg),
            (bTable, () => B(Array.Empty<byte>()), EmptyBMsg),
        ];
        var good = new Dictionary<string, Func<Dictionary<string, AttributeValue>>>(StringComparer.Ordinal)
        {
            [sTable] = () => Item(("pk", S("batch-good"))),
            [bTable] = () => Item(("pk", B("batch-good"))),
        };

        var arms = new List<(string Label, string Msg, Func<Task> Call)>();
        foreach (var (table, empty, msg) in shapes)
        {
            var label = Py.Repr(empty());
            arms.Add(($"PutItem {label}", msg, () => ddb.PutItemAsync(table, Item(("pk", empty()), ("v", S("x"))))));
            arms.Add(($"GetItem {label}", msg, () => ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", empty())) })));
            arms.Add(($"DeleteItem {label}", msg, () => ddb.DeleteItemAsync(new DeleteItemRequest { TableName = table, Key = Item(("pk", empty())) })));
            arms.Add((
                $"UpdateItem {label}",
                msg,
                () => ddb.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = table,
                    Key = Item(("pk", empty())),
                    UpdateExpression = "SET v = :v",
                    ExpressionAttributeValues = Item((":v", S("x"))),
                })));
        }

        foreach (var (table, empty, msg) in shapes)
        {
            var label = Py.Repr(empty());
            arms.Add((
                $"BatchWriteItem PutRequest {label}",
                msg,
                () => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
                {
                    RequestItems = new()
                    {
                        [table] =
                        [
                            new WriteRequest { PutRequest = new PutRequest { Item = good[table]() } },
                            new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", empty()), ("v", S("x"))) } },
                        ],
                    },
                })));
            arms.Add((
                $"BatchWriteItem DeleteRequest {label}",
                msg,
                () => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
                {
                    RequestItems = new()
                    {
                        [table] =
                        [
                            new WriteRequest { PutRequest = new PutRequest { Item = good[table]() } },
                            new WriteRequest { DeleteRequest = new DeleteRequest { Key = Item(("pk", empty())) } },
                        ],
                    },
                })));
        }

        foreach (var (label, msg, call) in arms)
        {
            var (code, gotMsg) = await Error(call);
            Ensure((code, gotMsg) == ("ValidationException", msg), $"{label} gave {Py.Repr(code)}: {Py.Repr(gotMsg)}");
        }

        foreach (var table in (string[])[sTable, bTable])
        {
            var absent = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = good[table](), ConsistentRead = true })).Item;
            Ensure(absent is null, $"a refused BatchWriteItem still wrote its valid sibling: {Py.Repr(absent)}");
            var count = (await ddb.ScanAsync(new ScanRequest { TableName = table, Select = Select.COUNT, ConsistentRead = true })).Count;
            Ensure(count == 1, $"table {table} holds {Py.Repr(count)} items after the refusals, expected 1");
        }
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

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
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
        var left = ctx.TimeLeftSeconds;
        var budget = TimeSpan.FromSeconds(Math.Max(0.0, (left > 0 ? left : 300.0) - 30.0));
        TableDescription? desc = null;
        while (clock.Elapsed < budget)
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

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected the call to be refused, but it succeeded");
        }

        return (error.Code, error.Message);
    }

    private static async Task<string> Create(FidelityContext ctx, IAmazonDynamoDB ddb, string attrType)
    {
        var table = FidelityContext.UniqueName($"sim2real-behav-ddb-emptykey-{attrType.ToLowerInvariant()}-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", new ScalarAttributeType(attrType))],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        return table;
    }
}
