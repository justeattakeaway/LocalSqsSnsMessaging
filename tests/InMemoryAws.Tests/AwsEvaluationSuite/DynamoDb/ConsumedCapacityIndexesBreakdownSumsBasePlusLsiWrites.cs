// Translated from behavioral/dynamodb/check_consumed_capacity_indexes_breakdown_sums_base_plus_lsi_writes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.consumed-capacity-indexes-breakdown-sums-base-plus-lsi-writes</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConsumedCapacityIndexesBreakdownSumsBasePlusLsiWrites
{
    private const string CheckId = "consumed-capacity-indexes-breakdown-sums-base-plus-lsi-writes";
    private static readonly string[] BothIndexes = ["lsi1", "lsi2"];

    /// <summary>
    /// With ReturnConsumedCapacity=INDEXES, a sub-1KB write into two LSIs bills 3.0 (1.0 table
    /// plus 1.0 per LSI entry) and the breakdown sums to the total; a write into no LSI bills 1.0
    /// with no per-index share.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.consumed-capacity-indexes-breakdown-sums-base-plus-lsi-writes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.N),
                new AttributeDefinition("li1", ScalarAttributeType.S),
                new AttributeDefinition("li2", ScalarAttributeType.S),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("li1", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
                new LocalSecondaryIndex
                {
                    IndexName = "lsi2",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("li2", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        // Both index range keys present, so both LSI entries are written.
        var both = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = t,
            Item = Item(("pk", S("a")), ("sk", N("1")), ("li1", S("x")), ("li2", S("y"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        var cc = both.ConsumedCapacity ?? new ConsumedCapacity();
        Ensure(cc.TableName == t, $"ConsumedCapacity must name the table, got {Py.Repr(cc.TableName)}");
        Ensure(
            cc.CapacityUnits == 3.0,
            $"two-LSI write must bill 3.0 total (1 base + 2 index entries), got {Py.Repr(cc.CapacityUnits)}");
        Ensure(cc.Table?.CapacityUnits == 1.0, $"base-table share must be 1.0, got {Py.Repr(cc.Table?.CapacityUnits)}");
        var perIndex = cc.LocalSecondaryIndexes ?? [];
        Ensure(
            perIndex.Keys.Order(StringComparer.Ordinal).SequenceEqual(BothIndexes),
            $"expected a per-LSI breakdown for both indexes, got {Py.Repr(perIndex.Keys.Order(StringComparer.Ordinal).ToList())}");
        foreach (var name in BothIndexes)
        {
            Ensure(
                perIndex[name].CapacityUnits == 1.0,
                $"{name} share must be 1.0, got {Py.Repr(perIndex[name].CapacityUnits)}");
        }

        Ensure(
            cc.Table!.CapacityUnits + perIndex.Values.Sum(v => v.CapacityUnits) == cc.CapacityUnits,
            $"breakdown must sum to the aggregate: {Py.Repr(Boto(cc))}");

        // Omitting both index range keys writes no index entry, so only the base table bills.
        var neither = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = t,
            Item = Item(("pk", S("b")), ("sk", N("2"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        var cc2 = neither.ConsumedCapacity ?? new ConsumedCapacity();
        Ensure(cc2.CapacityUnits == 1.0, $"write projecting into no LSI must bill 1.0, got {Py.Repr(cc2.CapacityUnits)}");
        Ensure(
            cc2.LocalSecondaryIndexes is not { Count: > 0 },
            $"no LSI entry was written, so no per-index share may be reported, got {Py.Repr(Boto(cc2)?.GetValueOrDefault("LocalSecondaryIndexes"))}");
    }

    /// <summary>The ConsumedCapacity as boto3 hands it to the check: a dict of its present members.</summary>
    private static Dictionary<string, object?>? Boto(ConsumedCapacity? cc)
    {
        if (cc is null)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (cc.TableName is not null)
        {
            result["TableName"] = cc.TableName;
        }

        if (cc.CapacityUnits is not null)
        {
            result["CapacityUnits"] = cc.CapacityUnits;
        }

        if (cc.ReadCapacityUnits is not null)
        {
            result["ReadCapacityUnits"] = cc.ReadCapacityUnits;
        }

        if (cc.WriteCapacityUnits is not null)
        {
            result["WriteCapacityUnits"] = cc.WriteCapacityUnits;
        }

        if (cc.Table is not null)
        {
            result["Table"] = Boto(cc.Table);
        }

        if (cc.LocalSecondaryIndexes is not null)
        {
            result["LocalSecondaryIndexes"] = cc.LocalSecondaryIndexes.ToDictionary(e => e.Key, e => (object?)Boto(e.Value), StringComparer.Ordinal);
        }

        if (cc.GlobalSecondaryIndexes is not null)
        {
            result["GlobalSecondaryIndexes"] = cc.GlobalSecondaryIndexes.ToDictionary(e => e.Key, e => (object?)Boto(e.Value), StringComparer.Ordinal);
        }

        return result;
    }

    private static Dictionary<string, object?>? Boto(Capacity? capacity)
    {
        if (capacity is null)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (capacity.ReadCapacityUnits is not null)
        {
            result["ReadCapacityUnits"] = capacity.ReadCapacityUnits;
        }

        if (capacity.WriteCapacityUnits is not null)
        {
            result["WriteCapacityUnits"] = capacity.WriteCapacityUnits;
        }

        if (capacity.CapacityUnits is not null)
        {
            result["CapacityUnits"] = capacity.CapacityUnits;
        }

        return result;
    }
}
