// Translated from behavioral/dynamodb/check_batch_get_item_over_the_hundred_key_cap_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-get-item-over-the-hundred-key-cap-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchGetItemOverTheHundredKeyCapRejected
{
    /// <summary>
    /// BatchGetItem retrieves at most 100 items per call, and the 101st makes the whole request
    /// invalid rather than returning 100 items with one in UnprocessedKeys.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-get-item-over-the-hundred-key-cap-rejected")]
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
        var err = await Error(
            () => ddb.BatchGetItemAsync(new BatchGetItemRequest
            {
                RequestItems = new()
                {
                    [table] = new KeysAndAttributes { Keys = [.. Enumerable.Range(0, 101).Select(i => Item(("pk", S($"k{i}"))))] },
                },
            }),
            "a BatchGetItem asking for 101 keys");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        var expectedMsg = $"1 validation error detected: Value at 'RequestItems.{table}.member.Keys' failed to " +
                          "satisfy constraint: Member must have length less than or equal to 100";
        Ensure(err.Message == expectedMsg, $"expected {Py.Repr(expectedMsg)}, got {Py.Repr(err.Message)}");
    }

    /// <summary>The Error dict from a call that MUST be refused.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> call, string what)
    {
        var error = await FidelityContext.CaptureError(call);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
    }
}
