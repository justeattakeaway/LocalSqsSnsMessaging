// Translated from behavioral/dynamodb/check_batch_execute_statement_explicit_none_return_values_omits_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-execute-statement-explicit-none-return-values-omits-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchExecuteStatementExplicitNoneReturnValuesOmitsItem
{
    private const string Check = "batch-execute-statement-explicit-none-return-values-omits-item";

    /// <summary>
    /// BatchExecuteStatement reports a per-statement conditional-write failure with
    /// ReturnValuesOnConditionCheckFailure explicitly set to NONE as a slot Error that carries no
    /// Item and leaves the item unchanged, while a sibling statement in the same batch applies.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchExecuteStatement.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-execute-statement-explicit-none-return-values-omits-item")]
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
        var explicitNoneItem = Item(("pk", S("explicit-none")), ("val", S("current-explicit-none")));
        await ddb.PutItemAsync(t, okItem);
        await ddb.PutItemAsync(t, explicitNoneItem);

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
                    Parameters = [S("clobbered"), S("explicit-none"), S("WRONG-expected-value")],
                    ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.NONE,
                },
            ],
        });
        var responses = resp.Responses;
        Ensure(responses.Count == 2, $"expected 2 per-statement responses, got {responses.Count}: {Py.Repr(responses.Select(Slot))}");
        var (okSlot, explicitNoneSlot) = (responses[0], responses[1]);

        Ensure(okSlot.Error is null, $"expected the matching-condition UPDATE to succeed, got {Py.Repr(Slot(okSlot))}");
        Ensure(
            Py.Equal(await Stored(ddb, t, "ok"), Item(("pk", S("ok")), ("val", S("updated-ok")))),
            $"the sibling's successful UPDATE must have applied, got {Py.Repr(await Stored(ddb, t, "ok"))}");

        var error = explicitNoneSlot.Error;
        Ensure(error is not null, $"expected the failing UPDATE slot to carry a per-slot Error, got {Py.Repr(Slot(explicitNoneSlot))}");
        Ensure(error.Code?.Value == "ConditionalCheckFailed", $"expected Error.Code 'ConditionalCheckFailed', got {Py.Repr(error.Code)}");
        Ensure(error.Message == "The conditional request failed", $"expected AWS's exact message, got {Py.Repr(error.Message)}");
        Ensure(
            error.Item is null,
            $"ReturnValuesOnConditionCheckFailure=NONE must omit Item from the slot Error, got {Py.Repr(error.Item)}");
        var unchanged = await Stored(ddb, t, "explicit-none");
        Ensure(Py.Equal(unchanged, explicitNoneItem), $"a rejected conditional UPDATE in the batch must not modify the item, got {Py.Repr(unchanged)}");
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
