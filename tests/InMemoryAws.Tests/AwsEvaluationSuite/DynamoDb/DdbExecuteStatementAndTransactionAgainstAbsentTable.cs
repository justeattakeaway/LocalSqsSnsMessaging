// Translated from behavioral/dynamodb/check_ddb_execute_statement_and_transaction_against_absent_table.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddb-execute-statement-and-transaction-against-absent-table</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbExecuteStatementAndTransactionAgainstAbsentTable
{
    private const string Check = "ddb-execute-statement-and-transaction-against-absent-table";

    private static readonly (string Code, string Message) NotFound = ("ResourceNotFoundException", "Requested resource not found");

    private static readonly (string Code, string Message) Cancelled = (
        "TransactionCanceledException",
        "Transaction cancelled, please refer cancellation reasons for specific reasons [ResourceNotFound]");

    /// <summary>
    /// ExecuteStatement naming a missing table is ResourceNotFoundException; ExecuteTransaction
    /// naming one is TransactionCanceledException with a ResourceNotFound cancellation reason, not a
    /// top-level ResourceNotFoundException.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ddb-execute-statement-and-transaction-against-absent-table")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await MakeTable(ctx);
        var absent = FidelityContext.UniqueName("sim2real-ddb-execabsent-");

        var selectPresent = $"SELECT * FROM \"{table}\" WHERE pk = 'k1'";
        var selectAbsent = $"SELECT * FROM \"{absent}\" WHERE pk = 'k1'";

        var present = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = selectPresent });
        Ensure(
            Py.Equal(present.Items, new[] { Item(("pk", S("k1"))) }),
            $"ExecuteStatement on the real table returned {Py.Repr(present.Items)}, expected the seeded item");

        var (code, msg, _) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = selectAbsent }));
        Ensure(
            (code, msg) == NotFound,
            $"ExecuteStatement naming a missing table gave {Py.Repr(code)}: {Py.Repr(msg)}, expected {Py.Repr(NotFound)}");

        var presentTxn = await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements = [new ParameterizedStatement { Statement = selectPresent }],
        });
        Ensure(
            Py.Equal(presentTxn.Responses[0].Item, Item(("pk", S("k1")))),
            $"ExecuteTransaction on the real table returned {Py.Repr(presentTxn.Responses.Select(r => r.Item).ToList())}, expected the " +
            "seeded item");

        var (code2, msg2, error) = await Error(() => ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements = [new ParameterizedStatement { Statement = selectAbsent }],
        }));
        Ensure(
            (code2, msg2) == Cancelled,
            $"ExecuteTransaction naming a missing table gave {Py.Repr(code2)}: {Py.Repr(msg2)}, expected {Py.Repr(Cancelled)} " +
            "(not a top-level ResourceNotFoundException)");
        var reasons = error.CancellationReasons()?.Select(BotoReason).ToList();
        var expectedReasons = new List<Dictionary<string, object?>>
        {
            new() { ["Code"] = "ResourceNotFound", ["Message"] = "Requested resource not found" },
        };
        Ensure(
            reasons is not null && Py.Equal(reasons, expectedReasons),
            $"ExecuteTransaction's CancellationReasons were {Py.Repr(reasons)}");
    }

    /// <summary>A cancellation reason as boto3 hands it back: only the members present.</summary>
    private static Dictionary<string, object?> BotoReason(CancellationReason reason)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (reason.Item is not null)
        {
            result["Item"] = reason.Item;
        }

        if (reason.Code is not null)
        {
            result["Code"] = reason.Code;
        }

        if (reason.Message is not null)
        {
            result["Message"] = reason.Message;
        }

        return result;
    }

    /// <summary>The (code, message, error) a ClientError from fn() carries; raises on an unexpected success.</summary>
    private static async Task<(string? Code, string? Message, AwsError Error)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
        }

        return (error.Code, error.Message, error);
    }

    /// <summary>A tagged on-demand table keyed pk (S) HASH, seeded with one item, deleted at cleanup.</summary>
    private static async Task<string> MakeTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-ddb-execpresent-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(name, Item(("pk", S("k1"))));
        return name;
    }
}
