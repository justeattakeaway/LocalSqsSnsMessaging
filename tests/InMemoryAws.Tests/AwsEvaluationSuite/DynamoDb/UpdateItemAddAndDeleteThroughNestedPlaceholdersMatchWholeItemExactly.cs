// Translated from behavioral/dynamodb/check_update_item_add_and_delete_through_nested_placeholders_match_whole_item_exactly.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-add-and-delete-through-nested-placeholders-match-whole-item-exactly</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemAddAndDeleteThroughNestedPlaceholdersMatchWholeItemExactly
{
    private const string Check = "update-item-add-and-delete-through-nested-placeholders-match-whole-item-exactly";

    /// <summary><paramref name="value"/> with every String/Number/Binary Set member put in a fixed order.</summary>
    private static AttributeValue Normalize(AttributeValue value)
    {
        if (value.M is not null)
        {
            return M([.. value.M.Select(e => (e.Key, Normalize(e.Value)))]);
        }

        if (value.L is not null)
        {
            return L([.. value.L.Select(Normalize)]);
        }

        if (value.SS is not null)
        {
            return SS([.. value.SS.Order(StringComparer.Ordinal)]);
        }

        if (value.NS is not null)
        {
            return NS([.. value.NS.Order(StringComparer.Ordinal)]);
        }

        if (value.BS is not null)
        {
            return BS([.. value.BS.Select(b => b.ToArray()).Order(Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b)))]);
        }

        return value;
    }

    private static Dictionary<string, AttributeValue>? NormalizeItem(Dictionary<string, AttributeValue>? item) =>
        item?.ToDictionary(e => e.Key, e => Normalize(e.Value), StringComparer.Ordinal);

    /// <summary>
    /// ADD and DELETE through per-segment placeholder paths change only the addressed nested
    /// leaf; the parent map is left present but empty once its last set member is deleted.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-add-and-delete-through-nested-placeholders-match-whole-item-exactly")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("deviceId", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("deviceId", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("deviceId", S("edge-gw-1")));
        Dictionary<string, AttributeValue> ItemWith(string seconds, AttributeValue alerts) => Item(
            ("deviceId", S("edge-gw-1")),
            ("firmware", S("v3")),
            ("telemetry", M(
                ("uptime", M(("seconds", N(seconds)))),
                ("alerts", alerts))));
        var original = ItemWith("100", M(("codes", SS("overheat", "vibration"))));
        await ddb.PutItemAsync(table, original);

        var uptimeNames = Names(("#t", "telemetry"), ("#u", "uptime"), ("#s", "seconds"));
        var alertsNames = Names(("#t", "telemetry"), ("#a", "alerts"), ("#c", "codes"));

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "ADD #t.#u.#s :delta",
            ExpressionAttributeNames = uptimeNames,
            ExpressionAttributeValues = Item((":delta", N("30"))),
        });
        var afterAdd = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        var expectedAfterAdd = ItemWith("130", M(("codes", SS("overheat", "vibration"))));
        Ensure(
            Py.Equal(NormalizeItem(afterAdd), NormalizeItem(expectedAfterAdd)),
            $"after ADD through telemetry.uptime.seconds the item is {Py.Repr(afterAdd)}, " +
            $"expected exactly {Py.Repr(expectedAfterAdd)}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "DELETE #t.#a.#c :codes",
            ExpressionAttributeNames = alertsNames,
            ExpressionAttributeValues = Item((":codes", SS("vibration"))),
        });
        var afterFirstDelete = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        var expectedAfterFirstDelete = ItemWith("130", M(("codes", SS("overheat"))));
        Ensure(
            Py.Equal(NormalizeItem(afterFirstDelete), NormalizeItem(expectedAfterFirstDelete)),
            "after DELETE of one element through telemetry.alerts.codes the item is " +
            $"{Py.Repr(afterFirstDelete)}, expected exactly {Py.Repr(expectedAfterFirstDelete)}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "DELETE #t.#a.#c :codes",
            ExpressionAttributeNames = alertsNames,
            ExpressionAttributeValues = Item((":codes", SS("overheat"))),
        });
        var afterSecondDelete = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        var expectedAfterSecondDelete = ItemWith("130", M());
        Ensure(
            Py.Equal(NormalizeItem(afterSecondDelete), NormalizeItem(expectedAfterSecondDelete)),
            "deleting the last element of telemetry.alerts.codes must drop that attribute and leave " +
            $"telemetry.alerts as an empty map; got {Py.Repr(afterSecondDelete)}, expected exactly " +
            $"{Py.Repr(expectedAfterSecondDelete)}");
    }
}
