// Translated from behavioral/dynamodb/check_put_item_condition_in_operator_number_list_and_map_structural_equality.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-condition-in-operator-number-list-and-map-structural-equality</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemConditionInOperatorNumberListAndMapStructuralEquality
{
    private const string Check = "put-item-condition-in-operator-number-list-and-map-structural-equality";

    private const string ErrCcf = "ConditionalCheckFailedException";

    private const string MsgCcf = "The conditional request failed";

    /// <summary>
    /// A PutItem ConditionExpression's IN operator matches a Number against a value list, and
    /// matches a Map against a list of Map operands by structural equality, not key order.
    /// An unmatched value refuses the write with ConditionalCheckFailedException, item unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-condition-in-operator-number-list-and-map-structural-equality")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbinop-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitTableActive(ctx, ddb, table);

        async Task<Dictionary<string, AttributeValue>> Get(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        // A Number IN a value list: the stored score (5) is one of the listed operands, so the
        // condition holds and the write applies (positive control for the refusal below).
        await ddb.PutItemAsync(table, Item(("pk", S("num_in")), ("score", N("5"))));
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("num_in")), ("score", N("5")), ("touched", Bool(true))),
            ConditionExpression = "score IN (:a, :b, :c)",
            ExpressionAttributeValues = Item((":a", N("1")), (":b", N("5")), (":c", N("9"))),
        });
        var item = await Get("num_in");
        Ensure(
            Py.Equal(item["touched"], Bool(true)),
            $"PutItem ConditionExpression 'score IN (1, 5, 9)' with score=5 must apply, got {Py.Repr(item)}");

        // The same condition against a value list that names neither 5 nor anything else the item
        // holds is refused, and the item is left exactly as the prior successful write left it.
        var (code, msg) = await Error(() => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("num_in")), ("score", N("5")), ("touched", Bool(false))),
            ConditionExpression = "score IN (:a, :b, :c)",
            ExpressionAttributeValues = Item((":a", N("2")), (":b", N("6")), (":c", N("10"))),
        }));
        Ensure(code == ErrCcf, $"PutItem ConditionExpression IN with no matching Number gave code {Py.Repr(code)}");
        Ensure(msg == MsgCcf, $"expected the exact message {Py.Repr(MsgCcf)}, got {Py.Repr(msg)}");
        item = await Get("num_in");
        Ensure(
            Py.Equal(item["touched"], Bool(true)),
            $"a refused PutItem must leave touched exactly as the prior successful write left it, got {Py.Repr(item)}");

        // A Map IN a list of Map operands: the stored Map is structurally equal to one listed
        // operand even though that operand's keys (and its nested Map's keys) are written in a
        // different order, so the condition holds and the write applies (positive control).
        AttributeValue StoredMap() => M(("a", N("1")), ("b", M(("inner1", S("m")), ("inner2", Bool(true)))));
        var permutedMatch = M(("b", M(("inner2", Bool(true)), ("inner1", S("m")))), ("a", N("1")));
        var unrelated = M(("a", N("2")), ("b", M(("inner1", S("z")), ("inner2", Bool(false)))));
        await ddb.PutItemAsync(table, Item(("pk", S("map_in")), ("val", StoredMap())));
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("map_in")), ("val", StoredMap()), ("touched", Bool(true))),
            ConditionExpression = "val IN (:m1, :m2)",
            ExpressionAttributeValues = Item((":m1", permutedMatch), (":m2", unrelated)),
        });
        item = await Get("map_in");
        Ensure(
            Py.Equal(item["touched"], Bool(true)),
            $"PutItem ConditionExpression IN with a key-order-permuted matching Map must apply, got {Py.Repr(item)}");

        // Neither listed Map operand is structurally equal to the stored Map: the write is
        // refused with ConditionalCheckFailedException and the item is left exactly as the prior
        // successful write left it.
        var mismatched1 = M(("a", N("1")), ("b", M(("inner1", S("z")), ("inner2", Bool(true)))));
        var mismatched2 = M(("a", N("3")), ("b", M(("inner1", S("m")), ("inner2", Bool(false)))));
        (code, msg) = await Error(() => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("map_in")), ("val", StoredMap()), ("touched", Bool(false))),
            ConditionExpression = "val IN (:m1, :m2)",
            ExpressionAttributeValues = Item((":m1", mismatched1), (":m2", mismatched2)),
        }));
        Ensure(code == ErrCcf, $"PutItem ConditionExpression IN with no structurally matching Map gave code {Py.Repr(code)}");
        Ensure(msg == MsgCcf, $"expected the exact message {Py.Repr(MsgCcf)}, got {Py.Repr(msg)}");
        item = await Get("map_in");
        Ensure(
            Py.Equal(item["touched"], Bool(true)),
            $"a refused PutItem must leave touched exactly as the prior successful write left it, got {Py.Repr(item)}");
    }

    /// <summary>Poll DescribeTable until TableStatus is ACTIVE, bounded by the run's remaining time.</summary>
    private static async Task<TableDescription> WaitTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string tableName)
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

        throw Fail($"table {tableName} never went ACTIVE in {budgetS}s: {Py.Repr(last?.TableStatus)}");
    }

    /// <summary>Call fn(); return (code, message), raising on an unexpected success.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is not null
            ? (error.Code, error.Message)
            : throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }
}
