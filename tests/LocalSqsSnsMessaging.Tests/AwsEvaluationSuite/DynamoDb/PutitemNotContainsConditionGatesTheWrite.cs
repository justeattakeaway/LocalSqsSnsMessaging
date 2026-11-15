// Translated from behavioral/dynamodb/check_putitem_not_contains_condition_gates_the_write.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.putitem-not-contains-condition-gates-the-write</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutitemNotContainsConditionGatesTheWrite
{
    private const string CheckId = "putitem-not-contains-condition-gates-the-write";
    private static readonly (string Code, string Message) CondFailed = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>A cleanup running <c>undo()</c> where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>Assert <c>fn()</c> raises exactly <c>(code, message)</c>; an unexpected success fails too.</summary>
    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
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

    /// <summary>
    /// A PutItem ConditionExpression `NOT contains(#r, :v)` is refused with ConditionalCheckFailedException,
    /// item unchanged, when `:v` is an SS member by value and type; it succeeds, replacing the item,
    /// when `:v` is the same digits under a different type, since contains() never matches across types.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.putitem-not-contains-condition-gates-the-write")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbnc-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("pk", S("target")));
        var initial = Item(("pk", S("target")), ("roles", SS("5")));
        await ddb.PutItemAsync(table, initial);

        var names = Names(("#r", "roles"));

        // The condition is false: contains(roles, S "5") is true (type-matched set membership), so
        // NOT contains(...) fails the write and leaves the item exactly as it was.
        await ExpectError(
            async () => await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S("target")), ("roles", SS("5")), ("written", Bool(true))),
                ConditionExpression = "NOT contains(#r, :v)",
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = Item((":v", S("5"))),
            }),
            CondFailed);
        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(unchanged, initial),
            $"PutItem under a failing NOT contains() condition left {Py.Repr(unchanged)}, expected {Py.Repr(initial)} unchanged");

        // The condition is true: contains(roles, N "5") is false (the SS holds strings, not
        // numbers, so a cross-type operand never matches), so NOT contains(...) holds and the
        // write replaces the item.
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("target")), ("roles", SS("5")), ("written", Bool(true))),
            ConditionExpression = "NOT contains(#r, :v)",
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = Item((":v", N("5"))),
        });
        var replaced = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        var expected = Item(("pk", S("target")), ("roles", SS("5")), ("written", Bool(true)));
        Ensure(
            Py.Equal(replaced, expected),
            $"PutItem under a holding NOT contains() condition left {Py.Repr(replaced)}, expected {Py.Repr(expected)}");
    }
}
