// Translated from behavioral/dynamodb/check_update_set_replaces_the_existing_value_without_merging.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-set-replaces-the-existing-value-without-merging</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateSetReplacesTheExistingValueWithoutMerging
{
    // (stored value, value SET over it)
    private static (string Case, AttributeValue Old, AttributeValue New)[] Over() =>
    [
        ("s-to-n", S("x"), N("5")),
        ("m-to-m", M(("a", S("1"))), M(("b", S("2")))),
        ("m-nested", M(("a", M(("x", S("1"))))), M(("a", M(("y", S("2")))))),
        ("l-to-m", L(S("1")), M(("b", S("2")))),
        ("l-to-l", L(S("1"), S("2")), L(S("3"))),
    ];

    /// <summary>
    /// UpdateItem SET over an attribute the item holds replaces the whole value (S to N, map to map,
    /// nested map, list to map, list to list), alone or with an ADD in the same update; no merge.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.UpdateExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-set-replaces-the-existing-value-without-merging")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-set-replace-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        foreach (var (@case, old, @new) in Over())
        {
            foreach (var withAdd in new[] { false, true })
            {
                var pk = $"{@case}-{(withAdd ? "add" : "alone")}";
                var key = Item(("pk", S(pk)));
                await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("s", old), ("keep", S("k"))));
                var names = Names(("#s", "s"));
                var values = Item((":s", @new));
                var expression = "SET #s = :s";
                if (withAdd)
                {
                    names["#n"] = "n";
                    values[":one"] = N("1");
                    expression += " ADD #n :one";
                }

                await ddb.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = table,
                    Key = key,
                    UpdateExpression = expression,
                    ExpressionAttributeNames = names,
                    ExpressionAttributeValues = values,
                });
                var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
                var want = Item(("pk", S(pk)), ("s", @new), ("keep", S("k")));
                if (withAdd)
                {
                    want["n"] = N("1");
                }

                Ensure(Py.Equal(got, want), $"{Py.Repr(expression)} over s = {Py.Repr(old)} left {Py.Repr(got)}, expected {Py.Repr(want)}");
            }
        }
    }

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            try
            {
                await undo();
                return;
            }
            catch (AmazonDynamoDBException ex)
            {
                if (notFound.Contains(ex.ErrorCode, StringComparer.Ordinal))
                {
                    return;
                }

                if (ex.ErrorCode != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(300))
                {
                    throw;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0));
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }
}
