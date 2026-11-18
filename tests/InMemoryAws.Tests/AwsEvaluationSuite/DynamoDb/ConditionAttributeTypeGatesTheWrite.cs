// Translated from behavioral/dynamodb/check_condition_attribute_type_gates_the_write.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.condition-attribute-type-gates-the-write</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConditionAttributeTypeGatesTheWrite
{
    /// <summary>
    /// attribute_type in a ConditionExpression compares the STORED type, so a write conditioned on the
    /// wrong type is refused and the item is left as it was.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.condition-attribute-type-gates-the-write")]
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
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("a")), ("v", N("1"))));
        var err = await Error(
            () => ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S("a")), ("v", N("2"))),
                ConditionExpression = "attribute_type(v, :t)",
                ExpressionAttributeValues = Item((":t", S("S"))),
            }),
            "a write conditioned on v being a String when it is a Number");
        Ensure(err.Code == "ConditionalCheckFailedException", $"expected ConditionalCheckFailedException, got {err.Code}");
        Ensure(err.Message == "The conditional request failed", $"expected 'The conditional request failed', got {Py.Repr(err.Message)}");
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("a"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(got["v"], N("1")), $"the refused write must leave v at 1, got {Py.Repr(got)}");
        // The same condition with the RIGHT type succeeds, so the refusal above was the type and not
        // the function being unimplemented.
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("a")), ("v", N("3"))),
            ConditionExpression = "attribute_type(v, :t)",
            ExpressionAttributeValues = Item((":t", S("N"))),
        });
        got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("a"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(got["v"], N("3")), $"the matching condition must let the write through, got {Py.Repr(got)}");
    }

    /// <summary>The Error dict from a call that MUST be refused.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> call, string what)
    {
        var error = await FidelityContext.CaptureError(call);
        return error is not null ? (error.Code, error.Message) : throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
    }
}
