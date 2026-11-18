// Translated from behavioral/dynamodb/check_batch_write_item_put_and_delete_for_the_same_key_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-item-put-and-delete-for-the-same-key-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteItemPutAndDeleteForTheSameKeyRejected
{
    private const string Check = "batch-write-item-put-and-delete-for-the-same-key-rejected";
    private const string ErrValidation = "ValidationException";
    private const string DupMessage = "Provided list of item keys contains duplicates";

    /// <summary>
    /// BatchWriteItem rejects a single call that pairs a PutRequest and a DeleteRequest on the
    /// SAME primary key with ValidationException, and applies neither that write nor any other,
    /// unrelated write in the same call.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchWriteItem.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-write-item-put-and-delete-for-the-same-key-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-bwpd-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        // Positive control: a batch with no repeated key applies its writes, so the rejection below
        // is attributable to the repeated key, not to a broken client.
        await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [table] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("control")), ("v", N("0"))) } }] },
        });
        var controlAfter = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("control"))), ConsistentRead = true });
        Ensure(
            Py.Equal(controlAfter.Item?.GetValueOrDefault("v"), N("0")),
            $"positive control: a batch with no repeated key must apply its write, got {Py.Repr(controlAfter.Item)}");

        // A pre-existing item under the contested key, so a wrongly-accepted DeleteRequest would be
        // visible as a loss rather than a no-op on an absent key.
        await ddb.PutItemAsync(table, Item(("pk", S("shared-target")), ("v", N("1"))));

        var (code, message) = await Error(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [table] =
                [
                    new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("shared-target")), ("v", N("2"))) } },
                    new WriteRequest { DeleteRequest = new DeleteRequest { Key = Item(("pk", S("shared-target"))) } },
                    new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("unrelated")), ("v", N("3"))) } },
                ],
            },
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code}: {Py.Repr(message)}");
        Ensure(message == DupMessage, $"expected the duplicate-keys refusal message, got {Py.Repr(message)}");

        var sharedAfter = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("shared-target"))), ConsistentRead = true });
        Ensure(
            Py.Equal(sharedAfter.Item?.GetValueOrDefault("v"), N("1")),
            "a rejected mixed Put/Delete batch on one key must leave it exactly as seeded, got " +
            $"{Py.Repr(sharedAfter.Item)}");
        var unrelatedAfter = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("unrelated"))), ConsistentRead = true });
        Ensure(
            unrelatedAfter.Item is null,
            "a rejected batch must apply NONE of its writes, including unrelated ones, got " +
            $"{Py.Repr(unrelatedAfter.Item)}");
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

    /// <summary>The (Code, Message) of a call real AWS refuses. RAISES on unexpected success.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"real AWS refuses this call, but it SUCCEEDED and returned {Py.Repr(result)}");
    }
}
