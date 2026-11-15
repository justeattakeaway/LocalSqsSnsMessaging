// Translated from behavioral/dynamodb/check_update_item_key_attribute_immutable.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-key-attribute-immutable</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemKeyAttributeImmutable
{
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// UpdateItem MUST reject an UpdateExpression that attempts to SET a key attribute (the primary
    /// key is immutable post-creation) with ValidationException.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-key-attribute-immutable")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("pk", S("immutable-key")));
        await ddb.PutItemAsync(t, Item(("pk", S("immutable-key")), ("v", S("1"))));

        // Positive control: an ordinary (non-key) UpdateItem against this same item must succeed,
        // so the rejection below is specific to touching the key and not "every UpdateItem fails".
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "SET v = :v2",
            ExpressionAttributeValues = Item((":v2", S("2"))),
        });
        var afterControl = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;
        Ensure(
            afterControl is not null && afterControl["v"].S == "2",
            $"positive control: expected v=2 after a legitimate UpdateItem, got {Py.Repr(afterControl)}");

        var err = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "SET pk = :newpk",
            ExpressionAttributeValues = Item((":newpk", S("different-key"))),
        }));
        Ensure(err.Code == ErrValidation, $"expected {ErrValidation}, got {ErrorRepr(err)}");
        const string expected = "One or more parameter values were invalid: Cannot update attribute pk. This attribute is part of the key";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;
        Ensure(unchanged is not null, "rejected key-attribute update must leave the item retrievable at its original key");
        Ensure(unchanged["v"].S == "2", $"rejected key-attribute update must not mutate the item, got v={Py.Repr(unchanged.GetValueOrDefault("v"))}");
    }

    /// <summary>The error of a call real AWS refuses; an unexpected success raises.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"real AWS rejects this UpdateItem, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    private static string ErrorRepr(AwsError error) =>
        Py.Repr(new Dictionary<string, object?> { ["Message"] = error.Message, ["Code"] = error.Code });
}
