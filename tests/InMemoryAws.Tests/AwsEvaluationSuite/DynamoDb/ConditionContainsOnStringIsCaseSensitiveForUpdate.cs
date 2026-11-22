// Translated from behavioral/dynamodb/check_condition_contains_on_string_is_case_sensitive_for_update.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.condition-contains-on-string-is-case-sensitive-for-update</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConditionContainsOnStringIsCaseSensitiveForUpdate
{
    private const string Check = "condition-contains-on-string-is-case-sensitive-for-update";

    private static readonly (string Code, string Message) CondFailed = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// An UpdateItem ConditionExpression `contains(#n, :v)` on a String compares byte-for-byte:
    /// a case-mismatched substring is refused with ConditionalCheckFailedException and leaves the
    /// item unchanged, while the same substring in its stored case holds and the update applies.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.condition-contains-on-string-is-case-sensitive-for-update")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbcc-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        Dictionary<string, AttributeValue> Key() => Item(("pk", S("target")));
        var initial = Item(("pk", S("target")), ("name", S("Example Corp")), ("stage", S("start")));
        await ddb.PutItemAsync(table, initial);

        Dictionary<string, string> NamesMap() => Names(("#n", "name"), ("#s", "stage"));

        // contains("Example Corp", "corp") is false: the stored value holds "Corp", not "corp", and
        // contains() compares case-sensitively, so the condition fails and the item is untouched.
        await ExpectError(
            () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Key(),
                UpdateExpression = "SET #s = :done",
                ConditionExpression = "contains(#n, :v)",
                ExpressionAttributeNames = NamesMap(),
                ExpressionAttributeValues = Item((":v", S("corp")), (":done", S("done"))),
            }),
            CondFailed);
        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(unchanged, initial),
            $"UpdateItem under a case-mismatched contains() condition left {Py.Repr(unchanged)}, " +
            $"expected {Py.Repr(initial)} unchanged");

        // contains("Example Corp", "Corp") is true: same substring, stored case, so the condition
        // holds and the update applies.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET #s = :done",
            ConditionExpression = "contains(#n, :v)",
            ExpressionAttributeNames = NamesMap(),
            ExpressionAttributeValues = Item((":v", S("Corp")), (":done", S("done"))),
        });
        var applied = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        var expected = Item(("pk", S("target")), ("name", S("Example Corp")), ("stage", S("done")));
        Ensure(
            Py.Equal(applied, expected),
            $"UpdateItem under a case-matched contains() condition left {Py.Repr(applied)}, expected {Py.Repr(expected)}");
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
