// Translated from behavioral/dynamodb/check_execute_transaction_client_token_idempotent_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-transaction-client-token-idempotent-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteTransactionClientTokenIdempotentMismatchRejected
{
    /// <summary>
    /// Reusing an ExecuteTransaction ClientRequestToken with identical parameters is a safe replay;
    /// reusing it with different parameters fails with IdempotentParameterMismatchException and
    /// leaves the original item unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-transaction-client-token-idempotent-mismatch-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags =
            [
                new Tag { Key = "sim2real:managed", Value = "true" },
                new Tag { Key = "sim2real:prefix", Value = "sim2real-behav" },
            ],
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, tableName));
        await WaitTableActive(ddb, tableName);

        var token = Guid.NewGuid().ToString();
        var statement = $"INSERT INTO \"{tableName}\" VALUE {{'pk': ?, 'val': ?}}";
        static List<AttributeValue> OriginalParams() => [S("idem-item"), S("v1")];

        await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements = [new ParameterizedStatement { Statement = statement, Parameters = OriginalParams() }],
            ClientRequestToken = token,
        });
        var got = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("idem-item"))) });
        Ensure(got.Item?.GetValueOrDefault("val")?.S == "v1", $"expected v1 after the initial insert, got {Py.Repr(got.Item)}");

        // Positive control: without it, rejecting every reused token would pass the mismatch leg.
        await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements = [new ParameterizedStatement { Statement = statement, Parameters = OriginalParams() }],
            ClientRequestToken = token,
        });
        var replayed = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("idem-item"))) });
        Ensure(replayed.Item?.GetValueOrDefault("val")?.S == "v1", $"identical-payload replay must not alter state, got {Py.Repr(replayed.Item)}");

        var (code, message) = await ErrorCodeAndMessage(() => ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements = [new ParameterizedStatement { Statement = statement, Parameters = [S("idem-item"), S("v2-DIFFERENT")] }],
            ClientRequestToken = token,
        }));
        Ensure(code == "IdempotentParameterMismatchException", $"expected IdempotentParameterMismatchException, got {Py.Repr(code)}");
        const string expectedMessage =
            "Specified idempotent token was used with different request parameters within the "
            + "idempotency window";
        Ensure(message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");

        var still = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("idem-item"))) });
        Ensure(still.Item?.GetValueOrDefault("val")?.S == "v1", $"expected v1 to survive the rejected mismatched retry, got {Py.Repr(still.Item)}");
    }

    /// <summary>Return fn()'s error code and message; an unexpected success raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"real DynamoDB rejects a reused ClientRequestToken with different parameters, but the call SUCCEEDED and returned {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<TableDescription> WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 120.0, double intervalSeconds = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var desc = (await ddb.DescribeTableAsync(tableName)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never became ACTIVE");
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}
