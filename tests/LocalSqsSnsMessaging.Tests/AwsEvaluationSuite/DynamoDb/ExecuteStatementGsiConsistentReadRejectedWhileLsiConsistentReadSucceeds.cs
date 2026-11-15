// Translated from behavioral/dynamodb/check_execute_statement_gsi_consistent_read_rejected_while_lsi_consistent_read_succeeds.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-gsi-consistent-read-rejected-while-lsi-consistent-read-succeeds</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementGsiConsistentReadRejectedWhileLsiConsistentReadSucceeds
{
    private const string CheckId = "execute-statement-gsi-consistent-read-rejected-while-lsi-consistent-read-succeeds";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// A PartiQL SELECT with ConsistentRead=True reads its own write through an LSI but is
    /// rejected with ValidationException and no data on a GSI that is live for eventual reads.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-gsi-consistent-read-rejected-while-lsi-consistent-read-succeeds")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlconsist-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("status", ScalarAttributeType.S),
                new AttributeDefinition("createdAt", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "status-index",
                    KeySchema = [new KeySchemaElement("status", KeyType.HASH), new KeySchemaElement("createdAt", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "alternate-index",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var clock = FidelityTime.StartNew();
        Dictionary<string, string?> gsiStatus = [];
        while (clock.Elapsed < TimeSpan.FromSeconds(300))
        {
            var described = (await ddb.DescribeTableAsync(table)).Table;
            gsiStatus = (described.GlobalSecondaryIndexes ?? []).ToDictionary(i => i.IndexName, i => i.IndexStatus?.Value, StringComparer.Ordinal);
            if (gsiStatus.GetValueOrDefault("status-index") == "ACTIVE")
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(gsiStatus.GetValueOrDefault("status-index") == "ACTIVE", $"status-index never became ACTIVE: {Py.Repr(gsiStatus)}");

        const string pk = "P1";
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("sk", S("S1")), ("status", S("active")), ("createdAt", S("2024-01-01"))));

        // Positive control 1: the LSI shares the base table's partition and is strongly consistent,
        // so a ConsistentRead=True SELECT through it must see the write it immediately followed.
        var lsiResp = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\".\"alternate-index\" WHERE pk = '{pk}'",
            ConsistentRead = true,
        });
        var lsiItems = lsiResp.Items ?? [];
        Ensure(
            lsiItems.Select(i => i["pk"].S).SequenceEqual([pk]),
            $"the strongly consistent LSI SELECT did not see the write it followed: {Py.Repr(lsiItems)}");

        // The same ConsistentRead=True flag, against the global index.
        var (accepted, rejected) = await FidelityContext.Capture(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\".\"status-index\" WHERE status = 'active'",
            ConsistentRead = true,
        }));

        Ensure(
            accepted is null,
            $"ConsistentRead=True on a global secondary index via ExecuteStatement must be rejected, but the service accepted it and returned {Py.Repr(accepted?.Items)}");
        var code = rejected!.Code;
        Ensure(
            code == ErrValidation,
            $"expected {Py.Repr(ErrValidation)} for ConsistentRead on a GSI via ExecuteStatement, got {Py.Repr(code)} ({Py.Repr(rejected.Message)})");
        const string expected = "Strongly consistent read is not supported on Global Secondary Indexes";
        var message = rejected.Message;
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
        // The rejection carries no Items key at all, not an empty page: an error response has no
        // Items member to carry (the .NET SDK raises an exception with no response object).

        // Positive control 2: the GSI is live for an eventually consistent read, so the rejection is
        // about the flag; polled because GSI propagation is eventually consistent.
        clock = FidelityTime.StartNew();
        List<Dictionary<string, AttributeValue>> gsiItems = [];
        while (clock.Elapsed < TimeSpan.FromSeconds(120))
        {
            gsiItems = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"SELECT * FROM \"{table}\".\"status-index\" WHERE status = 'active'",
            })).Items ?? [];
            if (gsiItems.Count > 0)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(
            gsiItems.Select(i => i["pk"].S).SequenceEqual([pk]),
            $"the GSI never became queryable through ExecuteStatement within 120s, so the rejection above would not be about consistency: {Py.Repr(gsiItems)}");
    }
}
