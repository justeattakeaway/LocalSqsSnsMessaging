// Translated from behavioral/dynamodb/check_execute_transaction_exceeds_100_statements_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-transaction-exceeds-100-statements-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteTransactionExceeds100StatementsRejected
{
    private const string CheckId = "execute-transaction-exceeds-100-statements-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// ExecuteTransaction with exactly 100 statements commits them all; 101 statements is rejected
    /// with ValidationException and writes nothing.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.multiplestatements.transactions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-transaction-exceeds-100-statements-rejected")]
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
            Tags = [new Tag { Key = "sim2real-behav", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var insertStmt = $"INSERT INTO \"{t}\" VALUE {{'pk': ?}}";
        var atLimit = Enumerable.Range(0, 100)
            .Select(i => new ParameterizedStatement { Statement = insertStmt, Parameters = [S(string.Create(CultureInfo.InvariantCulture, $"etxn-atlimit-{i}"))] })
            .ToList();
        await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest { TransactStatements = atLimit });
        var first = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("etxn-atlimit-0"))), ConsistentRead = true });
        Ensure(first.Item is not null, "a transaction at the documented 100-statement ceiling must be applied in full");
        var last = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("etxn-atlimit-99"))), ConsistentRead = true });
        Ensure(last.Item is not null, "a transaction at the documented 100-statement ceiling must be applied in full");

        var tooMany = Enumerable.Range(0, 101)
            .Select(i => new ParameterizedStatement { Statement = insertStmt, Parameters = [S(string.Create(CultureInfo.InvariantCulture, $"etxn-over100-{i}"))] })
            .ToList();
        var (code, message) = await ErrorCode(() => ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest { TransactStatements = tooMany }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {Py.Repr(code)}");
        // The rendered list between these carries a per-statement object identity hash that varies
        // request to request; the prefix and suffix around it are the stable, documented contract.
        const string prefix = "1 validation error detected: Value '[";
        const string suffix = "]' at 'transactStatements' failed to satisfy constraint: "
            + "Member must have length less than or equal to 100";
        Ensure(
            message is not null && message.StartsWith(prefix, StringComparison.Ordinal) && message.EndsWith(suffix, StringComparison.Ordinal),
            $"expected the ValidationException message to start with {Py.Repr(prefix)} and end with {Py.Repr(suffix)}, got {Py.Repr(message)}");

        var rejected = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("etxn-over100-0"))), ConsistentRead = true });
        Ensure(rejected.Item is null, "a rejected oversized transaction must not have written any item");
    }

    /// <summary>The error code and message fn() raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected ExecuteTransaction to be rejected, but it succeeded: {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }
}
