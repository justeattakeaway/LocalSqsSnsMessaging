// Translated from behavioral/dynamodb/check_delete_item_condition_or_and_precedence_applies_the_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-condition-or-and-precedence-applies-the-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemConditionOrAndPrecedenceAppliesTheDelete
{
    private const string Check = "delete-item-condition-or-and-precedence-applies-the-delete";

    private static readonly (string Code, string Message) Ccfe = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// DeleteItem's ConditionExpression `A OR B AND C` parses as `A OR (B AND C)` -- AND binds
    /// tighter than OR -- so a true A alone lets the delete apply even while B AND C is false.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-condition-or-and-precedence-applies-the-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        Dictionary<string, AttributeValue> Vals() => Item((":five", N("5")), (":ten", N("10")), (":twenty", N("20")));

        // A=(a=5) true, B=(a=10) false, C=(b=20) false: the explicit grouping `(A OR B) AND C`
        // = (true OR false) AND false = false -- must be refused.
        var groupedKey = Item(("pk", S("di-precedence-grouped")));
        await ddb.PutItemAsync(table, Item(("pk", S("di-precedence-grouped")), ("a", N("5")), ("b", N("10"))));
        await ExpectError(
            () => ddb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = groupedKey,
                ConditionExpression = "(a = :five OR a = :ten) AND b = :twenty",
                ExpressionAttributeValues = Vals(),
            }),
            Ccfe);
        var stillThere = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = groupedKey, ConsistentRead = true })).Item;
        Ensure(
            stillThere is not null && Py.Equal(stillThere.GetValueOrDefault("a"), N("5")),
            $"rejected delete under the grouped condition must not remove the item, got {Py.Repr(stillThere)}");

        // The identical values under the implicit, unparenthesized `A OR B AND C`: AND binds tighter
        // than OR, so this parses as `A OR (B AND C)` = true OR false = true -- the delete applies.
        var implicitKey = Item(("pk", S("di-precedence-implicit")));
        await ddb.PutItemAsync(table, Item(("pk", S("di-precedence-implicit")), ("a", N("5")), ("b", N("10"))));
        await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = implicitKey,
            ConditionExpression = "a = :five OR a = :ten AND b = :twenty",
            ExpressionAttributeValues = Vals(),
        });
        var gone = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = implicitKey, ConsistentRead = true })).Item;
        Ensure(gone is null, $"expected item absent after the implicit `A OR B AND C` delete applied, got {Py.Repr(gone)}");
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
