// Translated from behavioral/dynamodb/check_partiql_set_add_literal_ss_and_ns.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-set-add-literal-ss-and-ns</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSetAddLiteralSsAndNs
{
    private const string CheckId = "partiql-set-add-literal-ss-and-ns";

    /// <summary>Poll DescribeTable until TableStatus is ACTIVE, bounded by the run's remaining time.</summary>
    private static async Task<TableDescription> WaitTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string tableName)
    {
        var budgetS = Math.Max(5.0, Math.Min(90.0, ctx.TimeLeftSeconds - 10.0));
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

        throw Fail($"table {tableName} never went ACTIVE in {Py.Repr(budgetS)}s: {last}");
    }

    private static bool SetEquals(List<string>? members, params string[] expected) =>
        members is not null && members.ToHashSet(StringComparer.Ordinal).SetEquals(expected);

    /// <summary>
    /// PartiQL UPDATE ... SET s = set_add(s, &lt;&lt;...&gt;&gt;) on a String Set and a Number Set stores the
    /// union after each call, and adding a member already present leaves the set unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-set-add-literal-ss-and-ns")]
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
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"INSERT INTO \"{t}\" VALUE {{'pk': ?, 'tags': ?, 'nums': ?}}",
            Parameters = [S("s1"), SS("a"), NS("10")],
        });

        async Task<Dictionary<string, AttributeValue>> Stored() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;

        // set_add(tags, <<'b'>>) on a String Set adds the new member.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"UPDATE \"{t}\" SET tags = set_add(tags, <<'b'>>) WHERE pk = ?", Parameters = [S("s1")] });
        var item = await Stored();
        Ensure(SetEquals(item["tags"].SS, "a", "b"), $"set_add(tags, <<'b'>>) must store {{'a','b'}}, got {Py.Repr(item.GetValueOrDefault("tags"))}");

        // Adding a member already present is a no-op: the stored set is unchanged.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"UPDATE \"{t}\" SET tags = set_add(tags, <<'b'>>) WHERE pk = ?", Parameters = [S("s1")] });
        item = await Stored();
        Ensure(SetEquals(item["tags"].SS, "a", "b"), $"re-adding the already-present member 'b' must leave the set unchanged, got {Py.Repr(item.GetValueOrDefault("tags"))}");

        // set_add(nums, <<20>>) on a Number Set adds the new member.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"UPDATE \"{t}\" SET nums = set_add(nums, <<20>>) WHERE pk = ?", Parameters = [S("s1")] });
        item = await Stored();
        Ensure(SetEquals(item["nums"].NS, "10", "20"), $"set_add(nums, <<20>>) must store {{10,20}}, got {Py.Repr(item.GetValueOrDefault("nums"))}");

        // Adding an already-present numeric member is likewise a no-op.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"UPDATE \"{t}\" SET nums = set_add(nums, <<20>>) WHERE pk = ?", Parameters = [S("s1")] });
        item = await Stored();
        Ensure(SetEquals(item["nums"].NS, "10", "20"), $"re-adding the already-present member 20 must leave the set unchanged, got {Py.Repr(item.GetValueOrDefault("nums"))}");
    }
}
