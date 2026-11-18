// Translated from behavioral/dynamodb/check_include_projection_governs_index_reads_on_lsi_and_gsi.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.include-projection-governs-index-reads-on-lsi-and-gsi</summary>
[Category("AwsEvaluationSuite")]
public sealed class IncludeProjectionGovernsIndexReadsOnLsiAndGsi
{
    private const string CheckId = "include-projection-governs-index-reads-on-lsi-and-gsi";
    private const string LsiInclude = "lsi-include";
    private const string LsiAll = "lsi-all";
    private const string GsiInclude = "gsi-include";
    private const string GsiKeysOnly = "gsi-keys-only";
    private static readonly string[] AttributeNames = ["pk", "sk", "li", "la", "gi", "gk"];
    private static readonly string[] LsiIncludeMembers = ["kept_l", "li", "pk", "sk"];
    private static readonly string[] GsiIncludeMembers = ["gi", "kept_g", "pk", "sk"];
    private static readonly string[] KeysOnlyMembers = ["gk", "pk", "sk"];

    /// <summary>
    /// Poll <paramref name="fn"/> until <paramref name="pred"/> holds. A global secondary index is eventually consistent, so this
    /// waits for an observable condition rather than sleeping a fixed amount.
    /// </summary>
    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutSeconds, double intervalSeconds = 2.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        var value = await fn();
        while (!pred(value))
        {
            if (clock.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw Fail($"{description} not met within {timeoutSeconds}s. Last: {Py.Repr(value)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
            value = await fn();
        }

        return value;
    }

    /// <summary>A projection as boto3 hands it back: a dict with only the members the response carried.</summary>
    private static Dictionary<string, object?>? Proj(Projection? projection)
    {
        if (projection is null)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (projection.ProjectionType is not null)
        {
            result["ProjectionType"] = projection.ProjectionType.Value;
        }

        if (projection.NonKeyAttributes is not null)
        {
            result["NonKeyAttributes"] = projection.NonKeyAttributes;
        }

        return result;
    }

    private static List<string> SortedKeys(Dictionary<string, AttributeValue> item) => [.. item.Keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// DescribeTable echoes INCLUDE projections with NonKeyAttributes (none for ALL/KEYS_ONLY), and
    /// INCLUDE LSI/GSI queries return only keys plus the included attribute, while the ALL LSI returns
    /// the excluded one and the KEYS_ONLY GSI returns keys only.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.include-projection-governs-index-reads-on-lsi-and-gsi")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-proj-");
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
                    IndexName = LsiInclude,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("li", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["kept_l"] },
                },
                new LocalSecondaryIndex
                {
                    IndexName = LsiAll,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("la", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = GsiInclude,
                    KeySchema = [new KeySchemaElement("gi", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["kept_g"] },
                },
                new GlobalSecondaryIndex
                {
                    IndexName = GsiKeysOnly,
                    KeySchema = [new KeySchemaElement("gk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        // --- the declaration: DescribeTable echoes INCLUDE with its list, and nothing for the others ---
        var desc = (await ddb.DescribeTableAsync(t)).Table;
        var lsis = desc.LocalSecondaryIndexes.ToDictionary(i => i.IndexName, i => Proj(i.Projection), StringComparer.Ordinal);
        var gsis = desc.GlobalSecondaryIndexes.ToDictionary(i => i.IndexName, i => Proj(i.Projection), StringComparer.Ordinal);

        Ensure(
            Py.Equal(lsis.GetValueOrDefault(LsiInclude), new Dictionary<string, object?> { ["ProjectionType"] = "INCLUDE", ["NonKeyAttributes"] = new List<string> { "kept_l" } }),
            $"DescribeTable must echo the LSI INCLUDE projection together with its NonKeyAttributes, got {Py.Repr(lsis.GetValueOrDefault(LsiInclude))}");
        Ensure(
            Py.Equal(gsis.GetValueOrDefault(GsiInclude), new Dictionary<string, object?> { ["ProjectionType"] = "INCLUDE", ["NonKeyAttributes"] = new List<string> { "kept_g" } }),
            $"DescribeTable must echo the GSI INCLUDE projection together with its NonKeyAttributes, got {Py.Repr(gsis.GetValueOrDefault(GsiInclude))}");
        Ensure(
            Py.Equal(lsis.GetValueOrDefault(LsiAll), new Dictionary<string, object?> { ["ProjectionType"] = "ALL" }),
            $"an ALL projection names no attributes, so DescribeTable must carry no NonKeyAttributes member for it, got {Py.Repr(lsis.GetValueOrDefault(LsiAll))}");
        Ensure(
            Py.Equal(gsis.GetValueOrDefault(GsiKeysOnly), new Dictionary<string, object?> { ["ProjectionType"] = "KEYS_ONLY" }),
            $"a KEYS_ONLY projection names no attributes, so DescribeTable must carry no NonKeyAttributes member for it, got {Py.Repr(gsis.GetValueOrDefault(GsiKeysOnly))}");

        // --- one item carrying every attribute the projections divide up ---
        await ddb.PutItemAsync(t, Item(
            ("pk", S("p")), ("sk", S("s")),
            ("li", S("lv")), ("la", S("av")), ("gi", S("gv")), ("gk", S("kv")),
            ("kept_l", S("KL")), ("kept_g", S("KG")), ("excluded", S("X"))));
        var baseItem = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("p")), ("sk", S("s"))), ConsistentRead = true })).Item ?? [];
        Ensure(
            Py.Equal(baseItem.GetValueOrDefault("excluded"), S("X")),
            $"the base item must really carry 'excluded', or the absences asserted below are vacuous: {Py.Repr(baseItem)}");

        // --- the INCLUDE LSI serves exactly the projected attribute set ---
        var lsiItem = (await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            IndexName = LsiInclude,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("p"))),
            ConsistentRead = true,
        })).Items[0];
        Ensure(
            SortedKeys(lsiItem).SequenceEqual(LsiIncludeMembers),
            $"an INCLUDE LSI projects the table key, its own range key and the named attributes and nothing else, got {Py.Repr(SortedKeys(lsiItem))}");
        Ensure(Py.Equal(lsiItem.GetValueOrDefault("kept_l"), S("KL")), $"the INCLUDEd attribute must be projected with its value, got {Py.Repr(lsiItem.GetValueOrDefault("kept_l"))}");
        Ensure(!lsiItem.ContainsKey("excluded"), $"an attribute outside the INCLUDE list must not be served by the index, got {Py.Repr(lsiItem)}");

        // --- the ALL LSI on the same item does serve it: the absence above is the projection's doing ---
        var allItem = (await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            IndexName = LsiAll,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("p"))),
            ConsistentRead = true,
        })).Items[0];
        Ensure(
            Py.Equal(allItem.GetValueOrDefault("excluded"), S("X")),
            $"control: an ALL projection over the same item must serve the excluded attribute, so the INCLUDE absences are attributable to the projection and not to index reads generally; got {Py.Repr(allItem)}");

        // --- the same rule on a global secondary index, once it has caught up ---
        var gsiResp = await Poll(
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = t,
                IndexName = GsiInclude,
                KeyConditionExpression = "gi = :g",
                ExpressionAttributeValues = Item((":g", S("gv"))),
            }),
            r => r.Count == 1,
            timeoutSeconds: 120,
            description: $"the item propagating into {GsiInclude}");
        var gsiItem = gsiResp.Items[0];
        Ensure(
            SortedKeys(gsiItem).SequenceEqual(GsiIncludeMembers),
            $"an INCLUDE GSI projects the table key, its own key and the named attributes and nothing else, got {Py.Repr(SortedKeys(gsiItem))}");
        Ensure(Py.Equal(gsiItem.GetValueOrDefault("kept_g"), S("KG")), $"the INCLUDEd attribute must be projected with its value, got {Py.Repr(gsiItem.GetValueOrDefault("kept_g"))}");
        Ensure(!gsiItem.ContainsKey("excluded"), $"an attribute outside the GSI INCLUDE list must not be served by the index, got {Py.Repr(gsiItem)}");

        var keysResp = await Poll(
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = t,
                IndexName = GsiKeysOnly,
                KeyConditionExpression = "gk = :g",
                ExpressionAttributeValues = Item((":g", S("kv"))),
            }),
            r => r.Count == 1,
            timeoutSeconds: 120,
            description: $"the item propagating into {GsiKeysOnly}");
        var keysItem = keysResp.Items[0];
        Ensure(
            SortedKeys(keysItem).SequenceEqual(KeysOnlyMembers),
            $"a KEYS_ONLY GSI projects the table key and its own key and nothing else, got {Py.Repr(SortedKeys(keysItem))}");
    }
}
