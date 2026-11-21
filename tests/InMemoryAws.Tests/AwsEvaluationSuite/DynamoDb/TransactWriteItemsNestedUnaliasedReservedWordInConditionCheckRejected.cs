// Translated from behavioral/dynamodb/check_transact_write_items_nested_unaliased_reserved_word_in_condition_check_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-nested-unaliased-reserved-word-in-condition-check-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsNestedUnaliasedReservedWordInConditionCheckRejected
{
    private const string Check = "transact-write-items-nested-unaliased-reserved-word-in-condition-check-rejected";
    private const string Reserved = "Invalid ConditionExpression: Attribute name is a reserved keyword; reserved keyword: status";

    /// <summary>
    /// A reserved word used unaliased in a nested map path of a TransactWriteItems
    /// ConditionCheck's ConditionExpression is a top-level ValidationException; aliasing it commits.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ReservedWords.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-nested-unaliased-reserved-word-in-condition-check-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-tw-nest-res-");
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

        Dictionary<string, AttributeValue> CcKey() => Item(("pk", S("cc")));
        var ccItem = Item(("pk", S("cc")), ("m", M(("status", S("active")))));
        await ddb.PutItemAsync(table, Item(("pk", S("cc")), ("m", M(("status", S("active"))))));
        Dictionary<string, AttributeValue> SideKey() => Item(("pk", S("side")));
        var sideItem = Item(("pk", S("side")), ("v", S("1")));

        var err = await FidelityContext.CaptureError(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("side")), ("v", S("1"))) } },
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = table,
                        Key = CcKey(),
                        ConditionExpression = "m.status = :v",
                        ExpressionAttributeValues = Item((":v", S("active"))),
                    },
                },
            ],
        })) ?? throw Fail("expected the call to be refused, but it succeeded");
        var (code, msg, reasons) = (err.Code, err.Message, err.CancellationReasons());
        Ensure(
            (code, msg) == ("ValidationException", Reserved),
            $"the unaliased nested reserved word gave {Py.Repr(code)}: {Py.Repr(msg)}");
        Ensure(reasons is null, $"a top-level ValidationException must carry no CancellationReasons, got {Py.Repr(reasons?.Select(r => r.Code))}");
        Ensure(await Get(ddb, table, SideKey()) is null, "the refused transaction must not have written its other Put");
        Ensure(Py.Equal(await Get(ddb, table, CcKey()), ccItem), "the refused transaction must leave the cc item unchanged");

        // Positive control: the identical nested path, aliased, commits.
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("side")), ("v", S("1"))) } },
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = table,
                        Key = CcKey(),
                        ConditionExpression = "#m.#status = :v",
                        ExpressionAttributeNames = Names(("#m", "m"), ("#status", "status")),
                        ExpressionAttributeValues = Item((":v", S("active"))),
                    },
                },
            ],
        });
        Ensure(
            Py.Equal(await Get(ddb, table, SideKey()), sideItem),
            "the aliased nested path must let the transaction commit and write the side item");
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

    /// <summary>The item at key read with ConsistentRead, or None.</summary>
    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
