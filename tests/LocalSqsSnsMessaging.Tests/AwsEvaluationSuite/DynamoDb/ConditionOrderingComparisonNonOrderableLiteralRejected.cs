// Translated from behavioral/dynamodb/check_condition_ordering_comparison_non_orderable_literal_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.condition-ordering-comparison-non-orderable-literal-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConditionOrderingComparisonNonOrderableLiteralRejected
{
    private static readonly string[] OrderingOps = ["<", "<=", ">", ">="];

    private static (string TypeCode, Func<AttributeValue> Value)[] NonOrderable() =>
    [
        ("BOOL", () => Bool(true)),
        ("NULL", Null),
        ("SS", () => SS("a", "b")),
        ("NS", () => NS("1", "2")),
        ("L", () => L(S("a"))),
        ("M", () => M(("k", S("a")))),
    ];

    private static Dictionary<string, AttributeValue> Base() => Item(("pk", S("p1")), ("n", N("5")), ("s", S("x")), ("note", S("orig")));

    private static Dictionary<string, AttributeValue> Changed()
    {
        var item = Base();
        item["note"] = S("changed");
        return item;
    }

    private static string OperandTypeMsg(string op, string type) =>
        "1 validation error detected: Invalid ConditionExpression: Incorrect operand type for " +
        $"operator or function; operator or function: {op}, operand type: {type}";

    /// <summary>
    /// A ConditionExpression ordering comparison or BETWEEN with a BOOL/NULL/set/L/M value is
    /// ValidationException on PutItem, UpdateItem and DeleteItem, even inside a short-circuited or
    /// negated branch, and the item is unchanged; an orderable literal is accepted.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.OperatorsAndFunctions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.condition-ordering-comparison-non-orderable-literal-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-cond-order-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("p1")));
        await ddb.PutItemAsync(table, Base());

        Task<PutItemResponse> Put(string condition, Dictionary<string, AttributeValue> values) =>
            ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Changed(),
                ConditionExpression = condition,
                ExpressionAttributeValues = values,
            });

        static async Task AssertRefused(string label, Func<Task> fn, string op, string typeCode)
        {
            var want = OperandTypeMsg(op, typeCode);
            var (code, msg) = await Error(fn);
            Ensure((code, msg) == ("ValidationException", want), $"{label} gave {Py.Repr(code)}: {Py.Repr(msg)}");
        }

        foreach (var (typeCode, value) in NonOrderable())
        {
            foreach (var op in OrderingOps)
            {
                await AssertRefused($"PutItem 'n {op} :v' with a {typeCode} value", () => Put($"n {op} :v", Item((":v", value()))), op, typeCode);
            }
        }

        await AssertRefused(
            "PutItem 'n BETWEEN :a AND :b' with BOOL bounds",
            () => Put("n BETWEEN :a AND :b", Item((":a", Bool(true)), (":b", Bool(false)))),
            "BETWEEN",
            "BOOL");
        await AssertRefused(
            "PutItem ':x > :v' with a BOOL right operand",
            () => Put(":x > :v", Item((":x", N("1")), (":v", Bool(true)))),
            ">",
            "BOOL");
        await AssertRefused(
            "PutItem ':v > :x' with a BOOL left operand",
            () => Put(":v > :x", Item((":v", Bool(true)), (":x", N("1")))),
            ">",
            "BOOL");
        foreach (var condition in new[]
        {
            "attribute_not_exists(zzz) OR n > :bool",
            "attribute_exists(zzz) AND n > :bool",
            "NOT (n > :bool)",
            "nope > :bool",
            "size(s) > :bool",
        })
        {
            await AssertRefused($"PutItem {Py.Repr(condition)} with a BOOL value", () => Put(condition, Item((":bool", Bool(true)))), ">", "BOOL");
        }

        await AssertRefused(
            "UpdateItem condition 'n > :v' with a BOOL value",
            () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Key(),
                UpdateExpression = "SET note = :c",
                ConditionExpression = "n > :v",
                ExpressionAttributeValues = Item((":c", S("changed")), (":v", Bool(true))),
            }),
            ">",
            "BOOL");
        await AssertRefused(
            "DeleteItem condition 'n > :v' with a BOOL value",
            () => ddb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = Key(),
                ConditionExpression = "n > :v",
                ExpressionAttributeValues = Item((":v", Bool(true))),
            }),
            ">",
            "BOOL");

        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(item, Base()), $"item after every refused conditional write is {Py.Repr(item)}, expected {Py.Repr(Base())}");

        await Put("n > :zero", Item((":zero", N("0"))));
        item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(item, Changed()), $"PutItem under 'n > :zero' (5 > 0) left {Py.Repr(item)}, expected {Py.Repr(Changed())}");
    }

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        const double timeoutS = 300.0;
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var bound = Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < bound)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        return error is not null ? (error.Code, error.Message) : throw Fail("expected the call to be refused, but it succeeded");
    }
}
