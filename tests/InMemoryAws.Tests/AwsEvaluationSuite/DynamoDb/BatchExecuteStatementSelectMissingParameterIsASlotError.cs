// Translated from behavioral/dynamodb/check_batch_execute_statement_select_missing_parameter_is_a_slot_error.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-execute-statement-select-missing-parameter-is-a-slot-error</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchExecuteStatementSelectMissingParameterIsASlotError
{
    private const string Check = "batch-execute-statement-select-missing-parameter-is-a-slot-error";

    private static readonly Dictionary<string, object?> ErrorSlot = new(StringComparer.Ordinal)
    {
        ["Code"] = "ValidationError",
        ["Message"] = "Number of parameters in request and statement don't match.",
    };

    /// <summary>
    /// A BatchExecuteStatement SELECT whose placeholder has no matching Parameters entry
    /// fails only its own slot with a per-slot ValidationError, not a thrown exception, and a
    /// sibling SELECT with its parameter supplied still returns its item.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchExecuteStatement.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-execute-statement-select-missing-parameter-is-a-slot-error")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await Table(ctx, ddb);
        var goodItem = Item(("pk", S("sibling")), ("val", S("present")));
        await ddb.PutItemAsync(t, goodItem);

        var resp = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements =
            [
                new BatchStatementRequest { Statement = $"SELECT * FROM \"{t}\" WHERE pk = ?", ConsistentRead = true },
                new BatchStatementRequest { Statement = $"SELECT * FROM \"{t}\" WHERE pk = ?", Parameters = [S("sibling")], ConsistentRead = true },
            ],
        });
        var responses = resp.Responses;
        Ensure(responses.Count == 2, $"expected 2 per-statement responses, got {responses.Count}: {Py.Repr(responses.Select(Slot))}");
        var (bad, good) = (Slot(responses[0]), Slot(responses[1]));

        Ensure(
            Py.Equal(bad, new Dictionary<string, object?> { ["Error"] = ErrorSlot }),
            $"expected the slot with no Parameters entry to carry exactly {{'Error': {Py.Repr(ErrorSlot)}}}, got {Py.Repr(bad)}");
        Ensure(
            Py.Equal(good, new Dictionary<string, object?> { ["TableName"] = t, ["Item"] = goodItem }),
            $"expected the sibling SELECT to carry its matched item, got {Py.Repr(good)}");

        // Positive control: the identical statement text succeeds once its Parameters entry
        // is supplied, isolating the missing-placeholder value as the cause of the slot error.
        var resp2 = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements =
            [
                new BatchStatementRequest { Statement = $"SELECT * FROM \"{t}\" WHERE pk = ?", Parameters = [S("sibling")], ConsistentRead = true },
            ],
        });
        var fixedSlots = resp2.Responses.Select(Slot).ToList();
        Ensure(
            Py.Equal(fixedSlots, new[] { new Dictionary<string, object?> { ["TableName"] = t, ["Item"] = goodItem } }),
            $"expected supplying the parameter to make the statement succeed, got {Py.Repr(fixedSlots)}");
    }

    private static async Task<string> Table(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
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
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        return t;
    }

    /// <summary>A statement response as boto3 hands it back: only the members present.</summary>
    private static Dictionary<string, object?> Slot(BatchStatementResponse r)
    {
        var slot = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (r.Error is { } e)
        {
            var error = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (e.Item is not null)
            {
                error["Item"] = e.Item;
            }

            if (e.Code is not null)
            {
                error["Code"] = e.Code;
            }

            if (e.Message is not null)
            {
                error["Message"] = e.Message;
            }

            slot["Error"] = error;
        }

        if (r.TableName is not null)
        {
            slot["TableName"] = r.TableName;
        }

        if (r.Item is not null)
        {
            slot["Item"] = r.Item;
        }

        return slot;
    }
}
