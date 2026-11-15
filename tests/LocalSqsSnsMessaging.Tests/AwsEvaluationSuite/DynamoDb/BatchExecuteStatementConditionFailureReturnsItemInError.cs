// Translated from behavioral/dynamodb/check_batch_execute_statement_condition_failure_returns_item_in_error.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-execute-statement-condition-failure-returns-item-in-error</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchExecuteStatementConditionFailureReturnsItemInError
{
    private const string Check = "batch-execute-statement-condition-failure-returns-item-in-error";

    /// <summary>
    /// BatchExecuteStatement reports a per-statement conditional-write failure as that slot's
    /// Error rather than aborting the batch, and with ReturnValuesOnConditionCheckFailure=ALL_OLD
    /// the Error carries the item's current value, absent without the opt-in.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchExecuteStatement.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-execute-statement-condition-failure-returns-item-in-error")]
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
            Tags = [new Tag { Key = "sim2real-behav", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var okItem = Item(("pk", S("ok")), ("val", S("original-ok")));
        var staleItem = Item(("pk", S("stale")), ("val", S("current-stale")));
        var silentItem = Item(("pk", S("silent")), ("val", S("current-silent")));
        await ddb.PutItemAsync(t, okItem);
        await ddb.PutItemAsync(t, staleItem);
        await ddb.PutItemAsync(t, silentItem);

        var resp = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements =
            [
                new BatchStatementRequest
                {
                    Statement = $"UPDATE \"{t}\" SET val = ? WHERE pk = ? AND val = ?",
                    Parameters = [S("updated-ok"), S("ok"), S("original-ok")],
                },
                new BatchStatementRequest
                {
                    Statement = $"UPDATE \"{t}\" SET val = ? WHERE pk = ? AND val = ?",
                    Parameters = [S("clobbered"), S("stale"), S("WRONG-expected-value")],
                    ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
                },
                new BatchStatementRequest
                {
                    Statement = $"UPDATE \"{t}\" SET val = ? WHERE pk = ? AND val = ?",
                    Parameters = [S("clobbered"), S("silent"), S("WRONG-expected-value")],
                },
            ],
        });
        var responses = resp.Responses;
        Ensure(responses.Count == 3, $"expected 3 per-statement responses, got {responses.Count}: {Py.Repr(responses.Select(Slot))}");
        var (okSlot, withOptInSlot, withoutOptInSlot) = (responses[0], responses[1], responses[2]);

        Ensure(okSlot.Error is null, $"expected the matching-condition UPDATE to succeed, got {Py.Repr(Slot(okSlot))}");
        Ensure(
            Py.Equal(await Stored(ddb, t, "ok"), Item(("pk", S("ok")), ("val", S("updated-ok")))),
            $"the sibling's successful UPDATE must have applied, got {Py.Repr(await Stored(ddb, t, "ok"))}");

        var error = withOptInSlot.Error;
        Ensure(error is not null, $"expected the failing UPDATE slot to carry a per-slot Error, got {Py.Repr(Slot(withOptInSlot))}");
        Ensure(error.Code?.Value == "ConditionalCheckFailed", $"expected Error.Code 'ConditionalCheckFailed', got {Py.Repr(error.Code)}");
        Ensure(error.Message == "The conditional request failed", $"expected AWS's exact message, got {Py.Repr(error.Message)}");
        Ensure(
            Py.Equal(error.Item, staleItem),
            "ReturnValuesOnConditionCheckFailure=ALL_OLD must surface the complete current item in " +
            $"Error.Item, got {Py.Repr(error.Item)}");
        var unchanged = await Stored(ddb, t, "stale");
        Ensure(Py.Equal(unchanged, staleItem), $"a rejected conditional UPDATE in the batch must not modify the item, got {Py.Repr(unchanged)}");

        var silentError = withoutOptInSlot.Error;
        Ensure(
            silentError is not null,
            $"expected the second failing UPDATE slot to carry a per-slot Error too, got {Py.Repr(Slot(withoutOptInSlot))}");
        Ensure(silentError.Code?.Value == "ConditionalCheckFailed", $"expected Error.Code 'ConditionalCheckFailed', got {Py.Repr(silentError.Code)}");
        Ensure(silentError.Item is null, $"without ReturnValuesOnConditionCheckFailure the Error must omit Item, got {Py.Repr(silentError.Item)}");
        var unchangedSilent = await Stored(ddb, t, "silent");
        Ensure(
            Py.Equal(unchangedSilent, silentItem),
            $"a rejected conditional UPDATE in the batch must not modify the item, got {Py.Repr(unchangedSilent)}");
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
