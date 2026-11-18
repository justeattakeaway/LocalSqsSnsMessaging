// Translated from behavioral/dynamodb/check_update_item_add_delete_empty_set_operand_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-add-delete-empty-set-operand-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemAddDeleteEmptySetOperandRejected
{
    private const string CheckId = "update-item-add-delete-empty-set-operand-rejected";

    private static readonly (string Code, string Message) EmptySet = (
        "ValidationException",
        "1 validation error detected: One or more parameter values were invalid: " +
        "An string set  may not be empty");

    /// <summary>
    /// UpdateItem's ADD and DELETE actions both reject a set operand with zero members, leaving
    /// the target attribute unchanged; a same-typed operand with members still applies.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-add-delete-empty-set-operand-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("pk", S("x")));
        await ddb.PutItemAsync(table, Item(("pk", S("x")), ("tags", SS("only"))));

        await ExpectError(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "ADD tags :e",
            ExpressionAttributeValues = Item((":e", SS())),
        }), EmptySet);

        await ExpectError(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "DELETE tags :e",
            ExpressionAttributeValues = Item((":e", SS())),
        }), EmptySet);

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            unchanged["tags"].SS.SequenceEqual(["only"]),
            $"both rejected calls must leave the attribute untouched, got {Py.Repr(unchanged["tags"])}");

        // Positive control: the same operand shape with a member applies.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "ADD tags :e",
            ExpressionAttributeValues = Item((":e", SS("more"))),
        });
        var grown = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            grown["tags"].SS.ToHashSet(StringComparer.Ordinal).SetEquals(["only", "more"]),
            $"the positive-control ADD with a non-empty set must apply, got {Py.Repr(grown["tags"])}");
    }

    /// <summary>Assert <paramref name="fn"/> raises a ClientError with exactly (code, message); raises on success.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {Py.Repr(result)}");
        }

        var got = (error.Code, error.Message);
        Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
    }
}
