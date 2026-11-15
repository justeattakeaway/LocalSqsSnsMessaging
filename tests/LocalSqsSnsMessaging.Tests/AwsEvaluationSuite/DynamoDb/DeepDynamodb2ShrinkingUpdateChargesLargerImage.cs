// Translated from behavioral/dynamodb/check_deep_dynamodb_2_shrinking_update_charges_larger_image.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-2-shrinking-update-charges-larger-image</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb2ShrinkingUpdateChargesLargerImage
{
    private const string Check = "deep-dynamodb-2-shrinking-update-charges-larger-image";

    // Item sizes per the DynamoDB item-size rules (UTF-8 attribute name bytes + value bytes):
    //   pk('pk'+'k') = 3, big('big' + 5000) = 5003, small('small' + 900) = 905
    //   full item = 5911 B -> ceil(5911 / 1024) = 6 write units
    //   after REMOVE big = 908 B -> 1 write unit
    private static readonly string Big = new('x', 5000);
    private static readonly string Small = new('y', 900);

    private static double? Units(ConsumedCapacity? cc, string label)
    {
        Ensure(cc is not null, $"{label}: no ConsumedCapacity although ReturnConsumedCapacity was set");
        return cc.CapacityUnits;
    }

    /// <summary>
    /// An UpdateItem that shrinks a ~6 KB item to ~1 KB is charged 6 WCU, the LARGER image.
    ///
    /// The next small update costs 1 WCU and growing it back costs 6: the charge is
    /// max(size before, size after). On-demand tables report CapacityUnits only.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-dynamodb-2-shrinking-update-charges-larger-image")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await Table(ctx, ddb);
        var key = Item(("pk", S("k")));

        var put = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = t,
            Item = Item(("pk", S("k")), ("big", S(Big)), ("small", S(Small))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var u = Units(put.ConsumedCapacity, "PutItem of the ~5.8 KB item");
        Ensure(u == 6.0, $"PutItem of a 5911-byte item consumed {Py.Repr(u)} WCU; expected 6.0");

        var shrink = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "REMOVE big",
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
            ReturnValues = ReturnValue.ALL_NEW,
        });
        Ensure(
            shrink.Attributes.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["pk", "small"]),
            $"shrink left {Py.Repr(shrink.Attributes.Keys.Order(StringComparer.Ordinal).ToList())}");
        u = Units(shrink.ConsumedCapacity, "shrinking UpdateItem");
        Ensure(
            u == 6.0,
            $"UpdateItem shrinking 5911 B -> 908 B consumed {Py.Repr(u)} WCU; expected 6.0 (the larger, " +
            "pre-update image). 1.0 means it was metered on the post-update size.");
        var tableUnits = shrink.ConsumedCapacity.Table?.CapacityUnits;
        Ensure(tableUnits == 6.0, $"INDEXES breakdown Table.CapacityUnits was {Py.Repr(tableUnits)}; expected 6.0");

        var small = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "SET tick = :v",
            ExpressionAttributeValues = Item((":v", N("1"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        u = Units(small.ConsumedCapacity, "small update after shrink");
        Ensure(u == 1.0, $"small update on the shrunken ~0.9 KB item consumed {Py.Repr(u)} WCU; expected 1.0");

        var grow = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "SET big = :b",
            ExpressionAttributeValues = Item((":b", S(Big))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        u = Units(grow.ConsumedCapacity, "growing UpdateItem");
        Ensure(u == 6.0, $"UpdateItem growing ~0.9 KB -> ~5.8 KB consumed {Py.Repr(u)} WCU; expected 6.0");
    }

    private static async Task<string> Table(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("ddbexpr-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }
}
