// Translated from behavioral/dynamodb/check_put_item_overwrite_consumed_capacity_follows_larger_item_image.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-overwrite-consumed-capacity-follows-larger-item-image</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemOverwriteConsumedCapacityFollowsLargerItemImage
{
    private const string CheckId = "put-item-overwrite-consumed-capacity-follows-larger-item-image";

    // Item size rule (attribute name bytes + value bytes, summed): pk('pk'+'k')=3, blob('blob'+5600)=5604
    // -> 5607 B total -> ceil(5607/1024) = 6 write units. blob('blob'+900) -> 907 B total -> 1 write unit.
    private static readonly string Big = new('x', 5600);
    private static readonly string Small = new('y', 900);
    private static readonly string Small2 = new('z', 900);

    private static async Task<string> Table(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }

    private static double? Units(PutItemResponse resp, string label)
    {
        var cc = resp.ConsumedCapacity;
        Ensure(cc is not null, $"{label}: no ConsumedCapacity although ReturnConsumedCapacity was set: {Py.Repr(resp.Attributes)}");
        return cc.CapacityUnits;
    }

    /// <summary>
    /// A PutItem that overwrites a ~5.5 KB item with a ~0.9 KB one is charged for the larger,
    /// pre-write image; once both images are ~0.9 KB the charge drops to 1 WCU.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-overwrite-consumed-capacity-follows-larger-item-image")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await Table(ctx, ddb);
        var key = Item(("pk", S("k")));

        var create = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = t,
            Item = Item(("pk", S("k")), ("blob", S(Big))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var u = Units(create, "PutItem creating the ~5.6 KB item");
        Ensure(u == 6.0, $"PutItem of a 5607-byte item with no prior image consumed {Py.Repr(u)} WCU; expected 6.0");

        var created = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item ?? [];
        Ensure(created.GetValueOrDefault("blob")?.S == Big, "the created item must read back whole before the overwrite");

        var overwrite = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = t,
            Item = Item(("pk", S("k")), ("blob", S(Small))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        u = Units(overwrite, "PutItem overwriting the ~5.6 KB item with a ~0.9 KB one");
        Ensure(
            u == 6.0,
            $"PutItem overwriting a 5607-byte item with a 907-byte one consumed {Py.Repr(u)} WCU; expected "
            + "6.0 (the larger, pre-write image). 1.0 means it was metered on the post-write size alone.");

        var shrunk = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item ?? [];
        Ensure(shrunk.GetValueOrDefault("blob")?.S == Small, "the overwrite must persist the smaller image");

        var sameSize = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = t,
            Item = Item(("pk", S("k")), ("blob", S(Small2))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        u = Units(sameSize, "PutItem overwriting a ~0.9 KB item with another ~0.9 KB one");
        Ensure(u == 1.0, $"PutItem overwriting a 907-byte item with another 907-byte one consumed {Py.Repr(u)} WCU; expected 1.0");

        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item ?? [];
        Ensure(final.GetValueOrDefault("blob")?.S == Small2, "the second overwrite must persist its own image");
    }
}
