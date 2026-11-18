// Translated from behavioral/dynamodb/check_batch_write_duplicate_key_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-duplicate-key-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteDuplicateKeyRejected
{
    private const string Check = "batch-write-duplicate-key-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// BatchWriteItem MUST reject a single call that targets the SAME primary key twice (two
    /// PutRequests for the same pk) with ValidationException -- the whole call is invalidated up
    /// front, and NEITHER write (nor any other, unrelated write in the same batch) is applied.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-write-duplicate-key-rejected")]
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
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        // Positive control: a batch with no duplicate keys applies its writes. Without this, a
        // target that rejects every BatchWriteItem call would pass the negative below for the wrong
        // reason.
        await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [t] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("control-target")), ("v", N("0"))) } }] },
        });
        var controlAfter = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("control-target"))), ConsistentRead = true });
        Ensure(
            Py.Equal(controlAfter.Item?.GetValueOrDefault("v"), N("0")),
            $"positive control: a batch with no duplicate keys must apply its write, got {Py.Repr(controlAfter.Item)}");

        var (code, message) = await Error(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [t] =
                [
                    new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("dup-target")), ("v", N("1"))) } },
                    new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("dup-target")), ("v", N("2"))) } },
                    new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("unrelated")), ("v", N("3"))) } },
                ],
            },
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code}");
        Ensure(message == "Provided list of item keys contains duplicates", $"expected the duplicate-keys refusal message, got {Py.Repr(message)}");

        var dupAfter = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("dup-target"))), ConsistentRead = true });
        Ensure(dupAfter.Item is null, "a rejected duplicate-key batch must write nothing for that key");
        var unrelatedAfter = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("unrelated"))), ConsistentRead = true });
        Ensure(unrelatedAfter.Item is null, "a rejected batch must apply NONE of its writes, including unrelated ones");
    }

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
