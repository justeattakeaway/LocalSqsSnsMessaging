// Translated from behavioral/dynamodb/check_expression_attribute_keys_without_prefix_rejected_on_item_ops.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.expression-attribute-keys-without-prefix-rejected-on-item-ops</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExpressionAttributeKeysWithoutPrefixRejectedOnItemOps
{
    // Write operations prefix expression validation messages; GetItem does not.
    private const string WritePrefix = "1 validation error detected: ";
    private const string ReadPrefix = "";

    private static string BadNameMsg(string prefix, string key) => $"{prefix}ExpressionAttributeNames contains invalid key: Syntax error; key: \"{key}\"";

    private static string BadValueMsg(string prefix, string key) => $"{prefix}ExpressionAttributeValues contains invalid key: Syntax error; key: \"{key}\"";

    /// <summary>
    /// An ExpressionAttributeNames key without '#' or an ExpressionAttributeValues key without
    /// ':' is a syntax error naming the key on PutItem, GetItem, UpdateItem and DeleteItem.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ExpressionAttributeNames.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.expression-attribute-keys-without-prefix-rejected-on-item-ops")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-eakey-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, ctx, table);

        static Dictionary<string, AttributeValue> Key() => Item(("pk", S("item1")));
        static Dictionary<string, AttributeValue> Seeded() => Item(("pk", S("item1")), ("v", S("val")));
        static Dictionary<string, AttributeValue> NewKey() => Item(("pk", S("new")));
        await ddb.PutItemAsync(table, Seeded());

        (string Label, string Want, Func<Task> Call)[] arms =
        [
            (
                "PutItem name 'bad'",
                BadNameMsg(WritePrefix, "bad"),
                () => ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = NewKey(), ConditionExpression = "attribute_not_exists(pk)", ExpressionAttributeNames = Names(("bad", "pk")) })
            ),
            (
                "GetItem name 'nohash'",
                BadNameMsg(ReadPrefix, "nohash"),
                () => ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ProjectionExpression = "v", ExpressionAttributeNames = Names(("nohash", "v")) })
            ),
            (
                "UpdateItem name 'missing_hash'",
                BadNameMsg(WritePrefix, "missing_hash"),
                () => ddb.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = table,
                    Key = Key(),
                    UpdateExpression = "SET v = :val",
                    ExpressionAttributeNames = Names(("missing_hash", "v")),
                    ExpressionAttributeValues = Item((":val", S("new"))),
                })
            ),
            (
                "DeleteItem name 'nope'",
                BadNameMsg(WritePrefix, "nope"),
                () => ddb.DeleteItemAsync(new DeleteItemRequest { TableName = table, Key = Key(), ConditionExpression = "attribute_exists(v)", ExpressionAttributeNames = Names(("nope", "v")) })
            ),
            (
                "PutItem value 'nocolon'",
                BadValueMsg(WritePrefix, "nocolon"),
                () => ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = NewKey(), ConditionExpression = "attribute_not_exists(pk)", ExpressionAttributeValues = Item(("nocolon", S("x"))) })
            ),
            (
                "UpdateItem value 'val'",
                BadValueMsg(WritePrefix, "val"),
                () => ddb.UpdateItemAsync(new UpdateItemRequest { TableName = table, Key = Key(), UpdateExpression = "SET v = :val", ExpressionAttributeValues = Item(("val", S("new"))) })
            ),
            (
                "DeleteItem value 'nocolon'",
                BadValueMsg(WritePrefix, "nocolon"),
                () => ddb.DeleteItemAsync(new DeleteItemRequest { TableName = table, Key = Key(), ConditionExpression = "v = :v", ExpressionAttributeValues = Item(("nocolon", S("val"))) })
            ),
        ];
        foreach (var (label, want, call) in arms)
        {
            var (code, msg) = await Error(call);
            Ensure((code, msg) == ("ValidationException", want), $"{label} gave {Py.Repr(code)}: {Py.Repr(msg)}");
        }

        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(item, Seeded()), $"after the refused requests item1 is {Py.Repr(item)}, expected {Py.Repr(Seeded())}");
        var absent = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = NewKey(), ConsistentRead = true })).Item;
        Ensure(absent is null, $"a refused PutItem stored {Py.Repr(absent)}");

        // Positive control: the same requests with prefixed keys succeed.
        var got = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Key(),
            ProjectionExpression = "#v",
            ExpressionAttributeNames = Names(("#v", "v")),
            ConsistentRead = true,
        })).Item;
        Ensure(Py.Equal(got, Item(("v", S("val")))), $"GetItem projecting #v returned {Py.Repr(got)}");
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET #v = :val",
            ExpressionAttributeNames = Names(("#v", "v")),
            ExpressionAttributeValues = Item((":val", S("new"))),
        });
        item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(item, Item(("pk", S("item1")), ("v", S("new")))), $"the well-formed UpdateItem left {Py.Repr(item)}");
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = NewKey(),
            ConditionExpression = "attribute_not_exists(#pk)",
            ExpressionAttributeNames = Names(("#pk", "pk")),
        });
        var stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = NewKey(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(stored, NewKey()), $"the well-formed conditional PutItem stored {Py.Repr(stored)}");
        await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Key(),
            ConditionExpression = "attribute_exists(#v)",
            ExpressionAttributeNames = Names(("#v", "v")),
        });
        var gone = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(gone is null, $"the well-formed conditional DeleteItem left {Py.Repr(gone)}");
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
}
