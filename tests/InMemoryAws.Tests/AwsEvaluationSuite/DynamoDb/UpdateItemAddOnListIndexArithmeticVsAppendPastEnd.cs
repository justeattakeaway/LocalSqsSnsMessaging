// Translated from behavioral/dynamodb/check_update_item_add_on_list_index_arithmetic_vs_append_past_end.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-add-on-list-index-arithmetic-vs-append-past-end</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemAddOnListIndexArithmeticVsAppendPastEnd
{
    private const string Check = "update-item-add-on-list-index-arithmetic-vs-append-past-end";

    /// <summary>A cleanup running undo() where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
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

    /// <summary>
    /// ADD on an in-bounds numeric list index performs arithmetic on the existing element;
    /// ADD on a list index past the end has no element to add to, so it appends the raw operand,
    /// of any type, at the end instead.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-add-on-list-index-arithmetic-vs-append-past-end")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-addidx-");
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

        async Task<Dictionary<string, AttributeValue>> Update(string pk, string expr, Dictionary<string, AttributeValue> values, ReturnValue rv) =>
            (await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S(pk))),
                UpdateExpression = expr,
                ExpressionAttributeValues = values,
                ReturnValues = rv,
            })).Attributes;

        async Task<Dictionary<string, AttributeValue>> Read(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        // ADD on an in-bounds numeric list index adds the operand to the existing element.
        await ddb.PutItemAsync(table, Item(("pk", S("arith")), ("a", L(N("10"), N("20"), N("30")))));
        var got = await Update("arith", "ADD a[0] :v", Item((":v", N("5"))), ReturnValue.ALL_NEW);
        var want = Item(("pk", S("arith")), ("a", L(N("15"), N("20"), N("30"))));
        Ensure(Py.Equal(got, want), $"ADD a[0] :v on an in-bounds numeric element returned {Py.Repr(got)}, expected {Py.Repr(want)}");
        var item = await Read("arith");
        Ensure(Py.Equal(item, want), $"stored item after ADD a[0] :v is {Py.Repr(item)}, expected {Py.Repr(want)}");

        // ADD on a list index past the end has nothing to add to: the numeric operand is appended
        // as a new element, not summed with anything.
        await ddb.PutItemAsync(table, Item(("pk", S("append-n")), ("a", L(N("10"), N("20"), N("30")))));
        got = await Update("append-n", "ADD a[99] :v", Item((":v", N("5"))), ReturnValue.ALL_NEW);
        want = Item(("pk", S("append-n")), ("a", L(N("10"), N("20"), N("30"), N("5"))));
        Ensure(Py.Equal(got, want), $"ADD a[99] :v past the end returned {Py.Repr(got)}, expected {Py.Repr(want)}");
        item = await Read("append-n");
        Ensure(Py.Equal(item, want), $"stored item after ADD a[99] :v is {Py.Repr(item)}, expected {Py.Repr(want)}");

        // The same past-the-end ADD appends a Set-typed operand just as readily: nothing at that
        // slot forces the operand to match the list's existing element type.
        await ddb.PutItemAsync(table, Item(("pk", S("append-ss")), ("a", L(N("10")))));
        got = await Update("append-ss", "ADD a[9] :v", Item((":v", SS("x"))), ReturnValue.ALL_NEW);
        want = Item(("pk", S("append-ss")), ("a", L(N("10"), SS("x"))));
        Ensure(Py.Equal(got, want), $"ADD a[9] :v with a Set operand past the end returned {Py.Repr(got)}, expected {Py.Repr(want)}");
        item = await Read("append-ss");
        Ensure(Py.Equal(item, want), $"stored item after ADD a[9] :v (Set operand) is {Py.Repr(item)}, expected {Py.Repr(want)}");
    }
}
