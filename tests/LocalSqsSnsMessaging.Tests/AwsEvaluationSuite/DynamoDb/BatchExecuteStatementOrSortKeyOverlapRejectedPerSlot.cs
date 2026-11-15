// Translated from behavioral/dynamodb/check_batch_execute_statement_or_sort_key_overlap_rejected_per_slot.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-execute-statement-or-sort-key-overlap-rejected-per-slot</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchExecuteStatementOrSortKeyOverlapRejectedPerSlot
{
    private const string Check = "batch-execute-statement-or-sort-key-overlap-rejected-per-slot";

    /// <summary>
    /// BatchExecuteStatement reports a WHERE clause ORing a non-key equality beside a sort-key
    /// branch as a per-slot ValidationError, not a thrown exception aborting the batch, and a
    /// sibling statement in the same call still succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-execute-statement-or-sort-key-overlap-rejected-per-slot")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlbxor-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("p")), ("sk", S("2")), ("hue", S("red"))));

        var resp = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements =
            [
                new BatchStatementRequest { Statement = $"""SELECT * FROM "{table}" WHERE pk = 'p' AND hue = 'red' OR pk = 'p' AND sk = '2'""" },
                new BatchStatementRequest { Statement = $"""SELECT * FROM "{table}" WHERE pk = 'p' AND sk = '2'""" },
            ],
        });
        var responses = resp.Responses;
        Ensure(responses.Count == 2, $"expected 2 per-statement responses, got {responses.Count}: {Py.Repr(responses.Select(Slot))}");

        var overlapping = responses[0];
        Ensure(overlapping.Item is null, $"expected the overlapping-condition slot to carry no Item, got {Py.Repr(Slot(overlapping))}");
        var error = overlapping.Error;
        Ensure(
            error is not null,
            "expected the overlapping-condition slot to be reported as a per-slot Error (not a " +
            $"thrown exception that would have aborted the whole batch), got {Py.Repr(Slot(overlapping))}");
        Ensure(error.Code?.Value == "ValidationError", $"expected Code ValidationError, got {Py.Repr(error.Code)}");
        Ensure(
            error.Message == "Overlapping conditions with range keys are not supported in where clause",
            $"unexpected message: {Py.Repr(error.Message)}");

        // Positive control: the sibling statement, addressed at the same item with a plain key
        // equality, in the SAME batch call, is unaffected by its neighbour's rejection.
        var sibling = responses[1];
        Ensure(sibling.Error is null, $"expected the sibling SELECT to succeed, got {Py.Repr(Slot(sibling))}");
        Ensure(
            Py.Equal(sibling.Item?.GetValueOrDefault("hue"), S("red")),
            $"expected the sibling SELECT to return the seeded item, got {Py.Repr(Slot(sibling))}");
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
