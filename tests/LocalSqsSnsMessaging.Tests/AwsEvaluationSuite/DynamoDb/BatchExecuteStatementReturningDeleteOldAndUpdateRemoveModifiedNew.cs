// Translated from behavioral/dynamodb/check_batch_execute_statement_returning_delete_old_and_update_remove_modified_new.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-execute-statement-returning-delete-old-and-update-remove-modified-new</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchExecuteStatementReturningDeleteOldAndUpdateRemoveModifiedNew
{
    private const string Check = "batch-execute-statement-returning-delete-old-and-update-remove-modified-new";
    private static readonly (string Code, string Message) ExpectedInsertError = ("ValidationError", "RETURNING is not supported in INSERT");

    /// <summary>
    /// In one BatchExecuteStatement call, a DELETE member's RETURNING ALL OLD * carries the removed
    /// item, an UPDATE REMOVE member's RETURNING MODIFIED NEW * carries no Item but still echoes
    /// TableName, and an INSERT member with RETURNING is a per-slot Error, not a batch abort.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-execute-statement-returning-delete-old-and-update-remove-modified-new")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlbrdr-");
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

        var itemDel = Item(("pk", S("p1")), ("sk", S("s1")), ("name", S("Ada")), ("qty", N("3")));
        var itemUpd = Item(("pk", S("p2")), ("sk", S("s2")), ("qty", N("5")), ("note", S("n1")));
        foreach (var item in new[] { itemDel, itemUpd })
        {
            await ddb.PutItemAsync(table, item);
        }

        var resp = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements =
            [
                new BatchStatementRequest { Statement = $"DELETE FROM \"{table}\" WHERE pk = 'p1' AND sk = 's1' RETURNING ALL OLD *" },
                new BatchStatementRequest { Statement = $"UPDATE \"{table}\" REMOVE note WHERE pk = 'p2' AND sk = 's2' RETURNING MODIFIED NEW *" },
                new BatchStatementRequest { Statement = $"INSERT INTO \"{table}\" VALUE {{'pk':'p3','sk':'s3'}} RETURNING ALL OLD *" },
            ],
        });
        var responses = resp.Responses;
        Ensure(responses.Count == 3, $"expected 3 per-statement responses, got {responses.Count}: {Py.Repr(responses.Select(Slot))}");
        var (slotDelete, slotUpdate, slotInsert) = (responses[0], responses[1], responses[2]);

        // Slot 1: DELETE ... RETURNING ALL OLD * carries the whole removed item and echoes TableName.
        Ensure(slotDelete.Error is null, $"expected the DELETE slot to succeed, got {Py.Repr(Slot(slotDelete))}");
        Ensure(slotDelete.TableName == table, $"expected the DELETE slot to echo TableName={Py.Repr(table)}, got {Py.Repr(Slot(slotDelete))}");
        Ensure(
            Py.Equal(slotDelete.Item, itemDel),
            $"expected RETURNING ALL OLD * to carry the removed item {Py.Repr(itemDel)}, " +
            $"got {Py.Repr(slotDelete.Item)}");

        // Slot 2: UPDATE REMOVE ... RETURNING MODIFIED NEW * carries no Item, because the removed
        // attribute has no new value, but still echoes TableName since the statement succeeded.
        Ensure(slotUpdate.Error is null, $"expected the UPDATE slot to succeed, got {Py.Repr(Slot(slotUpdate))}");
        Ensure(slotUpdate.Item is null, $"expected the REMOVE slot's MODIFIED NEW * to carry no Item, got {Py.Repr(Slot(slotUpdate))}");
        Ensure(
            slotUpdate.TableName == table,
            $"expected the successful UPDATE slot to echo TableName={Py.Repr(table)} even with no Item, " +
            $"got {Py.Repr(Slot(slotUpdate))}");

        // Slot 3: an INSERT member with a RETURNING clause is rejected as a per-slot Error, not a
        // thrown exception that aborts the other two members, and carries no Item or TableName.
        Ensure(slotInsert.Item is null, $"expected the rejected INSERT slot to carry no Item, got {Py.Repr(Slot(slotInsert))}");
        Ensure(
            slotInsert.TableName is null,
            "expected the rejected INSERT slot to carry no TableName (only a successful slot " +
            $"echoes it), got {Py.Repr(Slot(slotInsert))}");
        var insertError = slotInsert.Error;
        Ensure(insertError is not null, $"expected the rejected INSERT slot to carry a per-slot Error, got {Py.Repr(Slot(slotInsert))}");
        Ensure(
            (insertError.Code?.Value, insertError.Message) == ExpectedInsertError,
            $"expected {Py.Repr(ExpectedInsertError)}, got " +
            $"{Py.Repr((insertError.Code?.Value, insertError.Message))}");

        // The batch's mutations landed exactly where the per-slot outcomes say they did: p1 is gone,
        // p2 lost its removed attribute and kept the rest, and p3 was never inserted.
        Ensure(await Stored(ddb, table, "p1", "s1") is null, $"p1 must have been deleted, got {Py.Repr(await Stored(ddb, table, "p1", "s1"))}");
        var wantUpd = itemUpd.Where(kv => kv.Key != "note").ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        Ensure(
            Py.Equal(await Stored(ddb, table, "p2", "s2"), wantUpd),
            $"p2 must have had note removed, leaving {Py.Repr(wantUpd)}, got {Py.Repr(await Stored(ddb, table, "p2", "s2"))}");
        Ensure(
            await Stored(ddb, table, "p3", "s3") is null,
            "p3 must never have been inserted by the rejected statement, " +
            $"got {Py.Repr(await Stored(ddb, table, "p3", "s3"))}");
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
