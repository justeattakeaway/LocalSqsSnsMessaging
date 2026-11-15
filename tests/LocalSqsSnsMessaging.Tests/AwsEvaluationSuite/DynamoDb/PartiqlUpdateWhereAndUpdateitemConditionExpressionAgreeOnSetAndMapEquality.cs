// Translated from behavioral/dynamodb/check_partiql_update_where_and_updateitem_condition_expression_agree_on_set_and_map_equality.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-update-where-and-updateitem-condition-expression-agree-on-set-and-map-equality</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlUpdateWhereAndUpdateitemConditionExpressionAgreeOnSetAndMapEquality
{
    private const string CheckId = "partiql-update-where-and-updateitem-condition-expression-agree-on-set-and-map-equality";
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
            last = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table;
            if (last.TableStatus == TableStatus.ACTIVE)
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"table {tableName} never went ACTIVE in {budgetS}s: {last}");
    }

    /// <summary>Call fn(); return (code, message), raising on an unexpected success.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// An UpdateItem ConditionExpression and a PartiQL UPDATE ... WHERE clause agree on the same '=' predicate over a String Set and a Map.
    /// A permuted Set still satisfies the condition and the write applies; a mismatched Map does not, and ConditionalCheckFailedException leaves the item unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-update-where-and-updateitem-condition-expression-agree-on-set-and-map-equality")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlupdwh-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitTableActive(ctx, ddb, table);

        async Task<Dictionary<string, AttributeValue>> Get(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        // UpdateItem ConditionExpression 'val = :v': a permuted String Set still satisfies it, and
        // a mismatched one is refused with ConditionalCheckFailedException, item unchanged.
        await ddb.PutItemAsync(table, Item(("pk", S("ui_ss")), ("val", SS("a", "b"))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("ui_ss"))),
            UpdateExpression = "SET touched = :t",
            ConditionExpression = "val = :v",
            ExpressionAttributeValues = Item((":t", Bool(true)), (":v", SS("b", "a"))),
        });
        var item = await Get("ui_ss");
        Ensure(
            Py.Equal(item["touched"], Bool(true)),
            $"UpdateItem ConditionExpression 'val = :v' with a permuted Set must apply, got {Py.Repr(item)}");

        var (code, msg) = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("ui_ss"))),
            UpdateExpression = "SET touched = :t",
            ConditionExpression = "val = :v",
            ExpressionAttributeValues = Item((":t", Bool(false)), (":v", SS("a", "z"))),
        }));
        Ensure(code == ErrCcf, $"UpdateItem ConditionExpression with a mismatched Set gave code {Py.Repr(code)}");
        Ensure(msg == MsgCcf, $"expected the exact message {Py.Repr(MsgCcf)}, got {Py.Repr(msg)}");
        item = await Get("ui_ss");
        Ensure(
            Py.Equal(item["touched"], Bool(true)),
            $"a refused UpdateItem must leave touched exactly as the prior successful write left it, got {Py.Repr(item)}");

        // The same predicate as a PartiQL UPDATE ... WHERE condition: a permuted Set fires the
        // write (positive control) the same way UpdateItem's ConditionExpression did above.
        await ddb.PutItemAsync(table, Item(("pk", S("upd_ss")), ("val", SS("p", "q"))));
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET flag = ? WHERE pk = ? AND val = ?",
            Parameters = [Bool(true), S("upd_ss"), SS("q", "p")],
        });
        item = await Get("upd_ss");
        Ensure(Py.Equal(item["flag"], Bool(true)), $"UPDATE ... WHERE val = <permuted Set> must apply the write, got {Py.Repr(item)}");

        // A matching Map condition applies the write (positive control); a mismatched Map is
        // refused with ConditionalCheckFailedException and leaves the item exactly as that write
        // left it.
        await ddb.PutItemAsync(table, Item(("pk", S("upd_m")), ("val", M(("x", N("1")), ("y", S("m"))))));
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET flag = ? WHERE pk = ? AND val = ?",
            Parameters = [Bool(true), S("upd_m"), M(("x", N("1")), ("y", S("m")))],
        });
        item = await Get("upd_m");
        Ensure(
            Py.Equal(item["flag"], Bool(true)),
            $"positive control: UPDATE ... WHERE val = <matching Map> must apply the write, got {Py.Repr(item)}");

        (code, msg) = await Error(async () => await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET flag = ? WHERE pk = ? AND val = ?",
            Parameters = [Bool(false), S("upd_m"), M(("x", N("1")), ("y", S("z")))],
        }));
        Ensure(code == ErrCcf, $"UPDATE ... WHERE val = <mismatched Map> gave code {Py.Repr(code)}");
        Ensure(msg == MsgCcf, $"expected the exact message {Py.Repr(MsgCcf)}, got {Py.Repr(msg)}");
        item = await Get("upd_m");
        Ensure(
            Py.Equal(item["flag"], Bool(true)),
            $"a refused UPDATE ... WHERE must leave the item exactly as the prior successful write left it, got {Py.Repr(item)}");
    }
}
