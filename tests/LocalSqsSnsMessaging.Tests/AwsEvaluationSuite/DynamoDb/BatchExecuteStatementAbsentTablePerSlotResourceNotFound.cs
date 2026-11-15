// Translated from behavioral/dynamodb/check_batch_execute_statement_absent_table_per_slot_resource_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-execute-statement-absent-table-per-slot-resource-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchExecuteStatementAbsentTablePerSlotResourceNotFound
{
    private const string Check = "batch-execute-statement-absent-table-per-slot-resource-not-found";

    /// <summary>
    /// A BatchExecuteStatement member naming a table that does not exist fails only its own
    /// slot with Error.Code 'ResourceNotFound', and the sibling member on a real table still
    /// returns its Item; the same holds when every member names an absent table.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchExecuteStatement.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-execute-statement-absent-table-per-slot-resource-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await PartiqlTable(ctx);
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("s1")), ("v", S("hit"))));
        var absent = FidelityContext.UniqueName("sim2real-behav-absent-");

        var resp = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements =
            [
                new BatchStatementRequest { Statement = $"""SELECT * FROM "{absent}" WHERE pk='x' AND sk='y'""" },
                new BatchStatementRequest { Statement = $"""SELECT * FROM "{t}" WHERE pk='p1' AND sk='s1'""" },
            ],
        });
        var responses = resp.Responses;
        Ensure(responses.Count == 2, $"expected 2 per-statement responses, got {responses.Count}: {Py.Repr(responses.Select(Slot))}");

        var bad = responses[0];
        Ensure(bad.Item is null, $"expected the absent-table slot to carry no Item, got {Py.Repr(Slot(bad))}");
        var error = bad.Error;
        Ensure(
            error is not null,
            "expected the absent-table statement to be a per-slot Error, not a thrown exception " +
            $"aborting the whole batch, got {Py.Repr(Slot(bad))}");
        Ensure(error.Code?.Value == "ResourceNotFound", $"expected Code ResourceNotFound, got {Py.Repr(error.Code)}");
        Ensure(error.Message == "Requested resource not found", $"expected AWS's exact message, got {Py.Repr(error.Message)}");

        // Positive control: the sibling statement, addressed at the real table in the SAME
        // batch call, is unaffected by its neighbour's rejection.
        var good = responses[1];
        Ensure(good.Error is null, $"expected the real-table statement to succeed, got {Py.Repr(Slot(good))}");
        Ensure(
            Py.Equal(good.Item?.GetValueOrDefault("v"), S("hit")),
            $"expected the real-table statement to return the seeded item, got {Py.Repr(Slot(good))}");

        var absent2 = FidelityContext.UniqueName("sim2real-behav-absent2-");
        var respAllAbsent = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements =
            [
                new BatchStatementRequest { Statement = $"""SELECT * FROM "{absent}" WHERE pk='x' AND sk='y'""" },
                new BatchStatementRequest { Statement = $"""SELECT * FROM "{absent2}" WHERE pk='x' AND sk='y'""" },
            ],
        });
        var allAbsentResponses = respAllAbsent.Responses;
        Ensure(
            allAbsentResponses.Count == 2,
            "expected 2 per-statement responses even when every member names an absent table, " +
            $"got {allAbsentResponses.Count}: {Py.Repr(allAbsentResponses.Select(Slot))}");
        foreach (var slot in allAbsentResponses)
        {
            var slotError = slot.Error;
            Ensure(slotError is not null, $"expected a per-slot Error, got {Py.Repr(Slot(slot))}");
            Ensure(slotError.Code?.Value == "ResourceNotFound", $"expected Code ResourceNotFound, got {Py.Repr(slotError.Code)}");
            Ensure(slotError.Message == "Requested resource not found", $"expected AWS's exact message, got {Py.Repr(slotError.Message)}");
        }
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
