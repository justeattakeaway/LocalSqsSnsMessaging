// Translated from behavioral/dynamodb/check_transact_write_idempotency_mismatch.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-idempotency-mismatch</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteIdempotencyMismatch
{
    private const string CheckId = "transact-write-idempotency-mismatch";
    private const string ErrIdempotentMismatch = "IdempotentParameterMismatchException";

    /// <summary>
    /// Reusing a TransactWriteItems ClientRequestToken with a different item fails with
    /// IdempotentParameterMismatchException and writes nothing; reuse with the same item is a no-op.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-idempotency-mismatch")]
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
            Tags = [new Tag { Key = "sim2real-behav", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var token = Guid.NewGuid().ToString();
        TransactWriteItem OriginalWrite() => new() { Put = new Put { TableName = table, Item = Item(("pk", S("idem-m1")), ("v", N("1"))) } };

        // Positive control: the first write with a fresh token succeeds and lands.
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = [OriginalWrite()], ClientRequestToken = token });
        var original = await Get(ddb, table, "idem-m1");
        Ensure(Py.Equal(original, Item(("pk", S("idem-m1")), ("v", N("1")))), $"positive control: first write must land, got {Py.Repr(original)}");

        var error = await FidelityContext.CaptureError(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("idem-m2")), ("v", N("2"))) } }],
            ClientRequestToken = token,
        }));
        var code = error?.Code;
        var message = error?.Message;
        Ensure(code == ErrIdempotentMismatch, $"expected {ErrIdempotentMismatch}, got {Py.Repr(code)}");
        Ensure(
            message == "Specified idempotent token was used with different request parameters within the idempotency window",
            $"expected AWS's exact IdempotentParameterMismatchException message, got {Py.Repr(message)}");

        var unwritten = await Get(ddb, table, "idem-m2");
        Ensure(unwritten is null, $"rejected mismatched write must not apply, got {Py.Repr(unwritten)}");
        var still = await Get(ddb, table, "idem-m1");
        Ensure(Py.Equal(still, Item(("pk", S("idem-m1")), ("v", N("1")))), $"original item must remain unchanged, got {Py.Repr(still)}");

        // Positive control: shows the rejection is about the mismatch, not any token reuse.
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = [OriginalWrite()], ClientRequestToken = token });
        var replayed = await Get(ddb, table, "idem-m1");
        Ensure(
            Py.Equal(replayed, Item(("pk", S("idem-m1")), ("v", N("1")))),
            $"identical replay of the same token must not raise or change state, got {Py.Repr(replayed)}");
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
}
