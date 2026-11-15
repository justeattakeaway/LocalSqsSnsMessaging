// Translated from behavioral/dynamodb/check_execute_transaction_condition_failure_cancels_and_bills_nothing.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-transaction-condition-failure-cancels-and-bills-nothing</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteTransactionConditionFailureCancelsAndBillsNothing
{
    private const string CheckId = "execute-transaction-condition-failure-cancels-and-bills-nothing";
    private const string ErrCanceled = "TransactionCanceledException";

    /// <summary>
    /// ReturnConsumedCapacity=INDEXES on an ExecuteTransaction whose UPDATE condition fails
    /// returns TransactionCanceledException with no ConsumedCapacity, and the item is unchanged;
    /// the same write with a true condition commits and is billed.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-transaction-condition-failure-cancels-and-bills-nothing")]
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("pk", S("et-cancel-1")));
        await ddb.PutItemAsync(t, Item(("pk", S("et-cancel-1")), ("v", N("0"))));

        // Positive control: the condition holds, the write commits and is billed.
        var updateStmt = $"UPDATE \"{t}\" SET v = ? WHERE pk = ? AND v = ?";
        var committed = await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements = [new ParameterizedStatement { Statement = updateStmt, Parameters = [N("1"), key["pk"], N("0")] }],
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        var cc = committed.ConsumedCapacity;
        Ensure(
            cc.Count == 1 && cc[0].TableName == t,
            $"a committed write transaction must report one ConsumedCapacity entry for the table, got {Py.Repr(cc.Select(Repr).ToList())}");
        Ensure(
            cc[0].CapacityUnits == 2.0 && cc[0].WriteCapacityUnits == 2.0,
            $"a committed 1-item transactional UPDATE must bill 2.0 write capacity units, got {Py.Repr(Repr(cc[0]))}");
        var afterCommit = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(afterCommit["v"], N("1")), $"the true condition must let the UPDATE commit, got {Py.Repr(afterCommit)}");

        // Break exactly one thing: the condition no longer matches the current value.
        // Sent raw, so the error response's body can be read for a ConsumedCapacity member.
        var body = new JsonObject
        {
            ["TransactStatements"] = new JsonArray(new JsonObject
            {
                ["Statement"] = updateStmt,
                ["Parameters"] = new JsonArray(
                    new JsonObject { ["N"] = "2" },
                    new JsonObject { ["S"] = "et-cancel-1" },
                    new JsonObject { ["N"] = "0" }),
            }),
            // boto3 fills in the idempotency token.
            ["ClientRequestToken"] = Guid.NewGuid().ToString(),
            ["ReturnConsumedCapacity"] = "INDEXES",
        };
        var response = await ctx.DynamoDbRaw("ExecuteTransaction", body.ToJsonString());

        Ensure((int)response.StatusCode >= 300, "a false condition must cancel the transaction");
        Ensure(response.ErrorCode == ErrCanceled, $"expected {ErrCanceled}, got {Py.Repr(response.ErrorCode)}");
        const string expectedMessage =
            "Transaction cancelled, please refer cancellation reasons for specific reasons "
            + "[ConditionalCheckFailed]";
        Ensure(response.ErrorMessage == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(response.ErrorMessage)}");
        Ensure(
            response.Json is not { ValueKind: JsonValueKind.Object } json || !json.TryGetProperty("ConsumedCapacity", out _),
            $"a canceled transaction must report no ConsumedCapacity, got {response.Body}");

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(unchanged["v"], N("1")), $"a canceled transaction must not have applied its UPDATE, got {Py.Repr(unchanged)}");
    }

    /// <summary>The ConsumedCapacity entry as boto3 hands it back: only the members present.</summary>
    private static Dictionary<string, object?> Repr(ConsumedCapacity c)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (c.TableName is not null)
        {
            dict["TableName"] = c.TableName;
        }

        if (c.CapacityUnits is { } units)
        {
            dict["CapacityUnits"] = units;
        }

        if (c.ReadCapacityUnits is { } read)
        {
            dict["ReadCapacityUnits"] = read;
        }

        if (c.WriteCapacityUnits is { } write)
        {
            dict["WriteCapacityUnits"] = write;
        }

        return dict;
    }
}
