// Translated from behavioral/dynamodb/check_batch_execute_statement_consistent_read_is_billed_per_member.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-execute-statement-consistent-read-is-billed-per-member</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchExecuteStatementConsistentReadIsBilledPerMember
{
    private const string Check = "batch-execute-statement-consistent-read-is-billed-per-member";

    /// <summary>
    /// BatchExecuteStatement bills each statement's own ConsistentRead: an eventual
    /// 1-item SELECT costs half a consistent one, an explicit false equals omitting the
    /// member, and a batch mixing both sums each statement's own cost.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-execute-statement-consistent-read-is-billed-per-member")]
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
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"INSERT INTO \"{t}\" VALUE {{'pk':'a'}}" });
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"INSERT INTO \"{t}\" VALUE {{'pk':'b'}}" });

        Task<BatchExecuteStatementResponse> Select(string pk, bool? consistentRead)
        {
            var stmt = new BatchStatementRequest { Statement = $"SELECT * FROM \"{t}\" WHERE pk='{pk}' ".Trim() };
            if (consistentRead is not null)
            {
                stmt.ConsistentRead = consistentRead;
            }

            return ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest { Statements = [stmt], ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL });
        }

        var omitted = await Select("a", null);
        var omittedCc = omitted.ConsumedCapacity;
        Ensure(omittedCc is { Count: 1 }, $"expected one per-table ConsumedCapacity entry, got {Py.Repr(omittedCc?.Select(c => c.CapacityUnits))}");
        var omittedCu = omittedCc![0].CapacityUnits;
        Ensure(omittedCu == 0.5, $"an eventual 1-item SELECT must bill 0.5, got {Py.Repr(omittedCu)}");

        var eventual = (await Select("a", false)).ConsumedCapacity[0].CapacityUnits;
        Ensure(eventual == 0.5, $"ConsistentRead=False must bill 0.5, got {Py.Repr(eventual)}");
        Ensure(eventual == omittedCu, $"ConsistentRead=False must equal omitting the member, got {Py.Repr(eventual)} vs {Py.Repr(omittedCu)}");

        var consistent = (await Select("a", true)).ConsumedCapacity[0].CapacityUnits;
        Ensure(consistent == 1.0, $"ConsistentRead=True must bill 1.0, got {Py.Repr(consistent)}");
        Ensure(
            consistent == omittedCu * 2,
            $"a consistent read must cost exactly twice an eventual one, got {Py.Repr(consistent)} vs {Py.Repr(omittedCu)}");

        var mixed = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements =
            [
                new BatchStatementRequest { Statement = $"SELECT * FROM \"{t}\" WHERE pk='a' ", ConsistentRead = true },
                new BatchStatementRequest { Statement = $"SELECT * FROM \"{t}\" WHERE pk='b' ", ConsistentRead = false },
            ],
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var mixedResponses = mixed.Responses;
        Ensure(mixedResponses.Count == 2, $"expected 2 per-statement responses, got {mixedResponses.Count}");
        Ensure(Py.Equal(mixedResponses[0].Item, Item(("pk", S("a")))), $"expected responses[0] to carry item a, got {Py.Repr(mixedResponses[0].Item)}");
        Ensure(Py.Equal(mixedResponses[1].Item, Item(("pk", S("b")))), $"expected responses[1] to carry item b, got {Py.Repr(mixedResponses[1].Item)}");
        var mixedCc = mixed.ConsumedCapacity;
        Ensure(
            mixedCc is { Count: 1 },
            $"both statements read the same table, expected one entry, got {Py.Repr(mixedCc?.Select(c => c.CapacityUnits))}");
        var mixedCu = mixedCc![0].CapacityUnits;
        Ensure(mixedCu == 1.5, $"a consistent member plus an eventual member must sum to 1.5, got {Py.Repr(mixedCu)}");
    }
}
