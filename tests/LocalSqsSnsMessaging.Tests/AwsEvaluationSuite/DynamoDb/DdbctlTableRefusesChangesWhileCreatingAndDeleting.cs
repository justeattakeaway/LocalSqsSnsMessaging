// Translated from behavioral/dynamodb/check_ddbctl_table_refuses_changes_while_creating_and_deleting.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddbctl-table-refuses-changes-while-creating-and-deleting</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbctlTableRefusesChangesWhileCreatingAndDeleting
{
    private const string Check = "ddbctl-table-refuses-changes-while-creating-and-deleting";

    /// <summary>The code <see cref="Outcome"/> reports for a call the target accepted.</summary>
    private const string Accepted = "<accepted>";

    /// <summary>Fresh tables tried before the check gives up on observing a window it needs.</summary>
    private const int Attempts = 5;

    /// <summary>Wall bound on all attempts; the runner's remaining time bounds it further.</summary>
    private const double DeadlineSeconds = 240.0;

    private static List<Tag> Tags() => [new Tag { Key = "sim2real", Value = Check }];

    /// <summary>
    /// A CREATING table refuses CreateBackup and UpdateContinuousBackups with TableNotFoundException and
    /// UpdateTable and DeleteTable with ResourceInUseException (being created); a DELETING table still
    /// describes as DELETING and refuses a second DeleteTable with ResourceInUseException (being deleted).
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbTransientStates, "CreateTable answers ACTIVE at once, never CREATING")]
    [DisplayName("dynamodb.ddbctl-table-refuses-changes-while-creating-and-deleting")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(Bound(ctx, DeadlineSeconds));
        var creating = new Dictionary<string, Func<string, Task>>(StringComparer.Ordinal)
        {
            ["CreateBackup"] = async t => OnSuccess(ctx, await ddb.CreateBackupAsync(new CreateBackupRequest { TableName = t, BackupName = $"{t}-early" })),
            ["UpdateContinuousBackups"] = t => ddb.UpdateContinuousBackupsAsync(new UpdateContinuousBackupsRequest
            {
                TableName = t,
                PointInTimeRecoverySpecification = new PointInTimeRecoverySpecification { PointInTimeRecoveryEnabled = true },
            }),
            ["UpdateTable"] = t => ddb.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = t,
                BillingMode = BillingMode.PROVISIONED,
                ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 1, WriteCapacityUnits = 1 },
            }),
            ["DeleteTable"] = t => ddb.DeleteTableAsync(t),
        };
        var creatingRefusal = new Dictionary<string, (string Code, string Message)>(StringComparer.Ordinal)
        {
            ["CreateBackup"] = ("TableNotFoundException", "Table not found: {t}"),
            ["UpdateContinuousBackups"] = ("TableNotFoundException", "Table not found: {t}"),
            ["UpdateTable"] = ("ResourceInUseException", "Attempt to change a resource which is still in use: Table is being created: {t}"),
            ["DeleteTable"] = ("ResourceInUseException", "Attempt to change a resource which is still in use: Table is being created: {t}"),
        };
        var pendingCreating = creating.Keys.ToList();
        var pendingDeleting = new List<string> { "DescribeTable", "DeleteTable" };
        var tried = new List<string>();
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if ((pendingCreating.Count == 0 && pendingDeleting.Count == 0) || clock.Elapsed >= deadline)
            {
                break;
            }

            var table = FidelityContext.UniqueName("sim2real-ddbctl-tsm-");
            var created = (await ddb.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                BillingMode = BillingMode.PAY_PER_REQUEST,
                Tags = Tags(),
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            })).TableDescription;
            ctx.OnCleanup(() => Drop(ddb, table));
            Ensure(
                created.TableStatus == TableStatus.CREATING,
                $"CreateTable answered TableStatus {Py.Repr(created.TableStatus?.Value)}, expected 'CREATING'");
            foreach (var op in pendingCreating.ToList())
            {
                if (await Status(ddb, table) != "CREATING")
                {
                    tried.Add($"{table}: left CREATING before {op}");
                    break;
                }

                var (code, message) = await Outcome(() => creating[op](table));
                var (wantCode, wantMessage) = creatingRefusal[op];
                wantMessage = wantMessage.Replace("{t}", table, StringComparison.Ordinal);
                if ((code, message) == (wantCode, wantMessage))
                {
                    pendingCreating.Remove(op);
                    continue;
                }

                var still = await Status(ddb, table);
                Ensure(
                    still != "CREATING",
                    $"{op} on a table that stayed CREATING answered {Py.Repr(code)} {Py.Repr(message)}, expected " +
                    $"{Py.Repr(wantCode)} {Py.Repr(wantMessage)}");
                tried.Add($"{table}: {op} answered {Py.Repr(code)} after the table left CREATING ({Py.Repr(still)})");
                break;
            }

            if (pendingDeleting.Count == 0 || await Status(ddb, table) is null or "DELETING")
            {
                continue;
            }

            await Active(ddb, table);
            var deleted = (await ddb.DeleteTableAsync(table)).TableDescription;
            Ensure(
                deleted.TableStatus == TableStatus.DELETING,
                $"DeleteTable on an ACTIVE table answered {Py.Repr(deleted.TableStatus?.Value)}, expected 'DELETING'");
            if (pendingDeleting.Contains("DescribeTable"))
            {
                var status = await Status(ddb, table);
                if (status is null)
                {
                    tried.Add($"{table}: gone before DescribeTable read it DELETING");
                    continue;
                }

                Ensure(status == "DELETING", $"DescribeTable on a deleted table reported {Py.Repr(status)}");
                pendingDeleting.Remove("DescribeTable");
            }

            var (code2, message2) = await Outcome(() => ddb.DeleteTableAsync(table));
            var want = ("ResourceInUseException", $"Attempt to change a resource which is still in use: Table is being deleted: {table}");
            if ((code2, message2) == want)
            {
                pendingDeleting.Remove("DeleteTable");
                continue;
            }

            Ensure(code2 != Accepted, "a second DeleteTable on a DELETING table was accepted");
            Ensure(
                (code2, message2) == ("ResourceNotFoundException", $"Requested resource not found: Table: {table} not found"),
                $"a second DeleteTable on a DELETING table answered {Py.Repr(code2)} {Py.Repr(message2)}, expected {Py.Repr(want)}");
            tried.Add($"{table}: gone before the second DeleteTable");
        }

        Ensure(
            pendingCreating.Count == 0 && pendingDeleting.Count == 0,
            $"never observed the refusal of {Py.Repr(pendingCreating)} while CREATING and {Py.Repr(pendingDeleting)} " +
            $"while DELETING in {Attempts} fresh tables: {Py.Repr(tried)}");
    }

    /// <summary>A backup the target accepted is deleted at teardown.</summary>
    private static void OnSuccess(FidelityContext ctx, CreateBackupResponse resp)
    {
        var arn = resp.BackupDetails.BackupArn;
        var ddb = ctx.Dynamo;
        ctx.OnCleanup(async () =>
        {
            try
            {
                await ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = arn });
            }
            catch (BackupNotFoundException)
            {
            }
        });
    }

    /// <summary>
    /// (code, message) of the refusal <paramref name="fn"/> raises, or (Accepted, the answer) when the
    /// target accepts the call.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Outcome(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        return error is null ? (Accepted, "<response>") : (error.Code, error.Message);
    }

    private static async Task<string?> Status(IAmazonDynamoDB ddb, string table)
    {
        try
        {
            return (await ddb.DescribeTableAsync(table)).Table.TableStatus?.Value;
        }
        catch (ResourceNotFoundException)
        {
            return null;
        }
    }

    private static Task Active(IAmazonDynamoDB ddb, string table) => ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 90);

    private static double Bound(FidelityContext ctx, double capSeconds)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capSeconds, left - Math.Min(60.0, left / 4)));
    }

    /// <summary>Delete the table once it settles; already gone or already DELETING counts as deleted.</summary>
    private static async Task Drop(IAmazonDynamoDB ddb, string table)
    {
        var status = await Status(ddb, table);
        if (status is null or "DELETING")
        {
            return;
        }

        if (status != "ACTIVE")
        {
            await Active(ddb, table);
        }

        try
        {
            await ddb.DeleteTableAsync(table);
        }
        catch (ResourceNotFoundException)
        {
        }
    }
}
