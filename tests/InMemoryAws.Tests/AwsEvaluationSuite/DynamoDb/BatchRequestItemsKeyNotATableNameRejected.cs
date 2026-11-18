// Translated from behavioral/dynamodb/check_batch_request_items_key_not_a_table_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-request-items-key-not-a-table-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchRequestItemsKeyNotATableNameRejected
{
    private const string Check = "batch-request-items-key-not-a-table-name-rejected";
    private const string ErrValidation = "ValidationException";
    private const string BadKey = "ab";

    /// <summary>
    /// A RequestItems map key that is not a legal table name (too short for the
    /// length-3 floor) is ValidationException for both BatchGetItem and BatchWriteItem,
    /// echoing the whole request value at 'requestItems'; an unrelated table is untouched.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-request-items-key-not-a-table-name-rejected")]
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
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 30);

        // Positive control: a well-formed batch write/read round trip works in this account,
        // so a rejection below is attributable to the malformed key, not to a broken client.
        await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [t] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("control"))) } }] },
        });
        var got = await ddb.ScanAsync(new ScanRequest { TableName = t, ConsistentRead = true });
        Ensure(Py.Equal(got.Items, new[] { Item(("pk", S("control"))) }), $"positive control not visible: {Py.Repr(got.Items)}");

        var (code, message) = await Error(() => ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new() { [BadKey] = new KeysAndAttributes { Keys = [Item(("pk", S("a")))] } },
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code}: {Py.Repr(message)}");
        Ensure(
            message ==
            "1 validation error detected: Value '{\"ab\":{\"Keys\":[{\"pk\":{\"S\":\"a\"}}]}}' at " +
            "'requestItems' failed to satisfy constraint: Map keys must satisfy constraint: " +
            "[Member must have length less than or equal to 255, Member must have length " +
            "greater than or equal to 3, Member must satisfy regular expression pattern: " +
            "[a-zA-Z0-9_.-]+]",
            $"unexpected message {Py.Repr(message)}");

        // The item is empty: a non-empty item embeds its AttributeValue inside the echoed
        // value through a representation AWS itself does not keep stable across requests,
        // so an exact-message assertion must not depend on it.
        (code, message) = await Error(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [BadKey] = [new WriteRequest { PutRequest = new PutRequest { Item = [] } }] },
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code}: {Py.Repr(message)}");
        Ensure(
            message ==
            "1 validation error detected: Value '{ab=[WriteRequest(putRequest=PutRequest(" +
            "item={}, workloadProfileName=null), deleteRequest=null)]}' at 'requestItems' " +
            "failed to satisfy constraint: Map keys must satisfy constraint: [Member must " +
            "have length less than or equal to 255, Member must have length greater than or " +
            "equal to 3, Member must satisfy regular expression pattern: [a-zA-Z0-9_.-]+]",
            $"unexpected message {Py.Repr(message)}");

        var after = await ddb.ScanAsync(new ScanRequest { TableName = t, ConsistentRead = true });
        Ensure(
            Py.Equal(after.Items, new[] { Item(("pk", S("control"))) }),
            $"the two rejected calls must leave the unrelated table unchanged, got {Py.Repr(after.Items)}");
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
