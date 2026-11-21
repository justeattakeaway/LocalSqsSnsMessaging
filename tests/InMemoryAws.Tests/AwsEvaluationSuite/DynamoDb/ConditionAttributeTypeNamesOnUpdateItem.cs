// Translated from behavioral/dynamodb/check_condition_attribute_type_names_on_update_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.condition-attribute-type-names-on-update-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConditionAttributeTypeNamesOnUpdateItem
{
    private static readonly (string Code, string Message) CondFailed = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// UpdateItem's ConditionExpression attribute_type matches the stored type for S, N, BOOL,
    /// NULL, L, M, NS and SS alike when ANDed together in one condition, and a mismatched type name
    /// refuses the write with the item left unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.condition-attribute-type-names-on-update-item")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-attrtype-upd-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        Dictionary<string, AttributeValue> Key() => Item(("pk", S("item")));
        var item = Item(
            ("pk", S("item")),
            ("s_attr", S("str")),
            ("n_attr", N("5")),
            ("bool_attr", Bool(true)),
            ("null_attr", Null()),
            ("list_attr", L(S("x"))),
            ("m_attr", M(("k", S("v")))),
            ("ns_attr", NS("1", "2")),
            ("ss_attr", SS("a", "b")));
        await ddb.PutItemAsync(table, item);

        var names = Names(
            ("#s", "s_attr"), ("#n", "n_attr"), ("#b", "bool_attr"), ("#nu", "null_attr"),
            ("#l", "list_attr"), ("#m", "m_attr"), ("#ns", "ns_attr"), ("#ss", "ss_attr"));
        names["#mk"] = "marker";
        const string condition =
            "attribute_type(#s, :ts) AND attribute_type(#n, :tn) AND attribute_type(#b, :tb) " +
            "AND attribute_type(#nu, :tnu) AND attribute_type(#l, :tl) AND attribute_type(#m, :tm) " +
            "AND attribute_type(#ns, :tns) AND attribute_type(#ss, :tss)";
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET #mk = :v1",
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = Item(
                (":ts", S("S")), (":tn", S("N")), (":tb", S("BOOL")), (":tnu", S("NULL")),
                (":tl", S("L")), (":tm", S("M")), (":tns", S("NS")), (":tss", S("SS")),
                (":v1", S("matched"))),
            ConditionExpression = condition,
        });
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(got.GetValueOrDefault("marker"), S("matched")),
            "UpdateItem under a condition ANDing attribute_type over S, N, BOOL, NULL, L, M, NS and " +
            $"SS left marker at {Py.Repr(got.GetValueOrDefault("marker"))}, expected it set to 'matched'");

        await ExpectError(
            () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Key(),
                UpdateExpression = "SET #mk = :v2",
                ExpressionAttributeNames = Names(("#mk", "marker"), ("#n", "n_attr")),
                ExpressionAttributeValues = Item((":v2", S("mismatched")), (":wrong", S("S"))),
                ConditionExpression = "attribute_type(#n, :wrong)",
            }),
            CondFailed);
        got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(got.GetValueOrDefault("marker"), S("matched")),
            "UpdateItem under a mismatched attribute_type condition (n_attr is N, checked against S) " +
            $"left marker at {Py.Repr(got.GetValueOrDefault("marker"))}, expected the earlier 'matched' value unchanged");
    }

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
