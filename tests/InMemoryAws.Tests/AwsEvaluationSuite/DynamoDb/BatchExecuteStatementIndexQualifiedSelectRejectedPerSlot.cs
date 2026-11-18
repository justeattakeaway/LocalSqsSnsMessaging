// Translated from behavioral/dynamodb/check_batch_execute_statement_index_qualified_select_rejected_per_slot.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-execute-statement-index-qualified-select-rejected-per-slot</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchExecuteStatementIndexQualifiedSelectRejectedPerSlot
{
    private const string Check = "batch-execute-statement-index-qualified-select-rejected-per-slot";

    /// <summary>
    /// BatchExecuteStatement reports a SELECT addressed at an index as a per-slot ValidationError,
    /// not a thrown exception aborting the batch, and a base-table sibling statement still succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-execute-statement-index-qualified-select-rejected-per-slot")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await PartiqlTable(ctx);
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("s1")), ("status", S("active"))));

        var resp = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements =
            [
                new BatchStatementRequest { Statement = $"""SELECT * FROM "{t}"."status-index" WHERE status='active'""" },
                new BatchStatementRequest { Statement = $"""SELECT * FROM "{t}" WHERE pk='p1' AND sk='s1'""" },
            ],
        });
        var responses = resp.Responses;
        Ensure(responses.Count == 2, $"expected 2 per-statement responses, got {responses.Count}: {Py.Repr(responses.Select(Slot))}");

        var bad = responses[0];
        Ensure(bad.Item is null, $"expected the index-qualified SELECT slot to carry no Item, got {Py.Repr(Slot(bad))}");
        var error = bad.Error;
        Ensure(
            error is not null,
            "expected the index-qualified SELECT to be reported as a per-slot Error " +
            $"(not a thrown exception that would have aborted the whole batch), got {Py.Repr(Slot(bad))}");
        Ensure(error.Code?.Value == "ValidationError", $"expected Code ValidationError, got {Py.Repr(error.Code)}");
        Ensure(
            error.Message == "Select statements within BatchExecuteStatement must specify the primary key in the where clause.",
            $"unexpected message: {Py.Repr(error.Message)}");

        // Positive control: the sibling statement, addressed at the base table in the SAME
        // batch call, is unaffected by its neighbour's rejection.
        var good = responses[1];
        Ensure(good.Error is null, $"expected the base-table SELECT to succeed, got {Py.Repr(Slot(good))}");
        Ensure(
            Py.Equal(good.Item?.GetValueOrDefault("status"), S("active")),
            $"expected the base-table SELECT to return the seeded item, got {Py.Repr(Slot(good))}");
    }

    private static async Task<string> PartiqlTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("status", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "status-index",
                    KeySchema = [new KeySchemaElement("status", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
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
