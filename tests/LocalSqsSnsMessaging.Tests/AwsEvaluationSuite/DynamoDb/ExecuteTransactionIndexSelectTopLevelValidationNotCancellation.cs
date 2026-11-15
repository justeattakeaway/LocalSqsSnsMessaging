// Translated from behavioral/dynamodb/check_execute_transaction_index_select_top_level_validation_not_cancellation.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-transaction-index-select-top-level-validation-not-cancellation</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteTransactionIndexSelectTopLevelValidationNotCancellation
{
    private const string CheckId = "execute-transaction-index-select-top-level-validation-not-cancellation";

    /// <summary>
    /// A SELECT addressed at a GSI inside ExecuteTransaction is a top-level ValidationException
    /// naming the statement, never a TransactionCanceledException; the same statement inside
    /// BatchExecuteStatement instead fails only that slot and leaves its sibling intact.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_ExecuteTransaction.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-transaction-index-select-top-level-validation-not-cancellation")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        var left = ctx.TimeLeftSeconds;
        var clock = FidelityTime.StartNew();
        var budget = TimeSpan.FromSeconds(Math.Max(1.0, Math.Min(60.0, left - 5.0)));
        Dictionary<string, string?> gsiStatus;
        while (true)
        {
            var described = (await ddb.DescribeTableAsync(table)).Table;
            gsiStatus = (described.GlobalSecondaryIndexes ?? []).ToDictionary(i => i.IndexName, i => i.IndexStatus?.Value, StringComparer.Ordinal);
            if (gsiStatus.GetValueOrDefault("gsi1") == "ACTIVE" || clock.Elapsed >= budget)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(gsiStatus.GetValueOrDefault("gsi1") == "ACTIVE", $"gsi1 never became ACTIVE: {Py.Repr(gsiStatus)}");

        await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S("s1")), ("gpk", S("g1"))));

        var indexSelect = $"SELECT * FROM \"{table}\".\"gsi1\" WHERE gpk='g1'";

        var (code, message, response) = await Error(() => ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements = [new ParameterizedStatement { Statement = indexSelect }],
        }));
        Ensure(code == "ValidationException", $"expected ValidationException, got {Py.Repr(code)}");
        Ensure(code != "TransactionCanceledException", "an index-qualified SELECT must fail validation, not cancellation");
        const string expected = "Validation failed in TransactStatements[0]: Reads on indices are not supported within transactions.";
        Ensure(message == expected, $"unexpected message: {Py.Repr(message)}");
        Ensure(
            response.CancellationReasons() is null,
            $"a top-level validation failure carries no per-statement CancellationReasons, got {response}");

        // Positive control: ExecuteTransaction itself still works for a base-table read.
        var goodTxn = (await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements = [new ParameterizedStatement { Statement = $"SELECT * FROM \"{table}\" WHERE pk='p1' AND sk='s1'" }],
        })).Responses;
        Ensure(
            Py.Equal(goodTxn[0].Item["gpk"], S("g1")),
            $"expected a base-table ExecuteTransaction SELECT to succeed, got {Py.Repr(goodTxn.Select(r => r.Item).ToList())}");

        var resp = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements =
            [
                new BatchStatementRequest { Statement = indexSelect },
                new BatchStatementRequest { Statement = $"SELECT * FROM \"{table}\" WHERE pk='p1' AND sk='s1'" },
            ],
        });
        var responses = resp.Responses;
        Ensure(responses.Count == 2, $"expected 2 per-statement responses, got {responses.Count}: {Py.Repr(responses.Select(Repr).ToList())}");

        var badSlot = responses[0];
        Ensure(badSlot.Item is null, $"expected the index-qualified SELECT slot to carry no Item, got {Py.Repr(Repr(badSlot))}");
        var slotError = badSlot.Error;
        Ensure(
            slotError is not null,
            $"expected the index-qualified SELECT to be a per-slot Error, not a thrown exception aborting the whole batch, got {Py.Repr(Repr(badSlot))}");
        Ensure(slotError.Code?.Value == "ValidationError", $"expected Code ValidationError, got {Py.Repr(slotError.Code?.Value)}");
        const string expectedBatchMsg = "Select statements within BatchExecuteStatement must specify the primary key in the where clause.";
        Ensure(slotError.Message == expectedBatchMsg, $"unexpected message: {Py.Repr(slotError.Message)}");

        var goodSlot = responses[1];
        Ensure(goodSlot.Error is null, $"expected the base-table SELECT sibling to succeed, got {Py.Repr(Repr(goodSlot))}");
        Ensure(
            Py.Equal(goodSlot.Item?.GetValueOrDefault("gpk"), S("g1")),
            $"expected the sibling slot to return the seeded item, got {Py.Repr(Repr(goodSlot))}");
    }

    /// <summary>(Error.Code, Error.Message, response) of the ClientError fn() raises; a success fails.</summary>
    private static async Task<(string? Code, string? Message, AwsError Response)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {Py.Repr(result)}");
        }

        return (error.Code, error.Message, error);
    }

    /// <summary>A BatchStatementResponse as boto3 hands it back: only the members present.</summary>
    private static Dictionary<string, object?> Repr(BatchStatementResponse r)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (r.Error is not null)
        {
            dict["Error"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Code"] = r.Error.Code?.Value, ["Message"] = r.Error.Message };
        }

        if (r.TableName is not null)
        {
            dict["TableName"] = r.TableName;
        }

        if (r.Item is not null)
        {
            dict["Item"] = r.Item;
        }

        return dict;
    }
}
