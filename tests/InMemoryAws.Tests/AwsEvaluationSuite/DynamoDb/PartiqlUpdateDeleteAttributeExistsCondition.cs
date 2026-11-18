// Translated from behavioral/dynamodb/check_partiql_update_delete_attribute_exists_condition.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-update-delete-attribute-exists-condition</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlUpdateDeleteAttributeExistsCondition
{
    private const string CheckId = "partiql-update-delete-attribute-exists-condition";
    private const string ErrCcf = "ConditionalCheckFailedException";
    private const string MsgCcf = "The conditional request failed";

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

    /// <summary>Call fn(); return (code, message), raising on an unexpected success.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>
    /// A PartiQL UPDATE/DELETE WHERE clause testing attribute_exists/attribute_not_exists on a
    /// non-key attribute acts as a write condition: the write applies when the predicate holds and
    /// fails with ConditionalCheckFailedException, item unchanged, when it does not.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-update-delete-attribute-exists-condition")]
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

        var key = Item(("pk", S("p1")));
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("flag", S("active")), ("amount", N("1"))));

        Task<ExecuteStatementResponse> Execute(string statement, params AttributeValue[] parameters) =>
            ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement, Parameters = [.. parameters] });

        async Task<Dictionary<string, AttributeValue>?> Get() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;

        // attribute_exists(flag) holds (flag is present): the UPDATE applies.
        await Execute($"UPDATE \"{t}\" SET amount = ? WHERE pk = ? AND attribute_exists(flag)", N("2"), S("p1"));
        var item = (await Get())!;
        Ensure(Py.Equal(item["amount"], N("2")), $"UPDATE must apply once attribute_exists(flag) holds, got amount={Py.Repr(item.GetValueOrDefault("amount"))}");

        // attribute_not_exists(flag) does not hold (flag is still present): the UPDATE is refused and
        // the item is left exactly as the previous successful write left it.
        var (code, msg) = await Error(() => Execute($"UPDATE \"{t}\" SET amount = ? WHERE pk = ? AND attribute_not_exists(flag)", N("3"), S("p1")));
        Ensure(code == ErrCcf, $"expected {ErrCcf} when attribute_not_exists(flag) is false, got {Py.Repr(code)}");
        Ensure(msg == MsgCcf, $"expected AWS's exact message {Py.Repr(MsgCcf)}, got {Py.Repr(msg)}");
        item = (await Get())!;
        Ensure(Py.Equal(item["amount"], N("2")), $"a failed conditional UPDATE must leave the item untouched, got amount={Py.Repr(item.GetValueOrDefault("amount"))}");

        // The same false predicate refuses a DELETE identically, and the item still exists.
        (code, msg) = await Error(() => Execute($"DELETE FROM \"{t}\" WHERE pk = ? AND attribute_not_exists(flag)", S("p1")));
        Ensure(code == ErrCcf, $"expected {ErrCcf} when attribute_not_exists(flag) is false, got {Py.Repr(code)}");
        Ensure(msg == MsgCcf, $"expected AWS's exact message {Py.Repr(MsgCcf)}, got {Py.Repr(msg)}");
        item = (await Get())!;
        Ensure(Py.Equal(item["flag"], S("active")), $"a failed conditional DELETE must leave the item in place, got {Py.Repr(item)}");

        // Remove the attribute so attribute_not_exists(flag) now holds.
        await Execute($"UPDATE \"{t}\" REMOVE flag WHERE pk = ? AND attribute_exists(flag)", S("p1"));
        item = (await Get())!;
        Ensure(!item.ContainsKey("flag"), $"REMOVE flag must drop the attribute, got {Py.Repr(item)}");

        // attribute_not_exists(flag) now holds: the DELETE applies and the item is gone.
        await Execute($"DELETE FROM \"{t}\" WHERE pk = ? AND attribute_not_exists(flag)", S("p1"));
        var gone = await Get();
        Ensure(gone is null, $"DELETE must apply once attribute_not_exists(flag) holds, got {Py.Repr(gone)}");
    }
}
