// Translated from behavioral/dynamodb/check_put_item_oversized_overwrite_rejected_ignoring_existing_image.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-oversized-overwrite-rejected-ignoring-existing-image</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemOversizedOverwriteRejectedIgnoringExistingImage
{
    private const string CheckId = "put-item-oversized-overwrite-rejected-ignoring-existing-image";
    private static readonly (string Code, string Message) ErrValidation = ("ValidationException", "Item size has exceeded the maximum allowed size");
    private const int KB = 1024;

    /// <summary>Call fn() and grade the exact ClientError code and message it raises.</summary>
    private static async Task ExpectError(Func<Task<PutItemResponse>> fn, (string Code, string Message) expected)
    {
        var (result, err) = await FidelityContext.Capture(fn);
        if (err is not null)
        {
            Ensure(err.Code == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(err.Code)}: {Py.Repr(err.Message)}");
            Ensure(err.Message == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(err.Message)}");
            return;
        }

        throw Fail($"expected {Py.Repr(expected.Code)}, but the call succeeded: {result}");
    }

    /// <summary>
    /// A PutItem over the 400 KB item-size limit is rejected even when it would overwrite a
    /// far smaller existing item, and the existing item is left unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-oversized-overwrite-rejected-ignoring-existing-image")]
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        var key = Item(("pk", S("k")));

        // Positive control: a normal write to this table succeeds and reads back whole.
        var small = new string('e', 900);
        await ddb.PutItemAsync(t, Item(("pk", S("k")), ("blob", S(small))));
        var existing = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item ?? [];
        Ensure(existing.GetValueOrDefault("blob")?.S == small, "the initial small write must read back whole");

        // Break exactly one thing: overwrite that same key with an over-400KB item.
        var oversized = new string('o', 410 * KB);
        await ExpectError(() => ddb.PutItemAsync(t, Item(("pk", S("k")), ("blob", S(oversized)))), ErrValidation);

        // The effect is absent: the pre-existing small item is unchanged, not partially overwritten.
        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item ?? [];
        var unchangedBlob = unchanged.GetValueOrDefault("blob")?.S ?? string.Empty;
        Ensure(
            unchanged.GetValueOrDefault("blob")?.S == small,
            $"a rejected oversized overwrite must leave the existing item unchanged, got {Py.Repr(unchangedBlob)} (truncated to length for readability: {unchangedBlob.Length})");

        // Positive control after the rejection: the table still accepts ordinary writes.
        var otherKey = Item(("pk", S("k2")));
        await ddb.PutItemAsync(t, Item(("pk", S("k2")), ("blob", S(small))));
        var other = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = otherKey, ConsistentRead = true })).Item ?? [];
        Ensure(other.GetValueOrDefault("blob")?.S == small, "an ordinary write after the rejection must still succeed");
    }
}
