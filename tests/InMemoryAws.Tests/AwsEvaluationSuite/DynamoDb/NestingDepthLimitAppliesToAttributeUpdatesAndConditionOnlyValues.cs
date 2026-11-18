// Translated from behavioral/dynamodb/check_nesting_depth_limit_applies_to_attribute_updates_and_condition_only_values.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.nesting-depth-limit-applies-to-attribute-updates-and-condition-only-values</summary>
[Category("AwsEvaluationSuite")]
public sealed class NestingDepthLimitAppliesToAttributeUpdatesAndConditionOnlyValues
{
    private const string NestingMsg = "1 validation error detected: Nesting Levels have exceeded supported limits: Attributes in the item have nested levels beyond supported limit";
    private const int OverLimitWrappers = 32;
    private const int AtLimitWrappers = 31;

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

    /// <summary><paramref name="wrappers"/> nested single-member M levels around an S leaf.</summary>
    private static AttributeValue DeepMap(int wrappers)
    {
        var value = S("leaf");
        for (var i = 0; i < wrappers; i++)
        {
            value = M(("a", value));
        }

        return value;
    }

    private static Dictionary<string, AttributeValue> With(Dictionary<string, AttributeValue> item, params (string Name, AttributeValue Value)[] extra)
    {
        var copy = new Dictionary<string, AttributeValue>(item, StringComparer.Ordinal);
        foreach (var (name, value) in extra)
        {
            copy[name] = value;
        }

        return copy;
    }

    /// <summary>
    /// A value one level past the nesting limit is refused in AttributeUpdates, inside
    /// if_not_exists, and when only a DeleteItem or UpdateItem condition reads it; one level less works.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.nesting-depth-limit-applies-to-attribute-updates-and-condition-only-values")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-depth-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitActive(ddb, ctx, table);

        Dictionary<string, AttributeValue> Key() => Item(("pk", S("target")));
        var seeded = With(Key(), ("v", S("orig")));
        await ddb.PutItemAsync(table, seeded);
        AttributeValue Deep() => DeepMap(OverLimitWrappers);

        var (code, msg) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            AttributeUpdates = new Dictionary<string, AttributeValueUpdate>(StringComparer.Ordinal)
            {
                ["deep"] = new AttributeValueUpdate { Action = AttributeAction.PUT, Value = Deep() },
            },
        }));
        Ensure((code, msg) == ("ValidationException", NestingMsg), $"AttributeUpdates PUT of an over-deep value gave {Py.Repr(code)}: {Py.Repr(msg)}");

        (code, msg) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET deep = if_not_exists(deep, :d)",
            ExpressionAttributeValues = Item((":d", Deep())),
        }));
        Ensure((code, msg) == ("ValidationException", NestingMsg), $"SET deep = if_not_exists(deep, :d) with an over-deep :d gave {Py.Repr(code)}: {Py.Repr(msg)}");

        (code, msg) = await Error(() => ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Key(),
            ConditionExpression = "attribute_exists(pk) OR :d = :d",
            ExpressionAttributeValues = Item((":d", Deep())),
        }));
        Ensure((code, msg) == ("ValidationException", NestingMsg), $"DeleteItem with an over-deep condition-only value gave {Py.Repr(code)}: {Py.Repr(msg)}");

        (code, msg) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET v = :s",
            ConditionExpression = "attribute_exists(pk) OR :d = :d",
            ExpressionAttributeValues = Item((":s", S("changed")), (":d", Deep())),
        }));
        Ensure((code, msg) == ("ValidationException", NestingMsg), $"UpdateItem with an over-deep condition-only value gave {Py.Repr(code)}: {Py.Repr(msg)}");

        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(item, seeded), $"after four refused requests the item is {Py.Repr(item)}, expected {Py.Repr(seeded)}");

        // Positive control: one level shallower, a condition-only value is accepted.
        Dictionary<string, AttributeValue> CtlKey() => Item(("pk", S("at-limit")));
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = With(CtlKey(), ("v", S("ok"))),
            ConditionExpression = "attribute_not_exists(pk) OR :d = :d",
            ExpressionAttributeValues = Item((":d", DeepMap(AtLimitWrappers))),
        });
        var ctl = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = CtlKey(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(ctl, With(CtlKey(), ("v", S("ok")))), $"PutItem with an at-limit condition-only value stored {Py.Repr(ctl)}");

        AttributeValue Shallow() => DeepMap(AtLimitWrappers);
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = CtlKey(),
            AttributeUpdates = new Dictionary<string, AttributeValueUpdate>(StringComparer.Ordinal)
            {
                ["deep"] = new AttributeValueUpdate { Action = AttributeAction.PUT, Value = Shallow() },
            },
        });
        ctl = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = CtlKey(), ConsistentRead = true })).Item;
        var want = With(CtlKey(), ("v", S("ok")), ("deep", Shallow()));
        Ensure(Py.Equal(ctl, want), $"AttributeUpdates PUT of an at-limit value left {Py.Repr(ctl)}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = CtlKey(),
            UpdateExpression = "SET deep2 = if_not_exists(deep2, :d)",
            ExpressionAttributeValues = Item((":d", Shallow())),
        });
        ctl = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = CtlKey(), ConsistentRead = true })).Item;
        want = With(want, ("deep2", Shallow()));
        Ensure(Py.Equal(ctl, want), $"SET deep2 = if_not_exists(deep2, :d) with an at-limit :d left {Py.Repr(ctl)}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = CtlKey(),
            UpdateExpression = "SET v = :s",
            ConditionExpression = "attribute_exists(pk) OR :d = :d",
            ExpressionAttributeValues = Item((":s", S("changed")), (":d", Shallow())),
        });
        ctl = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = CtlKey(), ConsistentRead = true })).Item;
        want = With(want, ("v", S("changed")));
        Ensure(Py.Equal(ctl, want), $"UpdateItem with an at-limit condition-only value left {Py.Repr(ctl)}");

        await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = CtlKey(),
            ConditionExpression = "attribute_exists(pk) OR :d = :d",
            ExpressionAttributeValues = Item((":d", Shallow())),
        });
        ctl = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = CtlKey(), ConsistentRead = true })).Item;
        Ensure(ctl is null, $"DeleteItem with an at-limit condition-only value left {Py.Repr(ctl)}");
    }
}
