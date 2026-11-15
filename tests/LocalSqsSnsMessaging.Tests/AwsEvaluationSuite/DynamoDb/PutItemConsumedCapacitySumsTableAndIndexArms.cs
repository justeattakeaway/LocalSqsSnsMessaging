// Translated from behavioral/dynamodb/check_put_item_consumed_capacity_sums_table_and_index_arms.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-consumed-capacity-sums-table-and-index-arms</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemConsumedCapacitySumsTableAndIndexArms
{
    /// <summary>A Capacity as boto3 hands it to the check: <c>{'CapacityUnits': 1.0}</c>.</summary>
    private static Dictionary<string, object?>? Cap(Capacity? capacity)
    {
        if (capacity is null)
        {
            return null;
        }

        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (capacity.ReadCapacityUnits is { } r)
        {
            d["ReadCapacityUnits"] = r;
        }

        if (capacity.WriteCapacityUnits is { } w)
        {
            d["WriteCapacityUnits"] = w;
        }

        if (capacity.CapacityUnits is { } c)
        {
            d["CapacityUnits"] = c;
        }

        return d;
    }

    private static Dictionary<string, object?>? Caps(Dictionary<string, Capacity>? arms) =>
        arms?.ToDictionary(a => a.Key, a => (object?)Cap(a.Value), StringComparer.Ordinal);

    /// <summary>
    /// A write that lands in both indexes reports a table arm and both index arms, and the total
    /// is their sum -- so an emulator reporting only the table write is short.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-consumed-capacity-sums-table-and-index-arms")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.N),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var cc = (await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
            Item = Item(("pk", S("a")), ("sk", S("1")), ("gpk", S("g1")), ("lsk", N("5"))),
        })).ConsumedCapacity;
        var tableArm = cc.Table?.CapacityUnits ?? 0;
        var gsiArm = cc.GlobalSecondaryIndexes?.GetValueOrDefault("gsi1")?.CapacityUnits ?? 0;
        var lsiArm = cc.LocalSecondaryIndexes?.GetValueOrDefault("lsi1")?.CapacityUnits ?? 0;
        Ensure(tableArm > 0, $"expected a table arm, got {Py.Repr(Cap(cc.Table))}");
        Ensure(gsiArm > 0, $"expected a gsi1 arm, got {Py.Repr(Caps(cc.GlobalSecondaryIndexes))}");
        Ensure(lsiArm > 0, $"expected an lsi1 arm, got {Py.Repr(Caps(cc.LocalSecondaryIndexes))}");
        Ensure(
            cc.CapacityUnits == tableArm + gsiArm + lsiArm,
            $"total {Py.Repr(cc.CapacityUnits)} must equal {Py.Repr(tableArm)} + {Py.Repr(gsiArm)} + {Py.Repr(lsiArm)}");
    }
}
