// Translated from behavioral/dynamodb/check_batch_return_options_invalid_enum_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-return-options-invalid-enum-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchReturnOptionsInvalidEnumRejected
{
    private const string Check = "batch-return-options-invalid-enum-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// An out-of-enum ReturnConsumedCapacity (BatchGetItem, BatchWriteItem) or
    /// ReturnItemCollectionMetrics (BatchWriteItem) is ValidationException naming the member
    /// and echoing the value; the table gains nothing from any rejected call.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-return-options-invalid-enum-rejected")]
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
        // so a rejection below is attributable to the enum value and not to a broken client.
        await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [t] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("control"))) } }] },
        });
        var got = await ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new() { [t] = new KeysAndAttributes { Keys = [Item(("pk", S("control")))], ConsistentRead = true } },
        });
        Ensure(Py.Equal(got.Responses[t], new[] { Item(("pk", S("control"))) }), $"positive control not visible: {Py.Repr(got.Responses)}");

        var (code, message) = await Error(() => ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new() { [t] = new KeysAndAttributes { Keys = [Item(("pk", S("rcc-get")))] } },
            ReturnConsumedCapacity = new ReturnConsumedCapacity("BOGUS"),
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code}: {Py.Repr(message)}");
        Ensure(
            message ==
            "1 validation error detected: Value 'BOGUS' at 'returnConsumedCapacity' failed to " +
            "satisfy constraint: Member must satisfy enum value set: [INDEXES, TOTAL, NONE]",
            $"unexpected message {Py.Repr(message)}");
        var after = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("rcc-get"))), ConsistentRead = true });
        Ensure(after.Item is null, $"a rejected BatchGetItem must write nothing, found {Py.Repr(after.Item)}");

        (code, message) = await Error(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [t] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("rcc-write"))) } }] },
            ReturnConsumedCapacity = new ReturnConsumedCapacity("BOGUS"),
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code}: {Py.Repr(message)}");
        Ensure(
            message ==
            "1 validation error detected: Value 'BOGUS' at 'returnConsumedCapacity' failed to " +
            "satisfy constraint: Member must satisfy enum value set: [INDEXES, TOTAL, NONE]",
            $"unexpected message {Py.Repr(message)}");
        after = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("rcc-write"))), ConsistentRead = true });
        Ensure(after.Item is null, $"a rejected BatchWriteItem must write nothing, found {Py.Repr(after.Item)}");

        (code, message) = await Error(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [t] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("ricm-write"))) } }] },
            ReturnItemCollectionMetrics = new ReturnItemCollectionMetrics("BOGUS"),
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code}: {Py.Repr(message)}");
        Ensure(
            message ==
            "1 validation error detected: Value 'BOGUS' at 'returnItemCollectionMetrics' failed to " +
            "satisfy constraint: Member must satisfy enum value set: [SIZE, NONE]",
            $"unexpected message {Py.Repr(message)}");
        after = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("ricm-write"))), ConsistentRead = true });
        Ensure(after.Item is null, $"a rejected BatchWriteItem must write nothing, found {Py.Repr(after.Item)}");
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
