// Translated from behavioral/dynamodb/check_consumed_capacity_indexes_breakdown_sums_to_total_over_lsi_fanout.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.consumed-capacity-indexes-breakdown-sums-to-total-over-lsi-fanout</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConsumedCapacityIndexesBreakdownSumsToTotalOverLsiFanout
{
    private const string CheckId = "consumed-capacity-indexes-breakdown-sums-to-total-over-lsi-fanout";
    private const string Lsi1 = "lsi1";
    private const string Lsi2 = "lsi2";
    private static readonly string[] AttributeNames = ["pk", "sk", "l1", "l2"];
    private static readonly string[] BothIndexes = [Lsi1, Lsi2];
    private static readonly string[] OneIndex = [Lsi1];
    private static readonly string[] WriteKeys = ["both", "one", "none"];

    /// <summary>
    /// ReturnConsumedCapacity=INDEXES bills 1.0 WCU per LSI entry written, itemizes only touched
    /// indexes, and the itemized breakdown sums to the total across writes supplying both, one, or no
    /// LSI keys.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.consumed-capacity-indexes-breakdown-sums-to-total-over-lsi-fanout")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-lsicc-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [.. AttributeNames.Select(n => new AttributeDefinition(n, ScalarAttributeType.S))],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = Lsi1,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("l1", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
                new LocalSecondaryIndex
                {
                    IndexName = Lsi2,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("l2", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        async Task<ConsumedCapacity> Put(string key, params (string Name, AttributeValue Value)[] extra)
        {
            var item = Item(("pk", S(key)), ("sk", S("only")));
            foreach (var (name, value) in extra)
            {
                item[name] = value;
            }

            var resp = await ddb.PutItemAsync(new PutItemRequest { TableName = t, Item = item, ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES });
            var cc = resp.ConsumedCapacity;
            Ensure(
                cc is not null,
                $"ReturnConsumedCapacity=INDEXES must return ConsumedCapacity for the {Py.Repr(key)} write, got response members {Py.Repr(ResponseMembers(resp))}");
            return cc;
        }

        var both = await Put("both", ("l1", S("a")), ("l2", S("b")));
        var one = await Put("one", ("l1", S("a")));
        var none = await Put("none");

        Ensure(
            both.CapacityUnits == 3.0,
            $"a <=1KB write supplying both LSI range keys writes 3 index entries (base + 2 LSIs) and must bill exactly 3.0 WCU, got {Py.Repr(both.CapacityUnits)}");
        Ensure(
            one.CapacityUnits == 2.0,
            $"a <=1KB write supplying one LSI range key writes 2 index entries and must bill exactly 2.0 WCU, got {Py.Repr(one.CapacityUnits)}");
        Ensure(
            none.CapacityUnits == 1.0,
            $"a <=1KB write supplying no LSI range key writes only the base item and must bill exactly 1.0 WCU, got {Py.Repr(none.CapacityUnits)}");

        // An LSI fan-out never re-charges the base item.
        foreach (var (label, cc) in new[] { ("both", both), ("one", one), ("none", none) })
        {
            Ensure(
                cc.Table?.CapacityUnits == 1.0,
                $"the {Py.Repr(label)} write stores exactly one base item and must itemize the base table at 1.0 WCU, got Table={Py.Repr(Boto(cc.Table))}");
        }

        Ensure(
            SortedIndexes(both).SequenceEqual(BothIndexes),
            $"a write supplying both LSI range keys must itemize both indexes under LocalSecondaryIndexes, got {Py.Repr(SortedIndexes(both))}");
        foreach (var name in BothIndexes)
        {
            Ensure(
                both.LocalSecondaryIndexes[name].CapacityUnits == 1.0,
                $"each LSI entry written costs exactly 1.0 WCU; {name} reported {Py.Repr(Boto(both.LocalSecondaryIndexes[name]))}");
        }

        Ensure(
            SortedIndexes(one).SequenceEqual(OneIndex),
            $"a write supplying only the {Lsi1} range key must itemize {Lsi1} and nothing else, got {Py.Repr(SortedIndexes(one))}");
        Ensure(
            none.LocalSecondaryIndexes is null,
            $"a write that creates no index entry must carry NO per-index breakdown member, got {Py.Repr(Boto(none)?.GetValueOrDefault("LocalSecondaryIndexes"))}");

        foreach (var (label, cc) in new[] { ("both", both), ("one", one), ("none", none) })
        {
            var parts = cc.Table!.CapacityUnits + (cc.LocalSecondaryIndexes ?? []).Values.Sum(m => m.CapacityUnits);
            Ensure(
                parts == cc.CapacityUnits,
                $"the {Py.Repr(label)} write reported an itemized breakdown summing to {Py.Repr(parts)} against an aggregate CapacityUnits of {Py.Repr(cc.CapacityUnits)}; the two must agree");
        }

        Ensure(
            none.CapacityUnits < one.CapacityUnits && one.CapacityUnits < both.CapacityUnits,
            $"each additional index entry must cost exactly one more unit; got none={Py.Repr(none.CapacityUnits)} one={Py.Repr(one.CapacityUnits)} both={Py.Repr(both.CapacityUnits)}");

        foreach (var key in WriteKeys)
        {
            var got = await ddb.GetItemAsync(new GetItemRequest
            {
                TableName = t,
                Key = Item(("pk", S(key)), ("sk", S("only"))),
                ConsistentRead = true,
            });
            Ensure(
                got.Item is not null && got.Item.TryGetValue("pk", out var pk) && Py.Equal(pk, S(key)),
                $"the billed {Py.Repr(key)} write must be readable, so the capacity above was charged for real work; got {Py.Repr(got.Item)}");
        }

        var idx = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            IndexName = Lsi1,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("both"))),
            ConsistentRead = true,
        });
        Ensure(
            idx.Count == 1,
            $"the 1.0 WCU billed against {Lsi1} must correspond to a real index entry, but querying {Lsi1} returned {Py.Repr(idx.Count)} items: {Py.Repr(idx.Items)}");
    }

    private static List<string> SortedIndexes(ConsumedCapacity cc) => [.. (cc.LocalSecondaryIndexes ?? []).Keys.Order(StringComparer.Ordinal)];

    private static List<string> ResponseMembers(PutItemResponse resp)
    {
        var members = new List<string> { "ResponseMetadata" };
        if (resp.Attributes is not null)
        {
            members.Add("Attributes");
        }

        if (resp.ConsumedCapacity is not null)
        {
            members.Add("ConsumedCapacity");
        }

        if (resp.ItemCollectionMetrics is not null)
        {
            members.Add("ItemCollectionMetrics");
        }

        members.Sort(StringComparer.Ordinal);
        return members;
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
