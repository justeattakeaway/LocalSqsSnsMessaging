// Translated from behavioral/dynamodb/check_put_item_consumed_capacity_pins_1024_byte_boundary_and_overwrite_follows_larger_image.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-consumed-capacity-pins-1024-byte-boundary-and-overwrite-follows-larger-image</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemConsumedCapacityPins1024ByteBoundaryAndOverwriteFollowsLargerImage
{
    private const string Check = "put-item-consumed-capacity-pins-1024-byte-boundary-and-overwrite-follows-larger-image";

    // Item size = sum of (attribute name bytes + value bytes), key attributes included.
    // "pk"(2) + "k"(1) + "v"(1) + X = 4 + X -> X=1020 totals exactly 1024 B; X=1021 totals exactly 1025 B.
    private static readonly string At1024 = new('x', 1020);
    private static readonly string At1025 = new('y', 1021);

    /// <summary>
    /// A 1024-byte PutItem costs 1 WCU and a 1025-byte one costs 2; overwriting the 1025-byte
    /// item with a 1024-byte one still costs 2, the larger of the two images either side of the write.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-consumed-capacity-pins-1024-byte-boundary-and-overwrite-follows-larger-image")]
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
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("k")));

        Task<PutItemResponse> Put(string v) => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("k")), ("v", S(v))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });

        async Task<string?> ReadV() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item?.GetValueOrDefault("v")?.S;

        var create = await Put(At1024);
        var u = Units(create, "PutItem creating the exact 1024-byte item");
        Ensure(u == 1.0, $"a 1024-byte item with no prior image consumed {Py.Repr(u)} WCU; expected 1.0");

        Ensure(await ReadV() == At1024, "the 1024-byte item must read back whole");

        var grow = await Put(At1025);
        u = Units(grow, "PutItem overwriting the 1024-byte item with a 1025-byte one");
        Ensure(u == 2.0, $"overwriting a 1024-byte item with a 1025-byte one consumed {Py.Repr(u)} WCU; expected 2.0");

        Ensure(await ReadV() == At1025, "the 1025-byte overwrite must persist");

        var shrink = await Put(At1024);
        u = Units(shrink, "PutItem overwriting the 1025-byte item with a 1024-byte one");
        Ensure(
            u == 2.0,
            $"overwriting a 1025-byte item with a 1024-byte one consumed {Py.Repr(u)} WCU; expected 2.0 " +
            "(the larger, pre-write image). 1.0 means it was metered on the post-write size alone.");

        Ensure(await ReadV() == At1024, "the shrink overwrite must persist the 1024-byte image");
    }

    private static double? Units(PutItemResponse resp, string label)
    {
        var cc = resp.ConsumedCapacity;
        Ensure(cc is not null, $"{label}: no ConsumedCapacity although ReturnConsumedCapacity was set: {resp}");
        return cc.CapacityUnits;
    }
}
