// Translated from behavioral/dynamodb/check_nesting_depth_limit_covers_transact_condition_check_and_legacy_expected_values.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.nesting-depth-limit-covers-transact-condition-check-and-legacy-expected-values</summary>
[Category("AwsEvaluationSuite")]
public sealed class NestingDepthLimitCoversTransactConditionCheckAndLegacyExpectedValues
{
    private const int OverLimitWrappers = 32;
    private const int AtLimitWrappers = 31;
    private const string NestingMsg = "Nesting Levels have exceeded supported limits: Attributes in the item have nested levels beyond supported limit";
    private const string TransactConditionCheckDeepMsg = NestingMsg;
    private const string LegacyExpectedDeepMsg = "1 validation error detected: " + NestingMsg;
    private static readonly string[] NsMembers = ["1", "2"];

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        var clock = FidelityTime.StartNew();
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

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary><paramref name="wrappers"/> nested single-member M levels around <paramref name="leaf"/>.</summary>
    private static AttributeValue DeepMap(int wrappers, AttributeValue leaf)
    {
        var value = leaf;
        for (var i = 0; i < wrappers; i++)
        {
            value = M(("a", value));
        }

        return value;
    }

    private static AttributeValue LeafOf(AttributeValue value, int wrappers)
    {
        for (var i = 0; i < wrappers; i++)
        {
            value = value.M["a"];
        }

        return value;
    }

    /// <summary>
    /// An over-deep value in a TransactWriteItems ConditionCheck or a legacy Expected Value is
    /// ValidationException and writes nothing; a number set at the deepest accepted level is stored.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.nesting-depth-limit-covers-transact-condition-check-and-legacy-expected-values")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-depthok-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitActive(ddb, ctx, table);

        async Task<Dictionary<string, AttributeValue>?> Get(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        // A number-set leaf under the deepest accepted map chain is stored and reads back.
        var doc = DeepMap(AtLimitWrappers, NS("1", "2"));
        await ddb.PutItemAsync(table, Item(("pk", S("ns")), ("doc", doc)));
        var stored = await Get("ns");
        Ensure(stored is not null, "PutItem of an at-limit map chain around a number set stored nothing");
        var leaf = LeafOf(stored["doc"], AtLimitWrappers);
        Ensure(
            (leaf.NS ?? []).Order(StringComparer.Ordinal).SequenceEqual(NsMembers) && Py.Boto(leaf).Keys.SequenceEqual(["NS"]),
            $"the number-set leaf read back as {Py.Repr(leaf)}");

        var seeded = Item(("pk", S("target")), ("v", S("orig")));
        await ddb.PutItemAsync(table, seeded);
        AttributeValue Deep() => DeepMap(OverLimitWrappers, S("leaf"));

        var (code, msg) = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = table,
                        Key = Item(("pk", S("target"))),
                        ConditionExpression = "attribute_exists(pk) OR :d = :d",
                        ExpressionAttributeValues = Item((":d", Deep())),
                    },
                },
                new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("marker"))) } },
            ],
        }));
        Ensure(
            (code, msg) == ("ValidationException", TransactConditionCheckDeepMsg),
            $"TransactWriteItems ConditionCheck with an over-deep value gave {Py.Repr(code)}: {Py.Repr(msg)}");
        var marker = await Get("marker");
        Ensure(marker is null, $"the refused transaction's Put left {Py.Repr(marker)}");
        var item = await Get("target");
        Ensure(Py.Equal(item, seeded), $"after the refused transaction the target item is {Py.Repr(item)}");

        (code, msg) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("target"))),
            AttributeUpdates = new Dictionary<string, AttributeValueUpdate>(StringComparer.Ordinal)
            {
                ["myattr"] = new AttributeValueUpdate { Action = AttributeAction.PUT, Value = S("x") },
            },
            Expected = new Dictionary<string, ExpectedAttributeValue>(StringComparer.Ordinal)
            {
                ["deep"] = new ExpectedAttributeValue { Value = Deep() },
            },
        }));
        Ensure(
            (code, msg) == ("ValidationException", LegacyExpectedDeepMsg),
            $"UpdateItem with an over-deep legacy Expected Value gave {Py.Repr(code)}: {Py.Repr(msg)}");
        item = await Get("target");
        Ensure(Py.Equal(item, seeded), $"after the refused legacy Expected update the item is {Py.Repr(item)}");
    }
}
