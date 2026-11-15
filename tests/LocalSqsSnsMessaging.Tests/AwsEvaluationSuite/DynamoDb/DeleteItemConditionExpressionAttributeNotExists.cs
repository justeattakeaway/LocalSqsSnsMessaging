// Translated from behavioral/dynamodb/check_delete_item_condition_expression_attribute_not_exists.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-condition-expression-attribute-not-exists</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemConditionExpressionAttributeNotExists
{
    private static readonly (string Code, string Message) Ccfe = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>Assert <c>fn()</c> raises exactly <c>(code, message)</c>.</summary>
    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            var got = (error.Code, error.Message);
            Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
            return;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }

    /// <summary>
    /// DeleteItem with ConditionExpression attribute_not_exists fails with
    /// ConditionalCheckFailedException while the named attribute is present on the
    /// item, and succeeds once the item carries no such attribute.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-condition-expression-attribute-not-exists")]
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

        var presentKey = Item(("pk", S("di-present")));
        await ddb.PutItemAsync(table, Item(("pk", S("di-present")), ("flag", S("active"))));

        // Break: 'flag' is present on the item, so attribute_not_exists(flag) must reject the delete.
        await ExpectError(
            async () => await ddb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = presentKey,
                ConditionExpression = "attribute_not_exists(flag)",
            }),
            Ccfe);

        // Effect absent: an independent read shows the rejected delete never landed.
        var stillThere = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = presentKey, ConsistentRead = true })).Item;
        Ensure(
            stillThere is not null && stillThere.TryGetValue("flag", out var flag) && Py.Equal(flag, S("active")),
            $"rejected delete must not remove the item, got {Py.Repr(stillThere)}");

        // Positive control: a sibling item with no 'flag' attribute satisfies the
        // identical condition, so the delete must succeed.
        var absentKey = Item(("pk", S("di-absent")));
        await ddb.PutItemAsync(table, Item(("pk", S("di-absent")), ("other", S("x"))));
        await ddb.DeleteItemAsync(new DeleteItemRequest { TableName = table, Key = absentKey, ConditionExpression = "attribute_not_exists(flag)" });

        var gone = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = absentKey, ConsistentRead = true })).Item;
        Ensure(gone is null, $"expected item absent after the satisfied-condition delete, got {Py.Repr(gone)}");
    }
}
