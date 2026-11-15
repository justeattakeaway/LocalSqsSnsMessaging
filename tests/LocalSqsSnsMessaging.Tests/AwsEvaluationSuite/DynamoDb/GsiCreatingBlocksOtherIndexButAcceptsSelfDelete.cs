// Translated from behavioral/dynamodb/check_gsi_creating_blocks_other_index_but_accepts_self_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-creating-blocks-other-index-but-accepts-self-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiCreatingBlocksOtherIndexButAcceptsSelfDelete
{
    private const string CheckId = "gsi-creating-blocks-other-index-but-accepts-self-delete";
    private const int NItems = 40;

    /// <summary>
    /// While a GSI is still being created, UpdateTable adding a second, different GSI is
    /// refused with LimitExceededException, but deleting the GSI that is being built is accepted
    /// and the index disappears.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbTransientStates, "a GSI added by UpdateTable is ACTIVE at once, never CREATING, so UpdateTable reports it ACTIVE and never refuses a second index build")]
    [DisplayName("dynamodb.gsi-creating-blocks-other-index-but-accepts-self-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-gsilc-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(DeleteTableWhenSettled(ddb, table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        for (var i = 0; i < NItems; i++)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(string.Create(CultureInfo.InvariantCulture, $"item-{i:D3}"))), ("g1", S("grp"))));
        }

        var scanned = (await ddb.ScanAsync(new ScanRequest { TableName = table, Select = Select.COUNT, ConsistentRead = true })).Count;
        Ensure(scanned == NItems, $"expected {NItems} items before adding the index, got {scanned}");

        var addResp = await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("g1", ScalarAttributeType.S)],
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Create = new CreateGlobalSecondaryIndexAction
                    {
                        IndexName = "idx-a",
                        KeySchema = [new KeySchemaElement("g1", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                },
            ],
        });
        var added = (addResp.TableDescription.GlobalSecondaryIndexes ?? []).ToDictionary(g => g.IndexName, StringComparer.Ordinal);
        Ensure(
            added.Count == 1 && added.ContainsKey("idx-a"),
            $"UpdateTable described indexes {Py.Repr(added.Keys.Order(StringComparer.Ordinal).ToList())}, expected only idx-a");
        Ensure(
            added["idx-a"].IndexStatus?.Value == "CREATING",
            $"a just-requested GSI must be CREATING, was {Py.Repr(added["idx-a"].IndexStatus?.Value)}");

        // Immediately: a second UpdateTable touching a DIFFERENT index while idx-a is still building.
        var err = await Error(
            () => ddb.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = table,
                AttributeDefinitions =
                [
                    new AttributeDefinition("pk", ScalarAttributeType.S),
                    new AttributeDefinition("g1", ScalarAttributeType.S),
                    new AttributeDefinition("g2", ScalarAttributeType.S),
                ],
                GlobalSecondaryIndexUpdates =
                [
                    new GlobalSecondaryIndexUpdate
                    {
                        Create = new CreateGlobalSecondaryIndexAction
                        {
                            IndexName = "idx-b",
                            KeySchema = [new KeySchemaElement("g2", KeyType.HASH)],
                            Projection = new Projection { ProjectionType = ProjectionType.ALL },
                        },
                    },
                ],
            }),
            "UpdateTable adding idx-b while idx-a is building");
        Ensure(err.Code == "LimitExceededException", $"expected LimitExceededException, got {Py.Repr(err.Code)} ({Py.Repr(err.Message)})");
        const string expectedMsg = "Subscriber limit exceeded: Only 1 online index can be created or deleted simultaneously per table";
        Ensure(err.Message == expectedMsg, $"expected message {Py.Repr(expectedMsg)}, got {Py.Repr(err.Message)}");
        var afterRefusal = (await ddb.DescribeTableAsync(table)).Table;
        var namesAfterRefusal = (afterRefusal.GlobalSecondaryIndexes ?? []).Select(g => g.IndexName).Order(StringComparer.Ordinal).ToList();
        Ensure(namesAfterRefusal.SequenceEqual(["idx-a"]), $"a refused add must leave the index set unchanged, got {Py.Repr(namesAfterRefusal)}");

        // Immediately: a second UpdateTable deleting the SAME index that is still building. Accepted.
        var deleteResp = await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            GlobalSecondaryIndexUpdates = [new GlobalSecondaryIndexUpdate { Delete = new DeleteGlobalSecondaryIndexAction { IndexName = "idx-a" } }],
        });
        var deleted = (deleteResp.TableDescription.GlobalSecondaryIndexes ?? []).ToDictionary(g => g.IndexName, StringComparer.Ordinal);
        Ensure(
            deleted.Count == 1 && deleted.ContainsKey("idx-a"),
            $"UpdateTable self-delete described indexes {Py.Repr(deleted.Keys.Order(StringComparer.Ordinal).ToList())}, expected only idx-a");
        Ensure(
            deleted["idx-a"].IndexStatus?.Value == "DELETING",
            $"a just-accepted self-delete must report idx-a DELETING, got {Py.Repr(deleted["idx-a"].IndexStatus?.Value)}");

        var clock = FidelityTime.StartNew();
        TableDescription? final = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(600))
        {
            var tbl = (await ddb.DescribeTableAsync(table)).Table;
            if ((tbl.GlobalSecondaryIndexes ?? []).Count == 0 && tbl.TableStatus == TableStatus.ACTIVE)
            {
                final = tbl;
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        if (final is null)
        {
            var lastSeen = (await ddb.DescribeTableAsync(table)).Table.GlobalSecondaryIndexes;
            throw Fail($"idx-a was never fully removed within 600s; last seen {Py.Repr(lastSeen?.Select(g => (g.IndexName, g.IndexStatus?.Value)).ToList())}");
        }

        var attrNames = final.AttributeDefinitions.Select(a => a.AttributeName).ToHashSet(StringComparer.Ordinal);
        Ensure(attrNames.SetEquals(["pk"]), $"removing idx-a must also drop the attribute definition it alone needed, got {Py.Repr(attrNames.ToList())}");

        var codeAfter = await Error(
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = "idx-a",
                KeyConditionExpression = "g1 = :g",
                ExpressionAttributeValues = Item((":g", S("grp"))),
            }),
            "Query against the now-deleted idx-a");
        Ensure(codeAfter.Code == "ValidationException", $"expected ValidationException querying deleted idx-a, got {Py.Repr(codeAfter.Code)}");
        const string expectedQueryMsg = "The table does not have the specified index: idx-a";
        Ensure(codeAfter.Message == expectedQueryMsg, $"expected message {Py.Repr(expectedQueryMsg)}, got {Py.Repr(codeAfter.Message)}");

        var scannedAfter = (await ddb.ScanAsync(new ScanRequest { TableName = table, Select = Select.COUNT, ConsistentRead = true })).Count;
        Ensure(scannedAfter == NItems, $"the base table lost items across the index lifecycle: expected {NItems}, got {scannedAfter}");
    }

    /// <summary>The Error dict a rejected call raises; an unexpected success raises instead.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn, string what)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it: {Py.Repr(result)}");
        }

        return error;
    }

    private static Func<Task> DeleteTableWhenSettled(IAmazonDynamoDB ddb, string table, double timeoutSeconds = 600) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(table));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed > TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };
}
