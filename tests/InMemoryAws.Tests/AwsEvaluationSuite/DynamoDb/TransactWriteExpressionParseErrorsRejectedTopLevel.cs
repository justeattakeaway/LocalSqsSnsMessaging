// Translated from behavioral/dynamodb/check_transact_write_expression_parse_errors_rejected_top_level.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-expression-parse-errors-rejected-top-level</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteExpressionParseErrorsRejectedTopLevel
{
    private const string UpdateReserved = "Invalid UpdateExpression: Attribute name is a reserved keyword; reserved keyword: status";
    private const string ConditionReserved =
        "Invalid ConditionExpression: Attribute name is a reserved keyword; reserved keyword: status";

    private const string UpdateSyntax = "Invalid UpdateExpression: Syntax error; token: \"<EOF>\", near: \"=\"";
    private const string ConditionRedundantParens = "Invalid ConditionExpression: The expression has redundant parentheses;";

    /// <summary>
    /// Reserved-word, syntax and redundant-parenthesis errors in any TransactWriteItems action are
    /// a top-level ValidationException without the single-item prefix; nothing is written.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ReservedWords.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-expression-parse-errors-rejected-top-level")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb, "sim2real-behav-ddb-txpe-");
        Dictionary<string, AttributeValue> SeedKey() => Item(("pk", S("seed")));
        var seed = Item(("pk", S("seed")), ("status", S("active")), ("foo", S("orig")));
        await ddb.PutItemAsync(table, Item(("pk", S("seed")), ("status", S("active")), ("foo", S("orig"))));
        Dictionary<string, AttributeValue> Value() => Item((":v", S("active")));

        var defects = new (string Label, Func<TransactWriteItem> Action, string Expected)[]
        {
            ("Update reserved word", () => new TransactWriteItem
            {
                Update = new Update { TableName = table, Key = SeedKey(), UpdateExpression = "SET status = :v", ExpressionAttributeValues = Value() },
            }, UpdateReserved),
            ("ConditionCheck reserved word", () => new TransactWriteItem
            {
                ConditionCheck = new ConditionCheck { TableName = table, Key = SeedKey(), ConditionExpression = "status = :v", ExpressionAttributeValues = Value() },
            }, ConditionReserved),
            ("Put condition reserved word", () => new TransactWriteItem
            {
                Put = new Put { TableName = table, Item = Item(("pk", S("put"))), ConditionExpression = "status = :v", ExpressionAttributeValues = Value() },
            }, ConditionReserved),
            ("Delete condition reserved word", () => new TransactWriteItem
            {
                Delete = new Delete { TableName = table, Key = SeedKey(), ConditionExpression = "status = :v", ExpressionAttributeValues = Value() },
            }, ConditionReserved),
            ("Update truncated expression", () => new TransactWriteItem
            {
                Update = new Update { TableName = table, Key = SeedKey(), UpdateExpression = "SET foo =" },
            }, UpdateSyntax),
            ("ConditionCheck redundant parentheses", () => new TransactWriteItem
            {
                ConditionCheck = new ConditionCheck
                {
                    TableName = table,
                    Key = SeedKey(),
                    ConditionExpression = "((pk = :k))",
                    ExpressionAttributeValues = Item((":k", S("seed"))),
                },
            }, ConditionRedundantParens),
        };
        for (var i = 0; i < defects.Length; i++)
        {
            var (label, action, expected) = defects[i];
            var sideKey = Item(("pk", S($"side-{i}")));
            var side = new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S($"side-{i}")), ("v", S("1"))) } };
            var (code, msg, reasons) = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = [side, action()] }));
            Ensure((code, msg) == ("ValidationException", expected), $"{label} gave {Py.Repr(code)}: {Py.Repr(msg)}");
            Ensure(reasons is null, $"{label}: a top-level ValidationException carried {Py.Repr(reasons)}");
            Ensure(await Get(ddb, table, sideKey) is null, $"{label}: the refused transaction wrote its other Put");
            Ensure(await Get(ddb, table, Item(("pk", S("put")))) is null, $"{label}: the refused Put landed");
            var got = await Get(ddb, table, SeedKey());
            Ensure(Py.Equal(got, seed), $"{label}: the seed item changed to {Py.Repr(got)}");
        }

        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = SeedKey(),
                        UpdateExpression = "SET #s = :d",
                        ConditionExpression = "#s = :v",
                        ExpressionAttributeNames = Names(("#s", "status")),
                        ExpressionAttributeValues = Item((":v", S("active")), (":d", S("done"))),
                    },
                },
            ],
        });
        var final = await Get(ddb, table, SeedKey());
        Ensure(
            Py.Equal(final, Item(("pk", S("seed")), ("status", S("done")), ("foo", S("orig")))),
            $"the aliased transactional Update stored {Py.Repr(final)}");
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
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, ctx, table);
        return table;
    }

    /// <summary>(Error.Code, Error.Message, CancellationReasons) of the error <paramref name="fn"/> raises.</summary>
    private static async Task<(string? Code, string? Message, List<CancellationReason>? Reasons)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn)
            ?? throw Fail("expected the call to be refused, but it succeeded");
        return (error.Code, error.Message, error.CancellationReasons());
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
}
