// Translated from behavioral/dynamodb/check_transact_items_accept_placeholders_without_an_expression.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-items-accept-placeholders-without-an-expression</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactItemsAcceptPlaceholdersWithoutAnExpression
{
    private const string EanNoExprPutItem =
        "1 validation error detected: ExpressionAttributeNames can only be specified when using expressions";

    private const string EavNoExprPutItem =
        "1 validation error detected: ExpressionAttributeValues can only be specified when using expressions";

    /// <summary>
    /// TransactWriteItems Put/Delete and TransactGetItems Get carrying placeholders without any
    /// expression are accepted and applied, while PutItem refuses the same placeholders.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-items-accept-placeholders-without-an-expression")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb, "sim2real-behav-ddb-txph-");
        Dictionary<string, string> NamesArg() => Names(("#n", "color"));
        Dictionary<string, AttributeValue> ValuesArg() => Item((":v", S("red")));

        var (code, msg, _) = await Error(() => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("single"))),
            ExpressionAttributeNames = NamesArg(),
        }));
        Ensure(
            (code, msg) == ("ValidationException", EanNoExprPutItem),
            $"PutItem with ExpressionAttributeNames and no expression gave {Py.Repr(code)}: {Py.Repr(msg)}");
        Ensure(await Get(ddb, table, Item(("pk", S("single")))) is null, "a refused PutItem wrote its item");
        (code, msg, _) = await Error(() => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("single-values"))),
            ExpressionAttributeValues = ValuesArg(),
        }));
        Ensure(
            (code, msg) == ("ValidationException", EavNoExprPutItem),
            $"PutItem with ExpressionAttributeValues and no expression gave {Py.Repr(code)}: {Py.Repr(msg)}");
        var stored = await Get(ddb, table, Item(("pk", S("single-values"))));
        Ensure(stored is null, $"a refused PutItem wrote {Py.Repr(stored)}");

        // puts = {"put-names": {"ExpressionAttributeNames": names}, "put-values": {"ExpressionAttributeValues": values}}
        foreach (var (pk, withNames) in new[] { ("put-names", true), ("put-values", false) })
        {
            var item = Item(("pk", S(pk)), ("color", S("blue")));
            var put = new Put { TableName = table, Item = Item(("pk", S(pk)), ("color", S("blue"))) };
            if (withNames)
            {
                put.ExpressionAttributeNames = NamesArg();
            }
            else
            {
                put.ExpressionAttributeValues = ValuesArg();
            }

            await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = [new TransactWriteItem { Put = put }] });
            var got = await Get(ddb, table, Item(("pk", S(pk))));
            Ensure(Py.Equal(got, item), $"transactional Put with {Extra(withNames)} and no expression stored {Py.Repr(got)}");
        }

        foreach (var (pk, withNames) in new[] { ("put-names", true), ("put-values", false) })
        {
            var delete = new Delete { TableName = table, Key = Item(("pk", S(pk))) };
            if (withNames)
            {
                delete.ExpressionAttributeNames = NamesArg();
            }
            else
            {
                delete.ExpressionAttributeValues = ValuesArg();
            }

            await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = [new TransactWriteItem { Delete = delete }] });
            var got = await Get(ddb, table, Item(("pk", S(pk))));
            Ensure(got is null, $"transactional Delete with {Extra(withNames)} and no expression left {Py.Repr(got)}");
        }

        var full = Item(("pk", S("read")), ("color", S("green")), ("size", N("3")));
        await ddb.PutItemAsync(table, Item(("pk", S("read")), ("color", S("green")), ("size", N("3"))));
        var resp = await ddb.TransactGetItemsAsync(new TransactGetItemsRequest
        {
            TransactItems = [new TransactGetItem { Get = new Get { TableName = table, Key = Item(("pk", S("read"))), ExpressionAttributeNames = NamesArg() } }],
        });
        var slots = resp.Responses.Select(r => r.Item is null ? [] : new Dictionary<string, object?> { ["Item"] = r.Item }).ToList();
        Ensure(
            Py.Equal(slots, new List<object?> { new Dictionary<string, object?> { ["Item"] = full } }),
            $"TransactGetItems Get with ExpressionAttributeNames and no projection returned {Py.Repr(slots)}, expected the full item {Py.Repr(full)}");
    }

    /// <summary><c>sorted(extra)!r</c>.</summary>
    private static string Extra(bool withNames) => withNames ? "['ExpressionAttributeNames']" : "['ExpressionAttributeValues']";

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string[] notFound, double timeoutSeconds = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null)
            {
                return;
            }

            if (notFound.Contains(error.Code))
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

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var clock = FidelityTime.StartNew();
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE
                && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc?.TableStatus}");
    }

    /// <summary>Create an on-demand table keyed pk (S), plus sk of rangeType when given; wait ACTIVE.</summary>
    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb, string prefix, ScalarAttributeType? rangeType = null)
    {
        var table = FidelityContext.UniqueName(prefix);
        List<KeySchemaElement> keys = [new KeySchemaElement("pk", KeyType.HASH)];
        List<AttributeDefinition> attrs = [new AttributeDefinition("pk", ScalarAttributeType.S)];
        if (rangeType is not null)
        {
            keys.Add(new KeySchemaElement("sk", KeyType.RANGE));
            attrs.Add(new AttributeDefinition("sk", rangeType));
        }

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = keys,
            AttributeDefinitions = attrs,
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, ctx, table);
        return table;
    }

    /// <summary>
    /// (Error.Code, Error.Message, CancellationReasons) of the error <paramref name="fn"/> raises.
    /// An unexpected success fails.
    /// </summary>
    private static async Task<(string? Code, string? Message, List<CancellationReason>? Reasons)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn)
            ?? throw Fail("expected the call to be refused, but it succeeded");
        return (error.Code, error.Message, error.CancellationReasons());
    }

    /// <summary>The item at key read with ConsistentRead, or null.</summary>
    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
}
