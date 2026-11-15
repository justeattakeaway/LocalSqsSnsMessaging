// Translated from behavioral/dynamodb/check_batch_execute_statement_return_consumed_capacity_enum_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-execute-statement-return-consumed-capacity-enum-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchExecuteStatementReturnConsumedCapacityEnumRejected
{
    private const string Check = "batch-execute-statement-return-consumed-capacity-enum-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// An out-of-enum ReturnConsumedCapacity on BatchExecuteStatement is ValidationException
    /// naming the member and echoing the value, and the whole batch writes nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-execute-statement-return-consumed-capacity-enum-rejected")]
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

        var (code, message) = await Error(() => ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements = [new BatchStatementRequest { Statement = $"INSERT INTO \"{t}\" VALUE {{'pk':'rcc-bogus'}}" }],
            ReturnConsumedCapacity = new ReturnConsumedCapacity("BOGUS"),
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code}: {Py.Repr(message)}");
        Ensure(
            message ==
            "1 validation error detected: Value 'BOGUS' at 'returnConsumedCapacity' failed to " +
            "satisfy constraint: Member must satisfy enum value set: [INDEXES, TOTAL, NONE]",
            $"unexpected message {Py.Repr(message)}");
        var after = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("rcc-bogus"))), ConsistentRead = true });
        Ensure(after.Item is null, $"a rejected BatchExecuteStatement must write nothing, found {Py.Repr(after.Item)}");

        // Positive control: the identical statement, with a valid ReturnConsumedCapacity, executes.
        var ok = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements = [new BatchStatementRequest { Statement = $"INSERT INTO \"{t}\" VALUE {{'pk':'rcc-ok'}}" }],
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        Ensure(ok.Responses[0].Error is null, $"expected the well-formed call to succeed, got {Py.Repr(ok.Responses[0].Error?.Message)}");
        var written = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("rcc-ok"))), ConsistentRead = true });
        Ensure(Py.Equal(written.Item, Item(("pk", S("rcc-ok")))), $"expected the write to land, got {Py.Repr(written.Item)}");
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
