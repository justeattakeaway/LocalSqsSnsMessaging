// Translated from behavioral/dynamodb/check_execute_statement_rejects_foreign_next_token.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-rejects-foreign-next-token</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementRejectsForeignNextToken
{
    private const string CheckId = "execute-statement-rejects-foreign-next-token";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// An ExecuteStatement NextToken is bound to its issuing statement: it resumes that statement,
    /// but a different statement on the same table rejects it ValidationException with AWS's message.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-rejects-foreign-next-token")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqltoken-");
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
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("sk", S("s1")), ("status", S("active")), ("createdAt", S("2024-01-01"))));
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("sk", S("s2")), ("status", S("active")), ("createdAt", S("2024-01-02"))));

        var baseStatement = $"SELECT * FROM \"{table}\" WHERE pk = '{pk}'";
        var page1 = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = baseStatement, Limit = 1 });
        var token = page1.NextToken;
        Ensure(!string.IsNullOrEmpty(token), $"the first page of a 2-item partition under Limit=1 must carry a NextToken, got {Py.Repr(page1.Items)}");
        Ensure(page1.Items.Count == 1, $"expected exactly 1 item on page 1, got {Py.Repr(page1.Items)}");

        // Positive control: the SAME statement, replayed with its own token, correctly resumes to
        // the second item -- so the mechanism works at all, and the rejection below is specific to
        // issuing the token to a DIFFERENT statement.
        var page2 = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = baseStatement, Limit = 1, NextToken = token });
        Ensure(
            (page2.Items ?? []).Select(i => i["sk"].S).SequenceEqual(["s2"]),
            $"resuming the issuing statement with its own token must return the second item, got {Py.Repr(page2.Items)}");

        // Break exactly one thing: hand the SAME token to a DIFFERENT statement.
        var foreignStatement = $"SELECT * FROM \"{table}\".\"status-index\" WHERE status = 'active'";
        var (accepted, rejected) = await FidelityContext.Capture(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = foreignStatement,
            Limit = 1,
            NextToken = token,
        }));

        Ensure(
            accepted is null,
            $"a NextToken issued by one statement must be rejected by a different statement, but it was accepted and returned {Py.Repr(accepted?.Items)}");
        var code = rejected!.Code;
        var message = rejected.Message;
        Ensure(code == ErrValidation, $"expected {Py.Repr(ErrValidation)} for a foreign NextToken, got {Py.Repr(code)} ({Py.Repr(message)})");
        const string expected = "NextToken does not match request";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
    }
}
