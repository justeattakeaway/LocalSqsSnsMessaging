// Translated from behavioral/dynamodb/check_transact_write_items_expression_size_limit_applies_per_action.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-expression-size-limit-applies-per-action</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsExpressionSizeLimitAppliesPerAction
{
    private const string Check = "transact-write-items-expression-size-limit-applies-per-action";
    private const string ErrValidation = "ValidationException";
    private const int Max = 4096;
    private const string CondTooLong = "Invalid ConditionExpression: Expression size has exceeded the maximum allowed size;";
    private const string UpdateTooLong = "Invalid UpdateExpression: Expression size has exceeded the maximum allowed size;";

    /// <summary>(code, message) of the error fn() raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"call unexpectedly SUCCEEDED where ValidationException was required: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>An `attribute_not_exists(&lt;padding&gt;)` condition exactly `total` bytes, true for every item.</summary>
    private static string ConditionOfLen(int total)
    {
        const string prefix = "attribute_not_exists(", suffix = ")";
        var expr = prefix + new string('a', total - prefix.Length - suffix.Length) + suffix;
        Ensure(expr.Length == total, $"{expr.Length}");
        return expr;
    }

    /// <summary>A `SET &lt;padding&gt; = :v` update exactly `total` bytes; `&lt;padding&gt;` is the attribute set.</summary>
    private static (string Expr, string Attr) UpdateOfLen(int total)
    {
        const string prefix = "SET ", suffix = " = :v";
        var attr = new string('a', total - prefix.Length - suffix.Length);
        var expr = prefix + attr + suffix;
        Ensure(expr.Length == total, $"{expr.Length}");
        return (expr, attr);
    }

    /// <summary>
    /// The 4 KB expression-size limit applies independently to each TransactWriteItems action:
    /// a 4096-byte Put condition or Update UpdateExpression commits, a 4097-byte one is a top-level
    /// ValidationException, and an oversized second action leaves the transaction's first Put unwritten.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-expression-size-limit-applies-per-action")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        // Positive control: a Put whose ConditionExpression is exactly 4096 bytes commits.
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Put = new Put { TableName = table, Item = Item(("pk", S("put-accepted"))), ConditionExpression = ConditionOfLen(Max) },
                },
            ],
        });
        var got = await Get(ddb, table, "put-accepted");
        Ensure(
            Py.Equal(got, Item(("pk", S("put-accepted")))),
            $"positive control: a 4096-byte Put ConditionExpression must commit, got {Py.Repr(got)}");

        // Positive control: an Update whose UpdateExpression is exactly 4096 bytes commits.
        var (updateExpr, updateAttr) = UpdateOfLen(Max);
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = Item(("pk", S("update-accepted"))),
                        UpdateExpression = updateExpr,
                        ExpressionAttributeValues = Item((":v", S("ok"))),
                    },
                },
            ],
        });
        got = await Get(ddb, table, "update-accepted");
        Ensure(
            Py.Equal(got?.GetValueOrDefault(updateAttr), S("ok")),
            $"positive control: a 4096-byte Update UpdateExpression must commit, got {Py.Repr(got)}");

        // Negative: a ConditionCheck whose ConditionExpression is 4097 bytes is a top-level ValidationException.
        await ddb.PutItemAsync(table, Item(("pk", S("cc-target"))));
        var (code, message) = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck { TableName = table, Key = Item(("pk", S("cc-target"))), ConditionExpression = ConditionOfLen(Max + 1) },
                },
            ],
        }));
        Ensure(code == ErrValidation, $"oversized ConditionCheck condition answered {Py.Repr(code)} ({Py.Repr(message)})");
        Ensure(message == CondTooLong, $"oversized ConditionCheck condition message was {Py.Repr(message)}");

        // Negative: a Delete whose ConditionExpression is 4097 bytes is a top-level ValidationException,
        // and the item it would have deleted survives.
        await ddb.PutItemAsync(table, Item(("pk", S("del-target"))));
        (code, message) = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Delete = new Delete { TableName = table, Key = Item(("pk", S("del-target"))), ConditionExpression = ConditionOfLen(Max + 1) },
                },
            ],
        }));
        Ensure(code == ErrValidation, $"oversized Delete condition answered {Py.Repr(code)} ({Py.Repr(message)})");
        Ensure(message == CondTooLong, $"oversized Delete condition message was {Py.Repr(message)}");
        got = await Get(ddb, table, "del-target");
        Ensure(
            Py.Equal(got, Item(("pk", S("del-target")))),
            $"a Delete rejected for an oversized ConditionExpression must not remove its item, got {Py.Repr(got)}");

        // Negative: an Update whose UpdateExpression is 4097 bytes is a top-level ValidationException.
        var (updateOver, _) = UpdateOfLen(Max + 1);
        (code, message) = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = Item(("pk", S("upd-target"))),
                        UpdateExpression = updateOver,
                        ExpressionAttributeValues = Item((":v", S("x"))),
                    },
                },
            ],
        }));
        Ensure(code == ErrValidation, $"oversized Update UpdateExpression answered {Py.Repr(code)} ({Py.Repr(message)})");
        Ensure(message == UpdateTooLong, $"oversized Update UpdateExpression message was {Py.Repr(message)}");

        // An oversized SECOND action refuses the whole call; the first action's Put never commits.
        const string firstKey = "second-member-oversized-first-put";
        (code, message) = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S(firstKey))) } },
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck { TableName = table, Key = Item(("pk", S("cc-target"))), ConditionExpression = ConditionOfLen(Max + 1) },
                },
            ],
        }));
        Ensure(code == ErrValidation, $"oversized second action answered {Py.Repr(code)} ({Py.Repr(message)})");
        Ensure(message == CondTooLong, $"oversized second action message was {Py.Repr(message)}");
        got = await Get(ddb, table, firstKey);
        Ensure(
            got is null,
            $"a transaction rejected for its oversized second action must not write its first Put, got {Py.Repr(got)}");
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
}
