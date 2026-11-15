// Translated from behavioral/dynamodb/check_condition_contains_set_element_gates_update_and_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.condition-contains-set-element-gates-update-and-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConditionContainsSetElementGatesUpdateAndDelete
{
    private static readonly (string Code, string Message) CondFailed = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// contains(set, element) in a ConditionExpression tests string-set membership: the write
    /// applies when the element is present and is refused, item unchanged, when it is absent.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.condition-contains-set-element-gates-update-and-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-contains-set-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        static AttributeValue Roles() => SS("admin", "viewer");

        // --- UpdateItem ---
        Dictionary<string, AttributeValue> UpdateKey() => Item(("pk", S("update-target")));
        await ddb.PutItemAsync(table, Item(("pk", S("update-target")), ("roles", Roles())));

        await ExpectError(
            () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = UpdateKey(),
                UpdateExpression = "SET #st = :s",
                ConditionExpression = "contains(#r, :v)",
                ExpressionAttributeNames = Names(("#r", "roles"), ("#st", "status")),
                ExpressionAttributeValues = Item((":v", S("suspended")), (":s", S("promoted"))),
            }),
            CondFailed.Code,
            CondFailed.Message);
        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = UpdateKey(), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(item, Item(("pk", S("update-target")), ("roles", Roles()))),
            $"UpdateItem under a failing contains() condition left {Py.Repr(item)}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = UpdateKey(),
            UpdateExpression = "SET #st = :s",
            ConditionExpression = "contains(#r, :v)",
            ExpressionAttributeNames = Names(("#r", "roles"), ("#st", "status")),
            ExpressionAttributeValues = Item((":v", S("admin")), (":s", S("promoted"))),
        });
        item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = UpdateKey(), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(item, Item(("pk", S("update-target")), ("roles", Roles()), ("status", S("promoted")))),
            $"UpdateItem under a holding contains() condition left {Py.Repr(item)}");

        // --- DeleteItem ---
        Dictionary<string, AttributeValue> DeleteKey() => Item(("pk", S("delete-target")));
        await ddb.PutItemAsync(table, Item(("pk", S("delete-target")), ("roles", Roles())));

        await ExpectError(
            () => ddb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = DeleteKey(),
                ConditionExpression = "contains(#r, :v)",
                ExpressionAttributeNames = Names(("#r", "roles")),
                ExpressionAttributeValues = Item((":v", S("suspended"))),
            }),
            CondFailed.Code,
            CondFailed.Message);
        item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = DeleteKey(), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(item, Item(("pk", S("delete-target")), ("roles", Roles()))),
            $"DeleteItem under a failing contains() condition left {Py.Repr(item)}");

        await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = DeleteKey(),
            ConditionExpression = "contains(#r, :v)",
            ExpressionAttributeNames = Names(("#r", "roles")),
            ExpressionAttributeValues = Item((":v", S("admin"))),
        });
        item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = DeleteKey(), ConsistentRead = true })).Item;
        Ensure(item is null, $"DeleteItem under a holding contains() condition left {Py.Repr(item)}");
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

    /// <summary>Assert <c>fn()</c> raises exactly <c>(code, message)</c>; an unexpected success fails too.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            var got = (error.Code, error.Message);
            Ensure(got == (code, message), $"expected {Py.Repr((code, message))}, got {Py.Repr(got)}");
            return;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }
}
