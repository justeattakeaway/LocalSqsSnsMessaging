// Translated from behavioral/dynamodb/check_partiql_set_delete_and_parameter_sets.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-set-delete-and-parameter-sets</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSetDeleteAndParameterSets
{
    private const string CheckId = "partiql-set-delete-and-parameter-sets";

    /// <summary>Poll DescribeTable until TableStatus is ACTIVE, bounded by the run's remaining time.</summary>
    private static async Task<TableDescription?> WaitTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string tableName)
    {
        var left = ctx.TimeLeftSeconds;
        var budgetS = Math.Max(5.0, Math.Min(90.0, left - 10.0));
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed.TotalSeconds < budgetS)
        {
            last = (await ddb.DescribeTableAsync(tableName)).Table;
            if (last.TableStatus == TableStatus.ACTIVE)
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"table {tableName} never went ACTIVE in {budgetS}s: {Py.Repr(last?.TableStatus)}");
    }

    /// <summary>
    /// PartiQL set_delete removes a member from a String, Number or Binary Set -- as a literal set
    /// or with the set passed as a ? parameter -- and deleting the last member removes the attribute
    /// the same way for every set type; deleting a non-member is a no-op.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-set-delete-and-parameter-sets")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await WaitTableActive(ctx, ddb, t);

        var key = Item(("pk", S("s1")));
        await ddb.PutItemAsync(t, Item(
            ("pk", S("s1")),
            ("tags", SS("a", "b")),
            ("nums", NS("1", "2")),
            ("bindata", BS([0x01], [0x02]))));

        async Task<Dictionary<string, AttributeValue>> Stored() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;

        Task Execute(string statement, params AttributeValue[] parameters) =>
            ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement, Parameters = [.. parameters] });

        // set_delete(tags, <<'a'>>) -- a literal set -- removes one of two String Set members.
        await Execute($"UPDATE \"{t}\" SET tags = set_delete(tags, <<'a'>>) WHERE pk = ?", S("s1"));
        var item = await Stored();
        Ensure(Py.Equal(item["tags"], SS("b")), $"set_delete(tags, <<'a'>>) must leave the set {{'b'}}, got {Py.Repr(item.GetValueOrDefault("tags"))}");

        // set_delete(nums, ?) with the set passed as a parameter removes one of two Number Set members.
        await Execute($"UPDATE \"{t}\" SET nums = set_delete(nums, ?) WHERE pk = ?", NS("1"), S("s1"));
        item = await Stored();
        Ensure(Py.Equal(item["nums"], NS("2")), $"set_delete(nums, ?) with {{NS:[1]}} must leave the set {{2}}, got {Py.Repr(item.GetValueOrDefault("nums"))}");

        // set_add(bindata, ?) with the set passed as a parameter adds a Binary Set member.
        await Execute($"UPDATE \"{t}\" SET bindata = set_add(bindata, ?) WHERE pk = ?", BS([0x03]), S("s1"));
        item = await Stored();
        var members = item["bindata"].BS.Select(b => Convert.ToHexString(b.ToArray())).ToHashSet(StringComparer.Ordinal);
        Ensure(
            members.SetEquals(["01", "02", "03"]),
            $"set_add(bindata, ?) with {{BS:[\\x03]}} must store {{\\x01,\\x02,\\x03}}, got {Py.Repr(item.GetValueOrDefault("bindata"))}");

        // Deleting a value that is not a member is a no-op: the set is unchanged.
        await Execute($"UPDATE \"{t}\" SET tags = set_delete(tags, ?) WHERE pk = ?", SS("not-a-member"), S("s1"));
        item = await Stored();
        Ensure(Py.Equal(item["tags"], SS("b")), $"set_delete of a non-member must leave the set unchanged, got {Py.Repr(item.GetValueOrDefault("tags"))}");

        // Deleting a String Set's last member removes the attribute entirely.
        await Execute($"UPDATE \"{t}\" SET tags = set_delete(tags, ?) WHERE pk = ?", SS("b"), S("s1"));
        item = await Stored();
        Ensure(!item.ContainsKey("tags"), $"deleting a Set's last member must remove the attribute, got {Py.Repr(item)}");

        // Deleting a Number Set's last member removes the attribute entirely, the same way.
        await Execute($"UPDATE \"{t}\" SET nums = set_delete(nums, ?) WHERE pk = ?", NS("2"), S("s1"));
        item = await Stored();
        Ensure(!item.ContainsKey("nums"), $"deleting a Number Set's last member must remove the attribute, got {Py.Repr(item)}");

        // Deleting every remaining Binary Set member removes the attribute entirely, the same way.
        await Execute($"UPDATE \"{t}\" SET bindata = set_delete(bindata, ?) WHERE pk = ?", BS([0x01], [0x02], [0x03]), S("s1"));
        item = await Stored();
        Ensure(!item.ContainsKey("bindata"), $"deleting a Binary Set's last members must remove the attribute, got {Py.Repr(item)}");
        Ensure(Py.Equal(item, Item(("pk", S("s1")))), $"only pk must remain on the item, got {Py.Repr(item)}");
    }
}
