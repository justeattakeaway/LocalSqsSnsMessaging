// Translated from behavioral/dynamodb/check_batch_execute_statement_select_absent_key_slot_carries_neither_item_nor_error.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-execute-statement-select-absent-key-slot-carries-neither-item-nor-error</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchExecuteStatementSelectAbsentKeySlotCarriesNeitherItemNorError
{
    private const string Check = "batch-execute-statement-select-absent-key-slot-carries-neither-item-nor-error";

    /// <summary>
    /// BatchExecuteStatement answers each SELECT by its position: a miss carries no
    /// Item and no Error, flanking hits carry their exact stored items, and writing the
    /// missing key turns every slot in the same statement order into a hit.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-execute-statement-select-absent-key-slot-carries-neither-item-nor-error")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await Table(ctx, ddb);
        var itemA = Item(("pk", S("slot-a")), ("val", S("alpha")));
        var itemC = Item(("pk", S("slot-c")), ("val", S("gamma")));
        await ddb.PutItemAsync(t, itemA);
        await ddb.PutItemAsync(t, itemC);

        var resp = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements = [Select(t, "slot-a"), Select(t, "slot-b"), Select(t, "slot-c")],
        });
        var responses = resp.Responses.Select(Slot).ToList();
        Ensure(responses.Count == 3, $"expected 3 per-statement responses, got {responses.Count}: {Py.Repr(responses)}");
        var (slotA, slotB, slotC) = (responses[0], responses[1], responses[2]);

        Ensure(
            Py.Equal(slotA, Hit(t, itemA)),
            $"expected slot 0 to carry exactly table {Py.Repr(t)} and Item {Py.Repr(itemA)}, got {Py.Repr(slotA)}");
        Ensure(
            Py.Equal(slotC, Hit(t, itemC)),
            $"expected slot 2 to carry exactly table {Py.Repr(t)} and Item {Py.Repr(itemC)}, got {Py.Repr(slotC)}");
        Ensure(
            Py.Equal(slotB, new Dictionary<string, object?> { ["TableName"] = t }),
            $"expected the miss at slot 1 to carry neither Item nor Error, got {Py.Repr(slotB)}");

        var missingItem = Item(("pk", S("slot-b")), ("val", S("beta")));
        await ddb.PutItemAsync(t, missingItem);

        var resp2 = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements = [Select(t, "slot-b"), Select(t, "slot-a"), Select(t, "slot-c")],
        });
        var responses2 = resp2.Responses.Select(Slot).ToList();
        Ensure(responses2.Count == 3, $"expected 3 per-statement responses, got {responses2.Count}: {Py.Repr(responses2)}");
        var (slotB2, slotA2, slotC2) = (responses2[0], responses2[1], responses2[2]);

        Ensure(
            Py.Equal(slotB2, Hit(t, missingItem)),
            $"expected the newly written key's slot to equal {Py.Repr(missingItem)}, got {Py.Repr(slotB2)}");
        Ensure(Py.Equal(slotA2, Hit(t, itemA)), $"expected the sibling hit to equal {Py.Repr(itemA)}, got {Py.Repr(slotA2)}");
        Ensure(Py.Equal(slotC2, Hit(t, itemC)), $"expected the sibling hit to equal {Py.Repr(itemC)}, got {Py.Repr(slotC2)}");
    }

    private static Dictionary<string, object?> Hit(string table, Dictionary<string, AttributeValue> item) =>
        new(StringComparer.Ordinal) { ["TableName"] = table, ["Item"] = item };

    private static BatchStatementRequest Select(string table, string pk) => new()
    {
        Statement = $"SELECT * FROM \"{table}\" WHERE pk = ?",
        Parameters = [S(pk)],
        ConsistentRead = true,
    };

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
