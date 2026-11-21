// Translated from behavioral/dynamodb/check_execute_statement_return_consumed_capacity_enum_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-return-consumed-capacity-enum-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementReturnConsumedCapacityEnumRejected
{
    private const string CheckId = "execute-statement-return-consumed-capacity-enum-rejected";

    private static readonly (string Code, string Message) Validation = (
        "ValidationException",
        "1 validation error detected: Value 'BOGUS' at 'returnConsumedCapacity' failed to satisfy "
        + "constraint: Member must satisfy enum value set: [INDEXES, TOTAL, NONE]");

    /// <summary>Assert <paramref name="fn"/> raises exactly (code, message); an unexpected success raises too.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            Ensure(error.Code == expected.Code, $"expected error code {Py.Repr(expected.Code)}, got {Py.Repr(error.Code)}: {Py.Repr(error.Message)}");
            Ensure(error.Message == expected.Message, $"expected error message {Py.Repr(expected.Message)}, got {Py.Repr(error.Message)}");
            return;
        }

        throw Fail($"expected the statement to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>
    /// An out-of-enum ReturnConsumedCapacity on ExecuteStatement is ValidationException naming
    /// the member and echoing the value, and the statement writes nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-return-consumed-capacity-enum-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlrcc-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"INSERT INTO \"{table}\" VALUE {{'pk': 'rcc-bogus'}}",
                ReturnConsumedCapacity = new ReturnConsumedCapacity("BOGUS"),
            }),
            Validation);
        var after = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("rcc-bogus"))), ConsistentRead = true });
        Ensure(after.Item is not { Count: > 0 }, $"a rejected ExecuteStatement must write nothing, found {Py.Repr(after.Item)}");

        // Positive control: the identical statement, with a valid ReturnConsumedCapacity, executes.
        var ok = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"INSERT INTO \"{table}\" VALUE {{'pk': 'rcc-ok'}}",
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        Ensure((ok.Items ?? []).Count == 0, $"an INSERT must return no items, got {Py.Repr(ok.Items)}");
        var written = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("rcc-ok"))), ConsistentRead = true });
        Ensure(Py.Equal(written.Item, Item(("pk", S("rcc-ok")))), $"expected the write to land, got {Py.Repr(written.Item)}");
    }
}
