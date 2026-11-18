// Translated from behavioral/dynamodb/check_backup_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.backup-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class BackupNotFound
{
    private const string Check = "backup-not-found";

    /// <summary>
    /// DescribeBackup and DeleteBackup for a syntactically well-formed but
    /// unknown backup ARN both return BackupNotFoundException.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.backup-not-found")]
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

        var tableArn = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = t })).Table.TableArn;
        var fakeBackupArn = $"{tableArn}/backup/01700000000000-aaaaaaaa";

        var expectedMsg = $"Backup not found: {fakeBackupArn}";

        var (describeCode, describeMsg) = await Error(() => ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = fakeBackupArn }));
        Ensure(describeCode == "BackupNotFoundException", $"expected BackupNotFoundException, got {describeCode}");
        Ensure(describeMsg == expectedMsg, $"DescribeBackup message: expected {Py.Repr(expectedMsg)}, got {Py.Repr(describeMsg)}");

        var (deleteCode, deleteMsg) = await Error(() => ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = fakeBackupArn }));
        Ensure(deleteCode == "BackupNotFoundException", $"expected BackupNotFoundException, got {deleteCode}");
        Ensure(deleteMsg == expectedMsg, $"DeleteBackup message: expected {Py.Repr(expectedMsg)}, got {Py.Repr(deleteMsg)}");
    }

    /// <summary>The (Code, Message) of a call real AWS refuses. RAISES on unexpected success.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected a ClientError, but the call succeeded and returned {Py.Repr(result)}");
    }
}
