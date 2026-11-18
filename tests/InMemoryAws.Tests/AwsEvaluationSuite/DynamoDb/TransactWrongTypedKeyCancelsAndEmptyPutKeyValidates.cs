// Translated from behavioral/dynamodb/check_transact_wrong_typed_key_cancels_and_empty_put_key_validates.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-wrong-typed-key-cancels-and-empty-put-key-validates</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWrongTypedKeyCancelsAndEmptyPutKeyValidates
{
    private const string Cancelled = "Transaction cancelled, please refer cancellation reasons for specific reasons [None, ValidationError]";
    private const string KeySchema = "The provided key element does not match the schema";
    private const string EmptyKey =
        "One or more parameter values are not valid. The AttributeValue for a key attribute cannot "
        + "contain an empty string value. Key: pk";

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string notFound, double timeoutSeconds = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || error.Code == notFound)
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
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        var clock = FidelityTime.StartNew();
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

        throw Fail($"table {table} not ACTIVE before the deadline: {desc}");
    }

    /// <summary>Create an on-demand table keyed pk (S); wait ACTIVE.</summary>
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
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);
        return table;
    }

    /// <summary>(Error.Code, Error.Message, CancellationReasons) of the ClientError fn() raises. An unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message, List<CancellationReason>? Reasons)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, err) = await FidelityContext.Capture(fn);
        if (err is not null)
        {
            return (err.Code, err.Message, err.CancellationReasons());
        }

        throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }

    /// <summary>The item at key read with ConsistentRead, or null.</summary>
    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

    private static string ReasonsRepr(List<CancellationReason>? reasons) =>
        reasons is null
            ? "None"
            : "[" + string.Join(", ", reasons.Select(r =>
            {
                var d = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Code"] = r.Code };
                if (r.Message is not null)
                {
                    d["Message"] = r.Message;
                }

                return Py.Repr(d);
            })) + "]";

    /// <summary>Assert outcome is a cancellation with reasons [None, ValidationError(key schema)].</summary>
    private static void AssertCancelled(string label, (string? Code, string? Message, List<CancellationReason>? Reasons) outcome)
    {
        var (code, msg, reasons) = outcome;
        Ensure(code == "TransactionCanceledException" && msg == Cancelled, $"{label} gave {Py.Repr(code)}: {Py.Repr(msg)}");
        var got = (reasons ?? []).Select(r => (r.Code, r.Message)).ToList();
        Ensure(
            got.Count == 2 && got[0] == ("None", null) && got[1] == ("ValidationError", KeySchema),
            $"{label} reasons were {ReasonsRepr(reasons)}");
    }

    /// <summary>
    /// A wrong-typed key in a Delete, Update or Get slot cancels the transaction with ValidationError;
    /// an empty-string key in a Put slot is a top-level ValidationException. Nothing is written.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-wrong-typed-key-cancels-and-empty-put-key-validates")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb, "sim2real-behav-ddb-txkey-");
        var seedKey = Item(("pk", S("seed")));
        var seed = Item(("pk", S("seed")), ("v", S("orig")));
        await ddb.PutItemAsync(table, seed);
        var wrongKey = Item(("pk", N("1")));
        var freshKey = Item(("pk", S("fresh")));
        TransactWriteItem UpdateSeed() => new()
        {
            Update = new Update
            {
                TableName = table,
                Key = seedKey,
                UpdateExpression = "SET v = :v",
                ExpressionAttributeValues = Item((":v", S("changed"))),
            },
        };
        TransactWriteItem PutFresh() => new()
        {
            Put = new Put { TableName = table, Item = Item(("pk", S("fresh")), ("v", S("1"))) },
        };

        AssertCancelled("Delete with an N key on an S-keyed table", await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [UpdateSeed(), new TransactWriteItem { Delete = new Delete { TableName = table, Key = wrongKey } }],
        })));
        AssertCancelled("Update with an N key on an S-keyed table", await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                PutFresh(),
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = wrongKey,
                        UpdateExpression = "SET v = :v",
                        ExpressionAttributeValues = Item((":v", S("x"))),
                    },
                },
            ],
        })));
        AssertCancelled("Get with an N key on an S-keyed table", await Error(() => ddb.TransactGetItemsAsync(new TransactGetItemsRequest
        {
            TransactItems =
            [
                new TransactGetItem { Get = new Get { TableName = table, Key = seedKey } },
                new TransactGetItem { Get = new Get { TableName = table, Key = wrongKey } },
            ],
        })));

        var (code, msg, reasons) = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                UpdateSeed(),
                new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("")), ("v", S("1"))) } },
            ],
        }));
        Ensure(code == "ValidationException" && msg == EmptyKey, $"Put with an empty key gave {Py.Repr(code)}: {Py.Repr(msg)}");
        Ensure(reasons is null, $"a top-level ValidationException carried CancellationReasons {ReasonsRepr(reasons)}");

        var got = await Get(ddb, table, seedKey);
        Ensure(Py.Equal(got, seed), $"a refused transaction changed the seed item to {Py.Repr(got)}");
        Ensure(await Get(ddb, table, freshKey) is null, "a cancelled transaction wrote its Put");
        var count = (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Count;
        Ensure(count == 1, $"the table holds {Py.Repr(count)} items after only refused transactions, expected 1");

        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = [UpdateSeed(), PutFresh()] });
        got = await Get(ddb, table, seedKey);
        Ensure(Py.Equal(got, Item(("pk", S("seed")), ("v", S("changed")))), $"the well-keyed transaction left the seed at {Py.Repr(got)}");
        Ensure(Py.Equal(await Get(ddb, table, freshKey), Item(("pk", S("fresh")), ("v", S("1")))), "the well-keyed Put did not land");
    }
}
