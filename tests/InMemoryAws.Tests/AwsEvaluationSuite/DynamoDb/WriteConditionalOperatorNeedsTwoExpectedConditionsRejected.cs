// Translated from behavioral/dynamodb/check_write_conditional_operator_needs_two_expected_conditions_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.write-conditional-operator-needs-two-expected-conditions-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class WriteConditionalOperatorNeedsTwoExpectedConditionsRejected
{
    private const string WithoutExpectedMsg =
        "1 validation error detected: ConditionalOperator cannot be used without Filter or Expected";

    private const string TwoOrMoreMsg =
        "1 validation error detected: ConditionalOperator can only be used when Filter or Expected "
        + "has two or more elements";

    /// <summary>A legacy EQ condition on one string value.</summary>
    private static ExpectedAttributeValue Cond(string value) =>
        new() { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [S(value)] };

    private static Dictionary<string, ExpectedAttributeValue> Expected(params (string Name, ExpectedAttributeValue Condition)[] entries) =>
        entries.ToDictionary(e => e.Name, e => e.Condition, StringComparer.Ordinal);

    private static Dictionary<string, AttributeValueUpdate> Put(string name, AttributeValue value) =>
        new(StringComparer.Ordinal) { [name] = new AttributeValueUpdate { Value = value, Action = AttributeAction.PUT } };

    private static Dictionary<string, AttributeValue> With(Dictionary<string, AttributeValue> item, string name, AttributeValue value) =>
        new(item, StringComparer.Ordinal) { [name] = value };

    /// <summary>
    /// ConditionalOperator on PutItem, DeleteItem or UpdateItem without Expected, or with a
    /// one-condition Expected, is ValidationException and writes nothing; two conditions work.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.write-conditional-operator-needs-two-expected-conditions-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-condop-write-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);
        var key = Item(("pk", S("k1")));
        var item = Item(("pk", S("k1")), ("a", S("x")), ("b", S("y")));
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = item });
        var newKey = Item(("pk", S("k2")));

        var cases = new (string Label, string Want, Func<Task> Fn)[]
        {
            ("PutItem with ConditionalOperator and no Expected", WithoutExpectedMsg,
                () => ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = newKey, ConditionalOperator = ConditionalOperator.AND })),
            ("DeleteItem with ConditionalOperator and no Expected", WithoutExpectedMsg,
                () => ddb.DeleteItemAsync(new DeleteItemRequest { TableName = table, Key = key, ConditionalOperator = ConditionalOperator.AND })),
            ("UpdateItem with ConditionalOperator, AttributeUpdates and no Expected", WithoutExpectedMsg,
                () => ddb.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = table, Key = key, ConditionalOperator = ConditionalOperator.AND,
                    AttributeUpdates = Put("a", S("v")),
                })),
            ("UpdateItem with only ConditionalOperator", WithoutExpectedMsg,
                () => ddb.UpdateItemAsync(new UpdateItemRequest { TableName = table, Key = newKey, ConditionalOperator = ConditionalOperator.AND })),
            ("PutItem with ConditionalOperator and a one-condition Expected", TwoOrMoreMsg,
                () => ddb.PutItemAsync(new PutItemRequest
                {
                    TableName = table, Item = newKey, ConditionalOperator = ConditionalOperator.AND,
                    Expected = Expected(("a", Cond("x"))),
                })),
            ("DeleteItem with ConditionalOperator and a one-condition Expected", TwoOrMoreMsg,
                () => ddb.DeleteItemAsync(new DeleteItemRequest
                {
                    TableName = table, Key = key, ConditionalOperator = ConditionalOperator.AND,
                    Expected = Expected(("a", Cond("x"))),
                })),
            ("UpdateItem with ConditionalOperator and a one-condition Expected", TwoOrMoreMsg,
                () => ddb.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = table, Key = key, ConditionalOperator = ConditionalOperator.AND,
                    AttributeUpdates = Put("a", S("v")),
                    Expected = Expected(("a", Cond("x"))),
                })),
        };
        foreach (var (label, want, fn) in cases)
        {
            var (code, msg) = await Error(fn);
            Ensure((code, msg) == ("ValidationException", want), $"{label} gave {Py.Repr(code)}: {Py.Repr(msg)}");
        }

        var items = (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Items;
        Ensure(Py.Equal(items, new[] { item }), $"table after the refused writes holds {Py.Repr(items)}, expected only {Py.Repr(item)}");

        var replaced = With(item, "c", S("z"));
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table, Item = replaced, ConditionalOperator = ConditionalOperator.AND,
            Expected = Expected(("a", Cond("x")), ("b", Cond("y"))),
        });
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(got, replaced), $"PutItem with a holding two-condition AND Expected left {Py.Repr(got)}");
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table, Key = key, ConditionalOperator = ConditionalOperator.AND,
            AttributeUpdates = Put("c", S("w")),
            Expected = Expected(("a", Cond("x")), ("b", Cond("y"))),
        });
        got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        var wantItem = With(replaced, "c", S("w"));
        Ensure(Py.Equal(got, wantItem), $"UpdateItem with a holding two-condition AND Expected left {Py.Repr(got)}");
        await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table, Key = key, ConditionalOperator = ConditionalOperator.OR,
            Expected = Expected(("a", Cond("x")), ("b", Cond("nope"))),
        });
        got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(got is null, $"DeleteItem with a holding two-condition OR Expected left {Py.Repr(got)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn) ?? throw Fail("expected the call to be refused, but it succeeded");
        return (error.Code, error.Message);
    }

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string notFound, double timeoutS = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || error.Code == notFound)
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
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
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (left != 0 ? left : 300.0) - 30.0));
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

        throw Fail($"table {table} not ACTIVE before the deadline: {desc?.TableStatus?.Value}");
    }
}
