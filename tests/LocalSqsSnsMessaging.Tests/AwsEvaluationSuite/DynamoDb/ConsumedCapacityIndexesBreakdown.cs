// Translated from behavioral/dynamodb/check_consumed_capacity_indexes_breakdown.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.consumed-capacity-indexes-breakdown</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConsumedCapacityIndexesBreakdown
{
    private const string CheckId = "consumed-capacity-indexes-breakdown";
    private const string Index = "gsi-by-cat";

    /// <summary>
    /// ReturnConsumedCapacity=INDEXES reports per-index capacity: a write to a GSI table charges Table and
    /// the GSI, a Query served by the GSI charges the GSI and no Table capacity, the same Query with TOTAL
    /// returns only the aggregate, and NONE returns no ConsumedCapacity.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.consumed-capacity-indexes-breakdown")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-idx-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("cat", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = [new KeySchemaElement("cat", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        // --- a base-table write fans out to the GSI: both must be itemized ---
        var put = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = t,
            Item = Item(("pk", S("i1")), ("cat", S("c1")), ("payload", S("p"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        var cc = put.ConsumedCapacity;
        Ensure(cc is not null, "ReturnConsumedCapacity=INDEXES must return ConsumedCapacity");
        Ensure(cc.TableName == t, $"ConsumedCapacity.TableName must be {t}, got {cc.TableName}");
        Ensure((cc.CapacityUnits ?? 0) > 0, $"aggregate CapacityUnits must be > 0, got {Py.Repr(cc.CapacityUnits)}");
        Ensure(cc.Table is not null, $"INDEXES must itemize the base table under \"Table\", got members {Py.Repr(Members(cc))}");
        Ensure(
            (cc.Table.CapacityUnits ?? 0) > 0,
            $"base-table CapacityUnits must be > 0 for a table write, got {Py.Repr(Boto(cc.Table))}");
        Ensure(
            cc.GlobalSecondaryIndexes is not null,
            $"INDEXES must itemize the GSI fan-out under \"GlobalSecondaryIndexes\", got members {Py.Repr(Members(cc))}");
        Ensure(
            cc.GlobalSecondaryIndexes.ContainsKey(Index),
            $"the breakdown must be keyed by index name {Index}, got {Py.Repr(cc.GlobalSecondaryIndexes.Keys.Order(StringComparer.Ordinal).ToList())}");
        Ensure(
            (cc.GlobalSecondaryIndexes[Index].CapacityUnits ?? 0) > 0,
            $"a write that fans out to {Index} must charge that index; got {Py.Repr(Boto(cc.GlobalSecondaryIndexes[Index]))}");

        // --- a Query SERVED BY the GSI charges the index, not the base table ---
        Task<QueryResponse> GsiQuery(ReturnConsumedCapacity rcc) => ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            IndexName = Index,
            KeyConditionExpression = "cat = :c",
            ExpressionAttributeValues = Item((":c", S("c1"))),
            ReturnConsumedCapacity = rcc,
        });

        // GSIs are eventually consistent; wait for the item to land before asserting capacity.
        var indexed = await Poll(
            () => GsiQuery(ReturnConsumedCapacity.INDEXES),
            r => r.Count == 1,
            timeoutSeconds: 90,
            intervalSeconds: 2.0,
            description: $"item propagated into {Index}");
        cc = indexed.ConsumedCapacity;
        Ensure(cc is not null, "a GSI Query with INDEXES must return ConsumedCapacity");
        Ensure(cc.TableName == t, $"ConsumedCapacity.TableName must be {t}, got {cc.TableName}");
        Ensure(
            cc.GlobalSecondaryIndexes is not null && cc.GlobalSecondaryIndexes.ContainsKey(Index),
            $"a Query on {Index} must itemize that index, got members {Py.Repr(Members(cc))}");
        Ensure(
            (cc.GlobalSecondaryIndexes[Index].CapacityUnits ?? 0) > 0,
            $"the index read must be charged, got {Py.Repr(Boto(cc.GlobalSecondaryIndexes[Index]))}");
        Ensure(
            (cc.Table?.CapacityUnits ?? 0) == 0,
            $"a Query served by an ALL-projection GSI reads no base-table item and must charge no base-table capacity, got Table={Py.Repr(Boto(cc.Table))}");

        // --- =TOTAL returns the aggregate with NO breakdown members ---
        var total = await GsiQuery(ReturnConsumedCapacity.TOTAL);
        cc = total.ConsumedCapacity;
        Ensure(cc is not null, "a GSI Query with TOTAL must return ConsumedCapacity");
        Ensure((cc.CapacityUnits ?? 0) > 0, $"TOTAL must report a positive aggregate, got {Py.Repr(Boto(cc))}");
        Ensure(
            cc.GlobalSecondaryIndexes is null,
            $"TOTAL must NOT include the per-index breakdown, got {Py.Repr(cc.GlobalSecondaryIndexes?.ToDictionary(e => e.Key, e => (object?)Boto(e.Value), StringComparer.Ordinal))}");
        Ensure(cc.Table is null, $"TOTAL must NOT include the per-table breakdown, got {Py.Repr(Boto(cc.Table))}");

        // --- =NONE on the same call omits ConsumedCapacity entirely ---
        var noneResp = await GsiQuery(ReturnConsumedCapacity.NONE);
        Ensure(noneResp.ConsumedCapacity is null, $"NONE must omit ConsumedCapacity, got {Py.Repr(Boto(noneResp.ConsumedCapacity))}");
    }

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutSeconds, double intervalSeconds = 1.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (clock.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                throw Fail($"{description} not met within {timeoutSeconds}s. Last: {Py.Repr(val)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
            val = await fn();
        }

        return val;
    }

    /// <summary><c>sorted(cc)</c>: the members of the ConsumedCapacity that are present.</summary>
    private static List<string> Members(ConsumedCapacity cc) => [.. Boto(cc)!.Keys.Order(StringComparer.Ordinal)];

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
