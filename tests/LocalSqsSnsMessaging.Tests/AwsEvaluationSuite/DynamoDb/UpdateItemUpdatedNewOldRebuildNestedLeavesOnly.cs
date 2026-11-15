// Translated from behavioral/dynamodb/check_update_item_updated_new_old_rebuild_nested_leaves_only.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-updated-new-old-rebuild-nested-leaves-only</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemUpdatedNewOldRebuildNestedLeavesOnly
{
    /// <summary>
    /// UPDATED_NEW/UPDATED_OLD on nested map and list-index SETs return only the touched leaves
    /// rebuilt under their parents (siblings omitted, one-element lists), with old or new values.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateItem.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-updated-new-old-rebuild-nested-leaves-only")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-rvnest-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        var key = Item(("pk", S("doc")));
        await ddb.PutItemAsync(table, Item(
            ("pk", S("doc")),
            ("a", M(("b", M(("c", S("c0")), ("d", S("d0")))), ("e", S("e0")), ("f", S("f0")))),
            ("mylist", L(S("x0"), S("x1"), S("x2"))),
            ("other", S("o0"))));

        async Task<Dictionary<string, AttributeValue>?> Update(string expr, Dictionary<string, AttributeValue> values, ReturnValue rv, Dictionary<string, string>? names = null)
        {
            var request = new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = expr,
                ExpressionAttributeValues = values,
                ReturnValues = rv,
            };
            if (names is { Count: > 0 })
            {
                request.ExpressionAttributeNames = names;
            }

            return (await ddb.UpdateItemAsync(request)).Attributes;
        }

        var got = await Update("SET a.b.c = :v", Item((":v", S("c1"))), ReturnValue.UPDATED_NEW);
        var want = Item(("a", M(("b", M(("c", S("c1")))))));
        Ensure(Py.Equal(got, want), $"SET a.b.c with UPDATED_NEW returned {Py.Repr(got)}, expected {Py.Repr(want)}");

        got = await Update("SET a.b.c = :v", Item((":v", S("c2"))), ReturnValue.UPDATED_OLD);
        want = Item(("a", M(("b", M(("c", S("c1")))))));
        Ensure(Py.Equal(got, want), $"SET a.b.c with UPDATED_OLD returned {Py.Repr(got)}, expected {Py.Repr(want)}");

        got = await Update("SET a.b.c = :c, a.e = :e", Item((":c", S("c3")), (":e", S("e1"))), ReturnValue.UPDATED_NEW);
        want = Item(("a", M(("b", M(("c", S("c3")))), ("e", S("e1")))));
        Ensure(Py.Equal(got, want), $"SET a.b.c, a.e with UPDATED_NEW returned {Py.Repr(got)}, expected {Py.Repr(want)}");

        got = await Update(
            "SET #a.#b.#c = :c, #a.#e = :e",
            Item((":c", S("c4")), (":e", S("e2"))),
            ReturnValue.UPDATED_OLD,
            names: Names(("#a", "a"), ("#b", "b"), ("#c", "c"), ("#e", "e")));
        want = Item(("a", M(("b", M(("c", S("c3")))), ("e", S("e1")))));
        Ensure(Py.Equal(got, want), $"aliased SET of two sub-paths with UPDATED_OLD returned {Py.Repr(got)}, expected {Py.Repr(want)}");

        got = await Update("SET mylist[1] = :v", Item((":v", S("y1"))), ReturnValue.UPDATED_NEW);
        want = Item(("mylist", L(S("y1"))));
        Ensure(Py.Equal(got, want), $"SET mylist[1] with UPDATED_NEW returned {Py.Repr(got)}, expected {Py.Repr(want)}");

        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        want = Item(
            ("pk", S("doc")),
            ("a", M(("b", M(("c", S("c4")), ("d", S("d0")))), ("e", S("e2")), ("f", S("f0")))),
            ("mylist", L(S("x0"), S("y1"), S("x2"))),
            ("other", S("o0")));
        Ensure(Py.Equal(item, want), $"after the nested updates the item is {Py.Repr(item)}, expected {Py.Repr(want)}");
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

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0));
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }
}
