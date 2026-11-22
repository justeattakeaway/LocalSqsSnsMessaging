// Translated from behavioral/dynamodb/check_batch_execute_statement_update_stored_type_mismatch_is_a_slot_error_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-execute-statement-update-stored-type-mismatch-is-a-slot-error-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchExecuteStatementUpdateStoredTypeMismatchIsASlotErrorRejected
{
    private const string Check = "batch-execute-statement-update-stored-type-mismatch-is-a-slot-error-rejected";
    private const string WrongType = "An operand in the update expression has an incorrect data type";
    private const string InvalidPath = "The document path provided in the update expression is invalid for update";

    /// <summary>
    /// BatchExecuteStatement reports an UPDATE that meets a stored value of the wrong type, or a path
    /// through a stored scalar, as that slot's ValidationError while the other slots still apply.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchExecuteStatement.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-execute-statement-update-stored-type-mismatch-is-a-slot-error-rejected")]
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
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(t), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 150);

        var doc = Item(("pk", S("doc")), ("s", S("text")), ("m", M(("x", N("1")))));
        var counter = Item(("pk", S("counter")), ("n", N("5")));
        await ddb.PutItemAsync(t, doc);
        await ddb.PutItemAsync(t, counter);

        var outResp = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements =
            [
                new BatchStatementRequest { Statement = $"UPDATE \"{t}\" SET s = s + 1 WHERE pk = ?", Parameters = [S("doc")] },
                new BatchStatementRequest { Statement = $"UPDATE \"{t}\" SET n = n + 1 WHERE pk = ?", Parameters = [S("counter")] },
            ],
        });
        var responses = outResp.Responses.Select(Slot).ToList();
        Ensure(
            Py.Equal(responses, new[] { Refused(WrongType, t), Applied(t) }),
            $"slot responses for the stored-type mismatch were {Py.Repr(responses)}");

        outResp = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements =
            [
                new BatchStatementRequest { Statement = $"UPDATE \"{t}\" SET m.x.y = 1 WHERE pk = ?", Parameters = [S("doc")] },
                new BatchStatementRequest { Statement = $"UPDATE \"{t}\" SET n = n + 1 WHERE pk = ?", Parameters = [S("counter")] },
            ],
        });
        responses = outResp.Responses.Select(Slot).ToList();
        Ensure(
            Py.Equal(responses, new[] { Refused(InvalidPath, t), Applied(t) }),
            $"slot responses for the path through a scalar were {Py.Repr(responses)}");

        var after = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("doc"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(after, doc), $"a refused slot changed its item: {Py.Repr(after)}");
        // The sibling slot is the positive control: it applied once per batch.
        var n = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("counter"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(n, Item(("pk", S("counter")), ("n", N("7")))), $"the sibling slot read back as {Py.Repr(n)}");
    }

    private static Dictionary<string, object?> Refused(string message, string table) => new(StringComparer.Ordinal)
    {
        ["Error"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Code"] = "ValidationError", ["Message"] = message },
        ["TableName"] = table,
    };

    private static Dictionary<string, object?> Applied(string table) => new(StringComparer.Ordinal) { ["TableName"] = table };

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

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
