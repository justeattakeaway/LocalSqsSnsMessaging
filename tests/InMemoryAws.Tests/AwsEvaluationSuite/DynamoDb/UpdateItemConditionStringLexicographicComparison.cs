// Translated from behavioral/dynamodb/check_update_item_condition_string_lexicographic_comparison.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-condition-string-lexicographic-comparison</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemConditionStringLexicographicComparison
{
    private const string Check = "update-item-condition-string-lexicographic-comparison";
    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>(Error.Code, Error.Message) of the error fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, err) = await FidelityContext.Capture(fn);
        return err is null ? throw Fail($"expected the call to be refused, but it succeeded: {result}") : (err.Code, err.Message);
    }

    /// <summary>Assert fn() raises exactly (code, message).</summary>
    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var got = await Error(fn);
        Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
    }

    /// <summary>
    /// An UpdateItem ConditionExpression comparing String attributes orders them
    /// lexicographically (a shorter prefix is less than its extension) and treats two equal
    /// strings as not less-than, rejecting the write with ConditionalCheckFailedException.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.OperatorsAndFunctions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-condition-string-lexicographic-comparison")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-strcmp-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("pk", S("strcmp")));
        await ddb.PutItemAsync(table, Item(("pk", S("strcmp")), ("label", S("apple"))));

        async Task<Dictionary<string, AttributeValue>> Read() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        async Task<object> Update(string newVal, string threshold) => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET val = :v",
            ConditionExpression = "label < :threshold",
            ExpressionAttributeValues = Item((":v", S(newVal)), (":threshold", S(threshold))),
        });

        // Positive control: "apple" is a strict prefix of "applesauce", so it is lexicographically
        // less than it even though the shared prefix compares equal character by character.
        await Update("admitted", "applesauce");
        var got = await Read();
        Ensure(
            Py.Equal(got["val"], S("admitted")),
            $"'apple' < 'applesauce' should admit the update, got val={Py.Repr(got.GetValueOrDefault("val"))}");

        // Negative: two equal strings are never less-than each other, so the write is refused and
        // the item is left exactly as the positive control left it.
        await ExpectError(() => Update("rejected", "apple"), Ccf);
        var unchanged = await Read();
        Ensure(
            Py.Equal(unchanged["val"], S("admitted")),
            $"rejected update via 'apple' < 'apple' must leave val unchanged, got {Py.Repr(unchanged.GetValueOrDefault("val"))}");
    }
}
