// Translated from behavioral/dynamodb/check_execute_statement_update_condition_failure_returns_old_item_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-update-condition-failure-returns-old-item-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementUpdateConditionFailureReturnsOldItemRejected
{
    private const string CheckId = "execute-statement-update-condition-failure-returns-old-item-rejected";
    private const string ErrConditionalCheckFailed = "ConditionalCheckFailedException";

    /// <summary>
    /// A PartiQL ExecuteStatement UPDATE ... WHERE whose condition does not match raises
    /// ConditionalCheckFailedException with the exact message, and with
    /// ReturnValuesOnConditionCheckFailure=ALL_OLD the exception carries the item's current value.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_ExecuteStatement.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-update-condition-failure-returns-old-item-rejected")]
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
            Tags = [new Tag { Key = "sim2real-behav", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("pk", S("cond")));
        await ddb.PutItemAsync(t, Item(("pk", S("cond")), ("val", S("original"))));

        // Positive control: an ExecuteStatement UPDATE whose WHERE condition matches the current
        // value succeeds.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{t}\" SET val = ? WHERE pk = ? AND val = ?",
            Parameters = [S("control"), S("cond"), S("original")],
        });
        var controlled = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(controlled.GetValueOrDefault("val"), S("control")), $"positive control update did not take effect, got {Py.Repr(controlled)}");

        // Break exactly one thing: the WHERE condition checks the now-STALE value 'original'; the
        // current value is 'control'.
        var err = await FidelityContext.CaptureError(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{t}\" SET val = ? WHERE pk = ? AND val = ?",
            Parameters = [S("clobbered"), S("cond"), S("original")],
            ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
        }));
        Ensure(err is not null, "an ExecuteStatement UPDATE with a stale WHERE condition must be rejected");
        Ensure(err.Code == ErrConditionalCheckFailed, $"expected {ErrConditionalCheckFailed}, got {Py.Repr(err.Code)}");
        Ensure(err.Message == "The conditional request failed", $"expected AWS's exact ConditionalCheckFailedException message, got {Py.Repr(err.Message)}");
        var surfaced = (err.Exception as ConditionalCheckFailedException)?.Item;
        Ensure(surfaced is not null, $"ReturnValuesOnConditionCheckFailure=ALL_OLD must surface the current item on rejection, got {err}");
        Ensure(
            Py.Equal(surfaced.GetValueOrDefault("val"), S("control")),
            $"the surfaced item must carry the CURRENT value 'control', not the stale expectation, got {Py.Repr(surfaced)}");

        // Effect absent: an independent read (GetItem, not ExecuteStatement's own exception) shows
        // the rejected update did not touch the item.
        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(unchanged.GetValueOrDefault("val"), S("control")), $"a rejected conditional UPDATE must not modify the item, got {Py.Repr(unchanged)}");

        // Restore: the identical statement, with the condition matching the CURRENT value, now succeeds.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{t}\" SET val = ? WHERE pk = ? AND val = ?",
            Parameters = [S("restored"), S("cond"), S("control")],
            ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
        });
        var restored = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(restored.GetValueOrDefault("val"), S("restored")),
            $"an ExecuteStatement UPDATE whose condition matches the current value must succeed, got {Py.Repr(restored)}");
    }
}
