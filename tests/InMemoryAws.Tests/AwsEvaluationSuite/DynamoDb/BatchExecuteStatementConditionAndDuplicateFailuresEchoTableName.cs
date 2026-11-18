// Translated from behavioral/dynamodb/check_batch_execute_statement_condition_and_duplicate_failures_echo_table_name.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-execute-statement-condition-and-duplicate-failures-echo-table-name</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchExecuteStatementConditionAndDuplicateFailuresEchoTableName
{
    private const string Check = "batch-execute-statement-condition-and-duplicate-failures-echo-table-name";

    /// <summary>
    /// A BatchExecuteStatement member that ran and failed echoes TableName: a conditional
    /// UPDATE failing its WHERE gives Error.Code 'ConditionalCheckFailed' and an INSERT of an
    /// existing key gives Error.Code 'DuplicateItem', both carrying the table's name.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchExecuteStatement.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-execute-statement-condition-and-duplicate-failures-echo-table-name")]
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
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(t, Item(("pk", S("condfail")), ("val", S("current"))));
        await ddb.PutItemAsync(t, Item(("pk", S("dup1")), ("val", S("original"))));
        await ddb.PutItemAsync(t, Item(("pk", S("ok")), ("val", S("original-ok"))));

        var resp = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements =
            [
                new BatchStatementRequest
                {
                    Statement = $"UPDATE \"{t}\" SET val = ? WHERE pk = ? AND val = ?",
                    Parameters = [S("clobbered"), S("condfail"), S("WRONG-expected-value")],
                },
                new BatchStatementRequest { Statement = $"INSERT INTO \"{t}\" VALUE {{'pk':'dup1','val':'second'}}" },
                new BatchStatementRequest
                {
                    Statement = $"UPDATE \"{t}\" SET val = ? WHERE pk = ? AND val = ?",
                    Parameters = [S("updated-ok"), S("ok"), S("original-ok")],
                },
            ],
        });
        var responses = resp.Responses;
        Ensure(responses.Count == 3, $"expected 3 per-statement responses, got {responses.Count}: {Py.Repr(responses.Select(Slot))}");
        var (condSlot, dupSlot, okSlot) = (responses[0], responses[1], responses[2]);

        var condError = condSlot.Error;
        Ensure(condError is not null, $"expected a per-slot Error for the failed condition, got {Py.Repr(Slot(condSlot))}");
        Ensure(condError.Code?.Value == "ConditionalCheckFailed", $"expected Code ConditionalCheckFailed, got {Py.Repr(condError.Code)}");
        Ensure(condError.Message == "The conditional request failed", $"expected AWS's exact message, got {Py.Repr(condError.Message)}");
        Ensure(condSlot.TableName == t, $"expected the failed slot to echo TableName {Py.Repr(t)}, got {Py.Repr(condSlot.TableName)}");
        Ensure(
            Py.Equal(await Stored(ddb, t, "condfail"), Item(("pk", S("condfail")), ("val", S("current")))),
            "a rejected conditional UPDATE in the batch must not modify the item");

        var dupError = dupSlot.Error;
        Ensure(dupError is not null, $"expected a per-slot Error for the duplicate INSERT, got {Py.Repr(Slot(dupSlot))}");
        Ensure(dupError.Code?.Value == "DuplicateItem", $"expected Code DuplicateItem, got {Py.Repr(dupError.Code)}");
        Ensure(dupError.Message == "Duplicate primary key exists in table", $"expected AWS's exact message, got {Py.Repr(dupError.Message)}");
        Ensure(dupSlot.TableName == t, $"expected the failed slot to echo TableName {Py.Repr(t)}, got {Py.Repr(dupSlot.TableName)}");
        Ensure(
            Py.Equal(await Stored(ddb, t, "dup1"), Item(("pk", S("dup1")), ("val", S("original")))),
            "a rejected duplicate INSERT in the batch must not alter the existing item");

        // Positive control: the well-formed sibling in the SAME batch call executed.
        Ensure(okSlot.Error is null, $"expected the matching-condition UPDATE to succeed, got {Py.Repr(Slot(okSlot))}");
        Ensure(
            Py.Equal(await Stored(ddb, t, "ok"), Item(("pk", S("ok")), ("val", S("updated-ok")))),
            "the sibling's successful UPDATE must have applied");
    }

    private static async Task<Dictionary<string, AttributeValue>?> Stored(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

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
