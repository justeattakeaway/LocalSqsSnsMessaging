// Translated from behavioral/dynamodb/check_batch_get_item_key_cap_is_aggregate_across_tables_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-get-item-key-cap-is-aggregate-across-tables-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchGetItemKeyCapIsAggregateAcrossTablesRejected
{
    private const string Check = "batch-get-item-key-cap-is-aggregate-across-tables-rejected";

    /// <summary>
    /// BatchGetItem's 100-key cap is a sum across every table in the request: 60 keys on one
    /// table plus 41 on another (101 total, neither table alone over 100) is refused, and the
    /// identical shape at exactly 100 combined keys succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-get-item-key-cap-is-aggregate-across-tables-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableA = FidelityContext.UniqueName("sim2real-behav-bgicapa-");
        var tableB = FidelityContext.UniqueName("sim2real-behav-bgicapb-");
        foreach (var table in new[] { tableA, tableB })
        {
            await ddb.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
                Tags = [new Tag { Key = "sim2real", Value = Check }],
            });
            ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        }

        foreach (var table in new[] { tableA, tableB })
        {
            await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        }

        await ddb.PutItemAsync(tableA, Item(("pk", S("a0")), ("val", S("canary-a"))));
        await ddb.PutItemAsync(tableB, Item(("pk", S("b0")), ("val", S("canary-b"))));

        List<Dictionary<string, AttributeValue>> KeysA60() => [.. Enumerable.Range(0, 60).Select(i => Item(("pk", S($"a{i}"))))];
        List<Dictionary<string, AttributeValue>> KeysB(int n) => [.. Enumerable.Range(0, n).Select(i => Item(("pk", S($"b{i}"))))];

        var (code, message) = await Error(() => ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new()
            {
                [tableA] = new KeysAndAttributes { Keys = KeysA60() },
                [tableB] = new KeysAndAttributes { Keys = KeysB(41) },
            },
        }));
        Ensure(
            code == "ValidationException",
            $"60 keys on {Py.Repr(tableA)} plus 41 on {Py.Repr(tableB)} (101 combined, neither table over 100) "
            + $"got {Py.Repr(code)}, expected ValidationException");
        const string expectedMessage = "Too many items requested for the BatchGetItem call";
        Ensure(message == expectedMessage, $"expected {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");

        var resp = await ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new()
            {
                [tableA] = new KeysAndAttributes { Keys = KeysA60() },
                [tableB] = new KeysAndAttributes { Keys = KeysB(40) },
            },
        });
        var unprocessed = resp.UnprocessedKeys ?? [];
        Ensure(
            unprocessed.Count == 0,
            "the identical shape at exactly 100 combined keys must not be refused or partial, got "
            + $"UnprocessedKeys {Py.Repr(unprocessed.ToDictionary(e => e.Key, e => e.Value.Keys))}");
        var canaryA = resp.Responses[tableA].ToDictionary(item => item["pk"].S, item => item["val"].S).GetValueOrDefault("a0");
        var canaryB = resp.Responses[tableB].ToDictionary(item => item["pk"].S, item => item["val"].S).GetValueOrDefault("b0");
        Ensure(
            (canaryA, canaryB) == ("canary-a", "canary-b"),
            $"the 100-combined-key call succeeded but did not return the stored canaries: {Py.Repr((canaryA, canaryB))}");
    }

    /// <summary>(Error.Code, Error.Message) of the error <paramref name="fn"/> raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
