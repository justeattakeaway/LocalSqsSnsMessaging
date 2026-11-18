// Translated from behavioral/dynamodb/check_gsi_creating_blocks_delete_table.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-creating-blocks-delete-table</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiCreatingBlocksDeleteTable
{
    /// <summary>
    /// DeleteTable while a GSI added with UpdateTable is still CREATING raises ResourceInUseException with the exact
    /// message and leaves the table; once the GSI is ACTIVE, DeleteTable removes the table.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-creating-blocks-delete-table")]
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
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(t), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(t, Item(("pk", S("a")), ("gsi_pk", S("grp"))));

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

        // Immediately (no waiting) confirm the GSI is still building, then try to
        // delete the whole table right on top of that in-progress build.
        var described = (await ddb.DescribeTableAsync(t)).Table;
        var gsiStatus = described.GlobalSecondaryIndexes.First(g => g.IndexName == "late-gsi").IndexStatus?.Value;
        Ensure(gsiStatus == "CREATING", $"expected the newly-requested GSI to still be CREATING, got {gsiStatus}");

        var (code, message) = await ErrorDetail(() => ddb.DeleteTableAsync(t));
        Ensure(code == "ResourceInUseException", $"expected ResourceInUseException, got {code}");
        const string inUseMessage =
            "Attempt to change a resource which is still in use: Cannot delete table while "
            + "indexes are being created, updated, or deleted.";
        Ensure(message == inUseMessage, $"expected message {Py.Repr(inUseMessage)}, got {Py.Repr(message)}");

        // Table must still exist, untouched, after the rejected delete attempt.
        var stillActive = (await ddb.DescribeTableAsync(t)).Table;
        Ensure(stillActive.TableName == t, Py.Repr(stillActive.TableName));

        // Once the GSI settles, DeleteTable must succeed normally. Real AWS GSI
        // backfill commonly takes several minutes and can exceed thirteen even for a
        // single-item table, so the wait takes most of the suite's 1800s per-check
        // budget and leaves the rest for the delete that follows.
        await WaitTableActive(ddb, t, timeoutSeconds: 1500);
        await ddb.DeleteTableAsync(t);
        await WaitTableDeleted(ddb, t);
        var (goneCode, goneMessage) = await ErrorDetail(() => ddb.DescribeTableAsync(t));
        Ensure(goneCode == "ResourceNotFoundException", goneCode ?? "None");
        var notFoundMessage = $"Requested resource not found: Table: {t} not found";
        Ensure(goneMessage == notFoundMessage, $"expected message {Py.Repr(notFoundMessage)}, got {Py.Repr(goneMessage)}");
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

    /// <summary>A cleanup running <c>undo()</c> that returns only on the resource's own not-found codes.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>The (code, message) a rejected call returned; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> ErrorDetail<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a rejection, the call succeeded: {Py.Repr(result)}");
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

    private static Task<string?> WaitTableDeleted(IAmazonDynamoDB ddb, string name, double timeoutSeconds = 90) =>
        PollUntil(
            async () =>
            {
                var (resp, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(name));
                if (error is not null)
                {
                    if (error.Code == "ResourceNotFoundException")
                    {
                        return "GONE";
                    }

                    throw error.Exception;
                }

                return resp!.Table.TableStatus?.Value;
            },
            s => s == "GONE",
            timeoutSeconds: timeoutSeconds,
            intervalSeconds: 1.0,
            description: $"table {name} deleted");
}
