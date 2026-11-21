// Translated from behavioral/dynamodb/check_put_item_not_attribute_exists_condition_on_new_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-not-attribute-exists-condition-on-new-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemNotAttributeExistsConditionOnNewKey
{
    private const string Check = "put-item-not-attribute-exists-condition-on-new-key";

    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// PutItem's ConditionExpression "NOT attribute_exists(pk)" holds for a key never
    /// written and the write applies; once the key exists the same condition fails the
    /// next PutItem with ConditionalCheckFailedException and leaves the item unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-not-attribute-exists-condition-on-new-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbnotex-");
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

        Dictionary<string, AttributeValue> Key() => Item(("pk", S("ddb-notexists-cond")));

        async Task<Dictionary<string, AttributeValue>?> Read() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;

        var prior = await Read();
        Ensure(prior is null, $"expected no prior item at the key, got {Py.Repr(prior)}");

        // NOT attribute_exists(pk) holds for a key that has never been written: the put applies.
        var itemV1 = Item(("pk", S("ddb-notexists-cond")), ("marker", S("first")));
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = itemV1, ConditionExpression = "NOT attribute_exists(pk)" });
        var got = await Read();
        Ensure(Py.Equal(got, itemV1), $"PutItem under a holding NOT attribute_exists(pk) left {Py.Repr(got)}, expected {Py.Repr(itemV1)}");

        // The same condition now fails: the key exists, so attribute_exists(pk) is true and
        // NOT negates it to false. The item is left unchanged.
        var itemV2 = Item(("pk", S("ddb-notexists-cond")), ("marker", S("second")));
        var (code, msg) = await Error(() => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = itemV2,
            ConditionExpression = "NOT attribute_exists(pk)",
        }));
        Ensure((code, msg) == Ccf, $"PutItem under a failing NOT attribute_exists(pk) gave {Py.Repr(code)}: {Py.Repr(msg)}");
        var after = await Read();
        Ensure(Py.Equal(after, itemV1), $"a rejected PutItem must leave {Py.Repr(itemV1)} unchanged, got {Py.Repr(after)}");
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

    /// <summary>The (code, message) of the ClientError <c>fn()</c> raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is not null
            ? (error.Code, error.Message)
            : throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }
}
