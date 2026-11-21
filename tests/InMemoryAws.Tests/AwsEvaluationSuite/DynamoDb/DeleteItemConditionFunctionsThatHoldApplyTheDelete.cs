// Translated from behavioral/dynamodb/check_delete_item_condition_functions_that_hold_apply_the_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-condition-functions-that-hold-apply-the-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemConditionFunctionsThatHoldApplyTheDelete
{
    private const string Check = "delete-item-condition-functions-that-hold-apply-the-delete";

    private static readonly (string Code, string Message) CondFailed = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// A DeleteItem ConditionExpression using attribute_type, begins_with or contains is
    /// evaluated against the stored item: it holds and the delete applies, or it fails and the
    /// item is left exactly as it was.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-condition-functions-that-hold-apply-the-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-delcond-");
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

        var cases = new (string Pk, string Attr, AttributeValue Value, string HoldExpr, Dictionary<string, AttributeValue> HoldValues, string FailExpr, Dictionary<string, AttributeValue> FailValues)[]
        {
            // (pk, attr name, attr value, holding condition expr/values, failing (near-miss) expr/values)
            ("null-type", "v", Null(),
             "attribute_type(#a, :t)", Item((":t", S("NULL"))),
             "attribute_type(#a, :t)", Item((":t", S("S")))),
            ("n-type", "v", N("42"),
             "attribute_type(#a, :t)", Item((":t", S("N"))),
             "attribute_type(#a, :t)", Item((":t", S("S")))),
            ("s-type", "v", S("hello"),
             "attribute_type(#a, :t)", Item((":t", S("S"))),
             "attribute_type(#a, :t)", Item((":t", S("N")))),
            ("begins-with", "name", S("widget-42"),
             "begins_with(#a, :p)", Item((":p", S("widget-"))),
             "begins_with(#a, :p)", Item((":p", S("gadget-")))),
            ("contains-ns", "tags", NS("10", "20", "30"),
             "contains(#a, :n)", Item((":n", N("20"))),
             "contains(#a, :n)", Item((":n", N("99")))),
            ("contains-str", "desc", S("a quick brown fox"),
             "contains(#a, :s)", Item((":s", S("brown"))),
             "contains(#a, :s)", Item((":s", S("purple")))),
        };

        foreach (var (pk, attr, value, holdExpr, holdValues, failExpr, failValues) in cases)
        {
            var key = Item(("pk", S(pk)));
            var item = Item(("pk", S(pk)), (attr, value));
            await ddb.PutItemAsync(table, item);
            var attrNames = Names(("#a", attr));

            await ExpectError(
                () => ddb.DeleteItemAsync(new DeleteItemRequest
                {
                    TableName = table,
                    Key = key,
                    ConditionExpression = failExpr,
                    ExpressionAttributeNames = attrNames,
                    ExpressionAttributeValues = failValues,
                }),
                CondFailed);
            var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
            Ensure(
                Py.Equal(UnorderedSets(got), UnorderedSets(item)),
                $"{pk}: DeleteItem under a failing {Py.Repr(failExpr)} must leave the item unchanged, got {Py.Repr(got)}");

            await ddb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = key,
                ConditionExpression = holdExpr,
                ExpressionAttributeNames = attrNames,
                ExpressionAttributeValues = holdValues,
            });
            got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
            Ensure(got is null, $"{pk}: DeleteItem under a holding {Py.Repr(holdExpr)} must delete it, got {Py.Repr(got)}");
        }
    }

    /// <summary><paramref name="item"/> with every set member list sorted: DynamoDB sets carry no member order.</summary>
    private static Dictionary<string, object?>? UnorderedSets(Dictionary<string, AttributeValue>? item) =>
        item?.ToDictionary(
            kv => kv.Key,
            kv => kv.Value switch
            {
                { SS: { } ss } => new Dictionary<string, object?> { ["SS"] = ss.Order(StringComparer.Ordinal).ToList() },
                { NS: { } ns } => new Dictionary<string, object?> { ["NS"] = ns.Order(StringComparer.Ordinal).ToList() },
                { BS: { } bs } => new Dictionary<string, object?> { ["BS"] = bs.Select(b => b.ToArray()).OrderBy(Convert.ToHexString, StringComparer.Ordinal).ToList() },
                var other => (object?)other,
            },
            StringComparer.Ordinal);

    /// <summary>A cleanup running <c>undo()</c> where a failed delete raises.</summary>
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

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }

    /// <summary>Assert <c>fn()</c> raises exactly <c>(code, message)</c>; an unexpected success fails too.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            var got = (error.Code, error.Message);
            Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
            return;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }
}
