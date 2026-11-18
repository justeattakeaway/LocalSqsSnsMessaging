// Translated from behavioral/dynamodb/check_batch_execute_statement_returning_update_vs_delete_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-execute-statement-returning-update-vs-delete-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchExecuteStatementReturningUpdateVsDeleteRejected
{
    private const string Check = "batch-execute-statement-returning-update-vs-delete-rejected";
    private const string ExpectedDeleteMessage =
        "Invalid returning clause: RETURNING ALL NEW *. " +
        "Only RETURNING ALL OLD * is allowed in DELETE statements.";

    /// <summary>
    /// BatchExecuteStatement reports an UPDATE member's RETURNING ALL NEW * / MODIFIED NEW * as a
    /// per-slot Item carrying that image, and a DELETE member's unsupported RETURNING ALL NEW * as a
    /// per-slot Error instead of aborting the batch; only the Item-carrying slots echo TableName.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchExecuteStatement.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-execute-statement-returning-update-vs-delete-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlbret-");
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
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 60);

        var item1 = Item(("pk", S("p1")), ("sk", S("s1")), ("name", S("Ada")), ("qty", N("3")));
        var item2 = Item(("pk", S("p2")), ("sk", S("s2")), ("name", S("Bea")), ("qty", N("5")));
        var item3 = Item(("pk", S("p3")), ("sk", S("s3")), ("name", S("Cid")), ("qty", N("7")));
        foreach (var item in new[] { item1, item2, item3 })
        {
            await ddb.PutItemAsync(table, item);
        }

        var resp = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements =
            [
                new BatchStatementRequest { Statement = $"UPDATE \"{table}\" SET qty = 10 WHERE pk = 'p1' AND sk = 's1' RETURNING ALL NEW *" },
                new BatchStatementRequest { Statement = $"UPDATE \"{table}\" SET name = 'Zed' WHERE pk = 'p2' AND sk = 's2' RETURNING MODIFIED NEW *" },
                new BatchStatementRequest { Statement = $"DELETE FROM \"{table}\" WHERE pk = 'p3' AND sk = 's3' RETURNING ALL NEW *" },
            ],
        });
        var responses = resp.Responses;
        Ensure(responses.Count == 3, $"expected 3 per-statement responses, got {responses.Count}: {Py.Repr(responses.Select(Slot))}");

        var (slot1, slot2, slot3) = (responses[0], responses[1], responses[2]);

        // Slot 1: UPDATE ... RETURNING ALL NEW * succeeds with the full post-update image, and the
        // slot echoes TableName.
        Ensure(slot1.Error is null, $"expected the ALL NEW * UPDATE slot to succeed, got {Py.Repr(Slot(slot1))}");
        Ensure(slot1.TableName == table, $"expected the successful UPDATE slot to echo TableName={Py.Repr(table)}, got {Py.Repr(Slot(slot1))}");
        var want1 = new Dictionary<string, AttributeValue>(item1, StringComparer.Ordinal) { ["qty"] = N("10") };
        Ensure(
            Py.Equal(slot1.Item, want1),
            $"expected RETURNING ALL NEW * to carry the full post-update item {Py.Repr(want1)}, got {Py.Repr(slot1.Item)}");

        // Slot 2: UPDATE ... RETURNING MODIFIED NEW * succeeds with only the modified attribute, and
        // the slot echoes TableName.
        Ensure(slot2.Error is null, $"expected the MODIFIED NEW * UPDATE slot to succeed, got {Py.Repr(Slot(slot2))}");
        Ensure(slot2.TableName == table, $"expected the successful UPDATE slot to echo TableName={Py.Repr(table)}, got {Py.Repr(Slot(slot2))}");
        var want2 = Item(("name", S("Zed")));
        Ensure(
            Py.Equal(slot2.Item, want2),
            $"expected RETURNING MODIFIED NEW * to carry only the set attribute {Py.Repr(want2)}, got {Py.Repr(slot2.Item)}");

        // Slot 3: DELETE ... RETURNING ALL NEW * is an unsupported variant, reported as a per-slot
        // Error (not a thrown exception aborting the batch), and the slot carries no TableName.
        Ensure(slot3.Item is null, $"expected the rejected DELETE slot to carry no Item, got {Py.Repr(Slot(slot3))}");
        Ensure(
            slot3.TableName is null,
            "expected the rejected DELETE slot to carry no TableName (only a successful slot echoes " +
            $"it), got {Py.Repr(Slot(slot3))}");
        var error = slot3.Error;
        Ensure(error is not null, $"expected the rejected DELETE slot to carry a per-slot Error, got {Py.Repr(Slot(slot3))}");
        Ensure(
            error.Code?.Value == "ValidationError",
            $"expected the rejected DELETE slot Error.Code to be ValidationError, got {Py.Repr(error.Code)}");
        Ensure(error.Message == ExpectedDeleteMessage, $"expected message {Py.Repr(ExpectedDeleteMessage)}, got {Py.Repr(error.Message)}");

        // The batch's mutations landed exactly where the per-slot outcomes say they did: p1 and p2
        // updated, p3 untouched by its rejected DELETE.
        Ensure(
            Py.Equal(await Stored(ddb, table, "p1", "s1"), want1),
            $"p1 must have been updated to {Py.Repr(want1)}, got {Py.Repr(await Stored(ddb, table, "p1", "s1"))}");
        var stored2 = await Stored(ddb, table, "p2", "s2");
        var wantStored2 = new Dictionary<string, AttributeValue>(item2, StringComparer.Ordinal) { ["name"] = S("Zed") };
        Ensure(Py.Equal(stored2, wantStored2), $"p2 must have been updated to {Py.Repr(wantStored2)}, got {Py.Repr(stored2)}");
        Ensure(
            Py.Equal(await Stored(ddb, table, "p3", "s3"), item3),
            $"p3 must be untouched by its rejected DELETE, got {Py.Repr(await Stored(ddb, table, "p3", "s3"))}");
    }

    private static async Task<Dictionary<string, AttributeValue>?> Stored(IAmazonDynamoDB ddb, string table, string pk, string sk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk)), ("sk", S(sk))), ConsistentRead = true })).Item;

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
