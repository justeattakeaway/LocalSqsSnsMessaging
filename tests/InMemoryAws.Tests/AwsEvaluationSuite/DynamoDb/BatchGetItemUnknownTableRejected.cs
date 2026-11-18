// Translated from behavioral/dynamodb/check_batch_get_item_unknown_table_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-get-item-unknown-table-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchGetItemUnknownTableRejected
{
    private const string Check = "batch-get-item-unknown-table-rejected";

    /// <summary>
    /// A BatchGetItem whose RequestItems map names one real table and one syntactically valid but
    /// nonexistent table is ResourceNotFoundException for the whole call; the real table's own item is
    /// still readable by a plain GetItem after the refusal.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-get-item-unknown-table-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var realTable = FidelityContext.UniqueName("sim2real-behav-bgiunk-");
        var missingTable = FidelityContext.UniqueName("sim2real-behav-bgiunk-missing-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = realTable,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(realTable));
        await ddb.WaitForTableExists(realTable, delaySeconds: 2, maxAttempts: 250);

        var seed = Item(("pk", S("seed")), ("v", S("kept")));
        await ddb.PutItemAsync(realTable, seed);

        // Positive control: BatchGetItem over the real table alone works, so the rejection below is
        // attributable to the missing table and not to a broken request.
        var control = await ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new() { [realTable] = new KeysAndAttributes { Keys = [Item(("pk", S("seed")))], ConsistentRead = true } },
        });
        Ensure(
            Py.Equal(control.Responses[realTable], new[] { seed }),
            "positive control BatchGetItem on the real table alone did not return the seed, got " +
            $"{Py.Repr(control.Responses)}");

        var (code, message) = await Error(() => ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new()
            {
                [realTable] = new KeysAndAttributes { Keys = [Item(("pk", S("seed")))], ConsistentRead = true },
                [missingTable] = new KeysAndAttributes { Keys = [Item(("pk", S("x")))], ConsistentRead = true },
            },
        }));
        Ensure(
            code == "ResourceNotFoundException",
            "BatchGetItem naming one real table and one nonexistent table must fail with " +
            $"ResourceNotFoundException for the whole call, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(message == "Requested resource not found", $"unexpected message {Py.Repr(message)}");

        var after = await ddb.GetItemAsync(new GetItemRequest { TableName = realTable, Key = Item(("pk", S("seed"))), ConsistentRead = true });
        Ensure(
            Py.Equal(after.Item, seed),
            $"the refused BatchGetItem must have left the real table untouched, got {Py.Repr(after.Item)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it succeeded: {Py.Repr(result)}");
    }
}
