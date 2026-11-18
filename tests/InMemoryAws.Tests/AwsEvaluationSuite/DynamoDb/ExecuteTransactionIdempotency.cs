// Translated from behavioral/dynamodb/check_execute_transaction_idempotency.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-transaction-idempotency</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteTransactionIdempotency
{
    private const string ErrIdempotentMismatch = "IdempotentParameterMismatchException";

    /// <summary>
    /// An ExecuteTransaction replay with the same ClientRequestToken and payload is deduplicated and
    /// billed as a read, not a write; the same token with a different payload fails with
    /// IdempotentParameterMismatchException and writes nothing under the mismatched key.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-transaction-idempotency")]
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
            Tags = [new Tag { Key = "sim2real", Value = "execute-transaction-idempotency" }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var token = Guid.NewGuid().ToString("N");
        List<ParameterizedStatement> Statements(string pk) =>
        [
            new ParameterizedStatement { Statement = $"INSERT INTO \"{t}\" VALUE {{'pk': ?, 'v': ?}}", Parameters = [S(pk), N("1")] },
        ];

        var resp1 = await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements = Statements("et-idem-1"),
            ClientRequestToken = token,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var cc1 = resp1.ConsumedCapacity[0];
        Ensure((cc1.WriteCapacityUnits ?? 0) > 0, $"the first use of a fresh token must bill a write: {Repr(cc1)}");
        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("et-idem-1"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(item["v"], N("1")), Py.Repr(item));

        var resp2 = await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements = Statements("et-idem-1"),
            ClientRequestToken = token,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var cc2 = resp2.ConsumedCapacity[0];
        Ensure((cc2.WriteCapacityUnits ?? 0) == 0, $"a deduplicated replay must bill no write capacity: {Repr(cc2)}");
        Ensure((cc2.ReadCapacityUnits ?? 0) > 0, $"a deduplicated replay must be served by reading the ledger: {Repr(cc2)}");
        var item2 = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("et-idem-1"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(item2["v"], N("1")), "deduplicated call must not alter state");

        var (code, message) = await ErrorCode(() => ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements = Statements("et-idem-DIFFERENT"),
            ClientRequestToken = token,
        }));
        Ensure(code == ErrIdempotentMismatch, code ?? "None");
        Ensure(
            message == "Specified idempotent token was used with different request parameters within the idempotency window",
            $"expected the exact IdempotentParameterMismatchException message AWS returns, got {Py.Repr(message)}");

        var absent = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("et-idem-DIFFERENT"))), ConsistentRead = true });
        Ensure(absent.Item is null, $"rejected mismatched payload must not be applied: {Py.Repr(absent.Item)}");
    }

    /// <summary>Return fn()'s error code and message; an unexpected success raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a rejection but the call SUCCEEDED and returned {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>The ConsumedCapacity entry as boto3 hands it back: only the members present.</summary>
    private static string Repr(ConsumedCapacity c)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (c.TableName is not null)
        {
            dict["TableName"] = c.TableName;
        }

        if (c.CapacityUnits is { } units)
        {
            dict["CapacityUnits"] = units;
        }

        if (c.ReadCapacityUnits is { } read)
        {
            dict["ReadCapacityUnits"] = read;
        }

        if (c.WriteCapacityUnits is { } write)
        {
            dict["WriteCapacityUnits"] = write;
        }

        return Py.Repr(dict);
    }
}
