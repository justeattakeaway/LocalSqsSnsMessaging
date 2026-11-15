// Translated from behavioral/dynamodb/check_execute_statement_index_select_paginated_token_rejected_on_other_index.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-index-select-paginated-token-rejected-on-other-index</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementIndexSelectPaginatedTokenRejectedOnOtherIndex
{
    private const string CheckId = "execute-statement-index-select-paginated-token-rejected-on-other-index";
    private static readonly (string Code, string Message) ForeignToken = ("ValidationException", "NextToken does not match request");

    /// <summary>
    /// A paginated PartiQL SELECT against one GSI resumes with its own NextToken, but the same
    /// token handed to a SELECT against a different GSI on the same table is a ValidationException.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-index-select-paginated-token-rejected-on-other-index")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlidxpg-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("status", ScalarAttributeType.S),
                new AttributeDefinition("category", ScalarAttributeType.S),
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
                new GlobalSecondaryIndex
                {
                    IndexName = "category-index",
                    KeySchema = [new KeySchemaElement("category", KeyType.HASH), new KeySchemaElement("createdAt", KeyType.RANGE)],
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
            if (gsiStatus.GetValueOrDefault("status-index") == "ACTIVE" && gsiStatus.GetValueOrDefault("category-index") == "ACTIVE")
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(
            gsiStatus.GetValueOrDefault("status-index") == "ACTIVE" && gsiStatus.GetValueOrDefault("category-index") == "ACTIVE",
            $"both indexes never became ACTIVE: {Py.Repr(gsiStatus)}");

        const string pk = "P1";
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("sk", S("s1")), ("status", S("active")), ("category", S("promo")), ("createdAt", S("2024-01-01"))));
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("sk", S("s2")), ("status", S("active")), ("category", S("promo")), ("createdAt", S("2024-01-02"))));

        var statusStatement = $"SELECT * FROM \"{table}\".\"status-index\" WHERE status = 'active'";

        // Both items are eventually consistent writes into the GSI; poll until the first page is
        // stable at 2 rows before pagination is graded, so Limit=1 is tested against a settled index.
        clock = FidelityTime.StartNew();
        List<Dictionary<string, AttributeValue>> full = [];
        while (clock.Elapsed < TimeSpan.FromSeconds(120))
        {
            full = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statusStatement })).Items ?? [];
            if (full.Count == 2)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(full.Select(i => i["sk"].S).SequenceEqual(["s1", "s2"]), $"status-index never settled to both items in order: {Py.Repr(full)}");

        var page1 = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statusStatement, Limit = 1 });
        var token = page1.NextToken;
        Ensure(!string.IsNullOrEmpty(token), $"the first page of a 2-item GSI partition under Limit=1 must carry a NextToken, got {Py.Repr(page1.Items)}");
        Ensure(page1.Items.Select(i => i["sk"].S).SequenceEqual(["s1"]), $"expected only s1 on page 1, got {Py.Repr(page1.Items)}");

        // Positive control: the SAME statement, replayed with its own token, resumes to the second item.
        var page2 = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statusStatement, Limit = 1, NextToken = token });
        Ensure(
            (page2.Items ?? []).Select(i => i["sk"].S).SequenceEqual(["s2"]),
            $"resuming the issuing index statement with its own token must return the remaining row, got {Py.Repr(page2.Items)}");

        // Break exactly one thing: hand the status-index token to a SELECT against the OTHER index.
        var categoryStatement = $"SELECT * FROM \"{table}\".\"category-index\" WHERE category = 'promo'";
        await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = categoryStatement, Limit = 1, NextToken = token }),
            ForeignToken.Code,
            ForeignToken.Message);
    }

    /// <summary>(code, message) of the ClientError fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected ExecuteStatement to be rejected, but it succeeded: {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == code, $"expected {Py.Repr(code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == message, $"unexpected message {Py.Repr(gotMessage)}");
    }
}
