// Translated from behavioral/dynamodb/check_gsi_add_and_remove_online.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-add-and-remove-online</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiAddAndRemoveOnline
{
    /// <summary>
    /// A GSI added to an ACTIVE table with UpdateTable becomes ACTIVE with its attribute definition and answers a
    /// Query; deleting it removes the index and its attribute definition, and a Query on it then raises
    /// ValidationException with the exact message.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-add-and-remove-online")]
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
            Tags = [new Tag { Key = "sim2real:managed", Value = "true" }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(t, Item(("pk", S("a")), ("gsi_pk", S("grp"))));
        await ddb.PutItemAsync(t, Item(("pk", S("b")))); // sparse: no gsi_pk

        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = t,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("gsi_pk", ScalarAttributeType.S)],
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Create = new CreateGlobalSecondaryIndexAction
                    {
                        IndexName = "late-gsi",
                        KeySchema = [new KeySchemaElement("gsi_pk", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                },
            ],
        });

        var describedCreating = (await ddb.DescribeTableAsync(t)).Table;
        var gsis = (describedCreating.GlobalSecondaryIndexes ?? []).ToDictionary(g => g.IndexName, StringComparer.Ordinal);
        Ensure(gsis.ContainsKey("late-gsi"), Py.Repr(gsis.Keys.ToList()));
        Ensure(gsis["late-gsi"].IndexStatus?.Value is "CREATING" or "ACTIVE", Py.Repr(gsis["late-gsi"].IndexStatus));

        var activeTable = await WaitTableActive(ddb, t, timeoutSeconds: 1800);
        var activeGsi = activeTable.GlobalSecondaryIndexes.First(g => g.IndexName == "late-gsi");
        Ensure(activeGsi.IndexStatus == IndexStatus.ACTIVE, Py.Repr(activeGsi.IndexStatus));

        var activeAttrNames = activeTable.AttributeDefinitions.Select(a => a.AttributeName).ToHashSet(StringComparer.Ordinal);
        Ensure(
            activeAttrNames.SetEquals(["pk", "gsi_pk"]),
            $"expected AttributeDefinitions {{'pk', 'gsi_pk'}} with the index ACTIVE, got {Py.Repr(activeAttrNames.ToList())}");

        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            IndexName = "late-gsi",
            KeyConditionExpression = "gsi_pk = :g",
            ExpressionAttributeValues = Item((":g", S("grp"))),
        });
        Ensure(resp.Count == 1, Py.Repr(resp.Items));
        Ensure(Py.Equal(resp.Items[0]["pk"], S("a")), Py.Repr(resp.Items));

        // Now delete the GSI and confirm it fully disappears from DescribeTable.
        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = t,
            GlobalSecondaryIndexUpdates = [new GlobalSecondaryIndexUpdate { Delete = new DeleteGlobalSecondaryIndexAction { IndexName = "late-gsi" } }],
        });
        var deletedTable = await PollUntil(
            async () => (await ddb.DescribeTableAsync(t)).Table,
            tbl => !(tbl.GlobalSecondaryIndexes ?? []).Any(g => g.IndexName == "late-gsi"),
            timeoutSeconds: 150,
            intervalSeconds: 2.0,
            description: "late-gsi removed from DescribeTable");

        // Removing the index also removes the attribute definition it alone required: a
        // definition with no remaining key-schema reference does not linger.
        var deletedAttrNames = deletedTable.AttributeDefinitions.Select(a => a.AttributeName).ToHashSet(StringComparer.Ordinal);
        Ensure(
            deletedAttrNames.SetEquals(["pk"]),
            $"expected AttributeDefinitions {{'pk'}} once late-gsi is gone, got {Py.Repr(deletedAttrNames.ToList())}");

        // Gone must also mean unusable: a query against the now-deleted index must be refused,
        // not silently served (e.g. by falling back to a base-table scan).
        var (code, message) = await ErrorCodeAndMessage(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            IndexName = "late-gsi",
            KeyConditionExpression = "gsi_pk = :g",
            ExpressionAttributeValues = Item((":g", S("grp"))),
        }));
        Ensure(code == "ValidationException", $"expected ValidationException querying deleted index late-gsi, got {Py.Repr(code)}");
        const string expectedMessage = "The table does not have the specified index: late-gsi";
        Ensure(message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutSeconds = 60, double intervalSeconds = 1.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        T? last = default;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"{description} not met within {timeoutSeconds}s. Last result: {Py.Repr(last)}");
    }

    private static async Task<(string? Code, string? Message)> ErrorCodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected the call to fail, but it succeeded");
        }

        return (error.Code, error.Message);
    }

    private static Task<TableDescription> WaitTableActive(IAmazonDynamoDB ddb, string name, double timeoutSeconds = 90) =>
        PollUntil(
            async () => (await ddb.DescribeTableAsync(name)).Table,
            t => t.TableStatus == TableStatus.ACTIVE && (t.GlobalSecondaryIndexes ?? []).All(idx => (idx.IndexStatus?.Value ?? "ACTIVE") == "ACTIVE"),
            timeoutSeconds: timeoutSeconds,
            intervalSeconds: 1.0,
            description: $"table {name} ACTIVE");
}
