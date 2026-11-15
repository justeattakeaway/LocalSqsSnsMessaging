// Translated from behavioral/dynamodb/check_batch_execute_statement_set_add_no_error_slot.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-execute-statement-set-add-no-error-slot</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchExecuteStatementSetAddNoErrorSlot
{
    private const string Check = "batch-execute-statement-set-add-no-error-slot";

    /// <summary>
    /// A set_add UPDATE sent as a BatchExecuteStatement member creates the string set on the
    /// item, and its slot in Responses carries no Error.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchExecuteStatement.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-execute-statement-set-add-no-error-slot")]
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
        await WaitTableActive(ctx, ddb, t);

        var key = Item(("pk", S("s1")));
        await ddb.PutItemAsync(t, Item(("pk", S("s1"))));

        var resp = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements =
            [
                new BatchStatementRequest
                {
                    Statement = $"UPDATE \"{t}\" SET tags = set_add(tags, <<'w'>>) WHERE pk = ?",
                    Parameters = [key["pk"]],
                },
            ],
        });
        var responses = resp.Responses;
        Ensure(responses.Count == 1, $"expected one per-statement response, got {responses.Count}: {Py.Repr(responses.Select(r => r.Error?.Message))}");
        Ensure(
            responses[0].Error is null,
            $"a successful set_add batch member must carry no Error, got {Py.Repr((responses[0].Error?.Code, responses[0].Error?.Message))}");

        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(item.GetValueOrDefault("tags"), SS("w")),
            $"the batch member's set_add must store {{'SS': ['w']}}, got {Py.Repr(item.GetValueOrDefault("tags"))}");
    }

    /// <summary>Poll DescribeTable until TableStatus is ACTIVE, bounded by the run's remaining time.</summary>
    private static async Task<TableDescription> WaitTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string tableName)
    {
        var left = ctx.TimeLeftSeconds;
        var budgetS = Math.Max(5.0, Math.Min(90.0, left - 10.0));
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed.TotalSeconds < budgetS)
        {
            last = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table;
            if (last.TableStatus == TableStatus.ACTIVE)
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"table {tableName} never went ACTIVE in {budgetS.ToString("0.0", CultureInfo.InvariantCulture)}s: {Py.Repr(last?.TableStatus)}");
    }
}
