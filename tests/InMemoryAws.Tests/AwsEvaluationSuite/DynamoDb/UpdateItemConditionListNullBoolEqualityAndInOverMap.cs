// Translated from behavioral/dynamodb/check_update_item_condition_list_null_bool_equality_and_in_over_map.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-condition-list-null-bool-equality-and-in-over-map</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemConditionListNullBoolEqualityAndInOverMap
{
    private const string Check = "update-item-condition-list-null-bool-equality-and-in-over-map";
    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>A cleanup running undo() where a failed delete raises; the resource's own not-found codes pass.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>Poll DescribeTable until TableStatus is ACTIVE, bounded by the run's remaining time.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var bound = Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < bound)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc)}");
    }

    /// <summary>Call fn(); assert it raised exactly (code, message); raise on an unexpected success.</summary>
    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var (result, err) = await FidelityContext.Capture(fn);
        if (err is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        var got = (err.Code, err.Message);
        Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
    }

    /// <summary>
    /// UpdateItem's ConditionExpression matches a List by whole-value equality, a Map through
    /// IN, and NULL only against another NULL; a near-miss is ConditionalCheckFailedException and
    /// leaves the item exactly as the prior successful write left it.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-condition-list-null-bool-equality-and-in-over-map")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-condeq-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        Dictionary<string, AttributeValue> KeyOf(string name) => Item(("pk", S(name)));
        await ddb.PutItemAsync(table, Item(("pk", S("list")), ("val", L(N("1"), N("2")))));
        await ddb.PutItemAsync(table, Item(("pk", S("map")), ("val", M(("x", N("1"))))));
        await ddb.PutItemAsync(table, Item(("pk", S("null")), ("val", Null())));
        await ddb.PutItemAsync(table, Item(("pk", S("bool")), ("val", Bool(true))));
        await ddb.PutItemAsync(table, Item(("pk", S("str")), ("val", S("abc"))));

        async Task<Dictionary<string, AttributeValue>> Get(string name) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = KeyOf(name), ConsistentRead = true })).Item;

        UpdateItemRequest Touch(string name, string condition, Dictionary<string, AttributeValue> values) => new()
        {
            TableName = table,
            Key = KeyOf(name),
            UpdateExpression = "SET touched = :t",
            ConditionExpression = condition,
            ExpressionAttributeValues = values,
        };

        // A List matches a ConditionExpression equality only as a whole value, element by element.
        await ddb.UpdateItemAsync(Touch("list", "val = :v", Item((":t", Bool(true)), (":v", L(N("1"), N("2"))))));
        var item = await Get("list");
        Ensure(
            Py.Equal(item["touched"], Bool(true)),
            $"ConditionExpression 'val = :v' with the exact List value must apply, got {Py.Repr(item)}");
        await ExpectError(async () => await ddb.UpdateItemAsync(Touch("list", "val = :v", Item((":t", Bool(false)), (":v", L(N("1"), N("3")))))), Ccf);
        item = await Get("list");
        Ensure(
            Py.Equal(item["touched"], Bool(true)),
            $"a refused List condition must leave touched exactly as the prior successful write left it, got {Py.Repr(item)}");

        // A Map matches an IN list by comparing each operand to it structurally, not by identity.
        await ddb.UpdateItemAsync(Touch("map", "val IN (:v1, :v2)", Item(
            (":t", Bool(true)),
            (":v1", M(("x", N("9")))),
            (":v2", M(("x", N("1")))))));
        item = await Get("map");
        Ensure(
            Py.Equal(item["touched"], Bool(true)),
            $"ConditionExpression 'val IN (:v1, :v2)' must apply once a Map operand matches, got {Py.Repr(item)}");
        await ExpectError(
            async () => await ddb.UpdateItemAsync(Touch("map", "val IN (:v1, :v3)", Item(
                (":t", Bool(false)),
                (":v1", M(("x", N("9")))),
                (":v3", M(("x", N("2"))))))),
            Ccf);
        item = await Get("map");
        Ensure(
            Py.Equal(item["touched"], Bool(true)),
            $"a refused Map IN condition must leave touched exactly as the prior successful write left it, got {Py.Repr(item)}");

        // NULL compares equal only to another NULL, never to a differently-typed value.
        await ddb.UpdateItemAsync(Touch("null", "val = :v", Item((":t", Bool(true)), (":v", Null()))));
        item = await Get("null");
        Ensure(
            Py.Equal(item["touched"], Bool(true)),
            $"ConditionExpression 'val = :v' with NULL = NULL must apply, got {Py.Repr(item)}");
        await ExpectError(async () => await ddb.UpdateItemAsync(Touch("str", "val = :v", Item((":t", Bool(true)), (":v", Null())))), Ccf);
        item = await Get("str");
        Ensure(
            !item.ContainsKey("touched"),
            $"a refused 'val = :v' with :v NULL against a String value must leave the item untouched, got {Py.Repr(item)}");

        // BOOL equality matches only the same boolean value.
        await ddb.UpdateItemAsync(Touch("bool", "val = :v", Item((":t", Bool(true)), (":v", Bool(true)))));
        item = await Get("bool");
        Ensure(
            Py.Equal(item["touched"], Bool(true)),
            $"ConditionExpression 'val = :v' with the matching BOOL value must apply, got {Py.Repr(item)}");
        await ExpectError(async () => await ddb.UpdateItemAsync(Touch("bool", "val = :v", Item((":t", Bool(false)), (":v", Bool(false))))), Ccf);
        item = await Get("bool");
        Ensure(
            Py.Equal(item["touched"], Bool(true)),
            $"a refused BOOL condition must leave touched exactly as the prior successful write left it, got {Py.Repr(item)}");
    }
}
