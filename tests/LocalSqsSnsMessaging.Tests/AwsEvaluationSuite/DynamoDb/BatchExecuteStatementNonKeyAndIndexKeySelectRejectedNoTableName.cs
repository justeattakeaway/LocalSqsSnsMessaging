// Translated from behavioral/dynamodb/check_batch_execute_statement_non_key_and_index_key_select_rejected_no_table_name.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-execute-statement-non-key-and-index-key-select-rejected-no-table-name</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchExecuteStatementNonKeyAndIndexKeySelectRejectedNoTableName
{
    private const string Check = "batch-execute-statement-non-key-and-index-key-select-rejected-no-table-name";
    private static readonly (string Code, string Message) SelectError = (
        "ValidationError",
        "Select statements within BatchExecuteStatement must specify the primary key in the where clause.");

    /// <summary>
    /// A BatchExecuteStatement SELECT naming a non-key attribute on the base table, or naming
    /// both the table and index keys on an index-qualified member, fails its own slot with the same
    /// per-slot ValidationError and no TableName; a sibling keyed on the base table still returns.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-execute-statement-non-key-and-index-key-select-rejected-no-table-name")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await PartiqlTable(ctx);
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("s1")), ("status", S("active")), ("ss", S("yes"))));

        var resp = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements =
            [
                new BatchStatementRequest { Statement = $"SELECT * FROM \"{t}\" WHERE attribute_exists(ss)" },
                new BatchStatementRequest { Statement = $"SELECT * FROM \"{t}\".\"status-index\" WHERE pk='p1' AND status='active' " },
                new BatchStatementRequest { Statement = $"SELECT * FROM \"{t}\" WHERE pk='p1' AND sk='s1' " },
            ],
        });
        var responses = resp.Responses;
        Ensure(responses.Count == 3, $"expected 3 per-statement responses, got {responses.Count}: {Py.Repr(responses.Select(Slot))}");

        var (nonKeySlot, indexQualifiedSlot, goodSlot) = (responses[0], responses[1], responses[2]);

        foreach (var (label, slot) in new[] { ("non-key base-table", nonKeySlot), ("index-qualified", indexQualifiedSlot) })
        {
            Ensure(slot.Item is null, $"expected the {label} SELECT slot to carry no Item, got {Py.Repr(Slot(slot))}");
            Ensure(
                slot.TableName is null,
                $"expected the {label} SELECT's pre-run error slot to carry no TableName, got {Py.Repr(Slot(slot))}");
            var error = slot.Error;
            Ensure(
                error is not null,
                $"expected the {label} SELECT to be a per-slot Error, not a thrown exception aborting " +
                $"the whole batch, got {Py.Repr(Slot(slot))}");
            Ensure(
                (error.Code?.Value, error.Message) == SelectError,
                $"unexpected {label} slot error: {Py.Repr(Slot(slot)["Error"])}");
        }

        // Positive control: the sibling statement, keyed on the base table's primary key in the
        // SAME batch call, is unaffected by its neighbours' rejection and carries a TableName.
        Ensure(goodSlot.Error is null, $"expected the base-table SELECT to succeed, got {Py.Repr(Slot(goodSlot))}");
        Ensure(goodSlot.TableName == t, $"expected the good slot's TableName to be {Py.Repr(t)}, got {Py.Repr(Slot(goodSlot))}");
        Ensure(
            Py.Equal(goodSlot.Item?.GetValueOrDefault("status"), S("active")),
            $"expected the base-table SELECT to return the seeded item, got {Py.Repr(Slot(goodSlot))}");
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
