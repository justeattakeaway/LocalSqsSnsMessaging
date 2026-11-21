// Translated from behavioral/dynamodb/check_transact_write_items_nested_aliased_reserved_words_and_document_functions_commit.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-nested-aliased-reserved-words-and-document-functions-commit</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsNestedAliasedReservedWordsAndDocumentFunctionsCommit
{
    private const string Check = "transact-write-items-nested-aliased-reserved-words-and-document-functions-commit";
    private const string Canceled = "TransactionCanceledException";
    private const string ConditionFailed = "ConditionalCheckFailed";

    /// <summary>
    /// A TransactWriteItems whose ConditionCheck, Put, Delete and Update actions alias reserved
    /// words on nested map/list paths and call functions over them commits and applies every action.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ReservedWords.html
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-nested-aliased-reserved-words-and-document-functions-commit")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-tw-nest-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        var ccKey = Item(("pk", S("cc")));
        var ccItem = With(ccKey, ("m", M(("status", S("active")), ("name", S("alpha-widget-beta")))));
        var putKey = Item(("pk", S("put")));
        var putOld = With(putKey, ("m", M(("name", S("beta-item")))));
        var delKey = Item(("pk", S("del")));
        var delItem = With(delKey, ("l", L(M(("status", S("done"))), S("tail"))));
        var updKey = Item(("pk", S("upd")));
        var updOld = With(updKey, ("m", M(("tags", L(S("a"), S("b"))), ("year", N("2020")))));
        foreach (var item in new[] { ccItem, putOld, delItem, updOld })
        {
            await ddb.PutItemAsync(table, item);
        }

        var putNew = With(putKey, ("m", M(("name", S("gamma-item")))), ("seq", N("1")));

        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = table,
                        Key = ccKey,
                        ConditionExpression = "attribute_exists(#m.#status) AND #m.#status = :active "
                            + "AND contains(#m.#name, :needle)",
                        ExpressionAttributeNames = Names(("#m", "m"), ("#status", "status"), ("#name", "name")),
                        ExpressionAttributeValues = Item((":active", S("active")), (":needle", S("widget"))),
                    },
                },
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = table,
                        Item = putNew,
                        ConditionExpression = "attribute_type(#m.#name, :s) AND begins_with(#m.#name, :b)",
                        ExpressionAttributeNames = Names(("#m", "m"), ("#name", "name")),
                        ExpressionAttributeValues = Item((":s", S("S")), (":b", S("beta"))),
                    },
                },
                new TransactWriteItem
                {
                    Delete = new Delete
                    {
                        TableName = table,
                        Key = delKey,
                        ConditionExpression = "#l[0].#status = :done AND attribute_not_exists(#l[0].#missing)",
                        ExpressionAttributeNames = Names(("#l", "l"), ("#status", "status"), ("#missing", "missing")),
                        ExpressionAttributeValues = Item((":done", S("done"))),
                    },
                },
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = updKey,
                        UpdateExpression = "SET #m.#year = :newyear",
                        ConditionExpression = "size(#m.#tags) = :two AND #m.#year = :oldyear",
                        ExpressionAttributeNames = Names(("#m", "m"), ("#year", "year"), ("#tags", "tags")),
                        ExpressionAttributeValues = Item((":two", N("2")), (":oldyear", N("2020")), (":newyear", N("2021"))),
                    },
                },
            ],
        });

        var gotCc = await Get(ddb, table, ccKey);
        Ensure(Py.Equal(gotCc, ccItem), $"the ConditionCheck action must not alter its item, got {Py.Repr(gotCc)}");
        var gotPut = await Get(ddb, table, putKey);
        Ensure(Py.Equal(gotPut, putNew), $"the committed Put must store {Py.Repr(putNew)}, got {Py.Repr(gotPut)}");
        var gotDel = await Get(ddb, table, delKey);
        Ensure(gotDel is null, $"the committed Delete must remove the item, got {Py.Repr(gotDel)}");
        var gotUpd = await Get(ddb, table, updKey);
        var wantUpd = With(updKey, ("m", M(("tags", L(S("a"), S("b"))), ("year", N("2021")))));
        Ensure(Py.Equal(gotUpd, wantUpd), $"the committed Update must store {Py.Repr(wantUpd)}, got {Py.Repr(gotUpd)}");

        // Negative control: the same nested-aliased ConditionCheck with a mismatching value must
        // cancel the whole transaction, proving the condition is actually evaluated.
        var sideKey = Item(("pk", S("side")));
        var err = await FidelityContext.CaptureError(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = table,
                        Key = ccKey,
                        ConditionExpression = "#m.#status = :wrong",
                        ExpressionAttributeNames = Names(("#m", "m"), ("#status", "status")),
                        ExpressionAttributeValues = Item((":wrong", S("inactive"))),
                    },
                },
                new TransactWriteItem { Put = new Put { TableName = table, Item = With(sideKey, ("v", S("1"))) } },
            ],
        })) ?? throw Fail("expected the call to be refused, but it succeeded");
        var (code, msg, reasons) = (err.Code, err.Message, Reasons(err));
        var expectedMsg = "Transaction cancelled, please refer cancellation reasons for specific reasons "
            + $"[{ConditionFailed}, None]";
        Ensure((code, msg) == (Canceled, expectedMsg), $"the mismatching nested condition gave {Py.Repr(code)}: {Py.Repr(msg)}");
        Ensure(
            Py.Equal(reasons, new[] { R(ConditionFailed, "The conditional request failed"), R("None") }),
            $"unexpected cancellation reasons {Py.Repr(reasons)}");
        Ensure(await Get(ddb, table, sideKey) is null, "the canceled transaction must not have created the side item");
        Ensure(Py.Equal(await Get(ddb, table, ccKey), ccItem), "the canceled transaction must not have altered the cc item");
    }

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var bound = Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < bound)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }

    /// <summary><c>{**item, name: value, ...}</c>.</summary>
    private static Dictionary<string, AttributeValue> With(Dictionary<string, AttributeValue> item, params (string Name, AttributeValue Value)[] entries)
    {
        var result = new Dictionary<string, AttributeValue>(item, StringComparer.Ordinal);
        foreach (var (name, value) in entries)
        {
            result[name] = value;
        }

        return result;
    }

    /// <summary>The item at key read with ConsistentRead, or None.</summary>
    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

    /// <summary>The cancellation reasons as boto3 hands them back: each with only the members present.</summary>
    private static List<Dictionary<string, object?>>? Reasons(AwsError err) => err.CancellationReasons()?.Select(r =>
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (r.Item is not null)
        {
            result["Item"] = r.Item;
        }

        if (r.Code is not null)
        {
            result["Code"] = r.Code;
        }

        if (r.Message is not null)
        {
            result["Message"] = r.Message;
        }

        return result;
    }).ToList();

    private static Dictionary<string, object?> R(string code, string? message = null)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Code"] = code };
        if (message is not null)
        {
            result["Message"] = message;
        }

        return result;
    }

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
