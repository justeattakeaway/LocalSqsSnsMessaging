// Translated from behavioral/dynamodb/check_describe_backup_not_found_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.describe-backup-not-found-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribeBackupNotFoundRejected
{
    /// <summary>DescribeBackup with an unknown backup ARN returns BackupNotFoundException.</summary>
    [Test]
    [DisplayName("dynamodb.describe-backup-not-found-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var accountId = ctx.Account;
        var fakeTable = FidelityContext.UniqueName("sim2real-notable-");
        // Real DynamoDB backup ids are a 14-digit (zero-padded) millisecond epoch timestamp,
        // a dash, and an 8-character hex suffix (e.g. "01639426382000-1509b1b3"). A backup id
        // of the wrong shape (e.g. only 12 digits) is rejected by ARN-shape validation with a
        // generic ValidationException *before* DynamoDB ever looks up whether a backup with
        // that id exists — so it never reaches BackupNotFoundException. Use a well-formed but
        // nonexistent id so the request actually exercises the not-found lookup.
        var fakeBackupId = "0" + FidelityTime.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "-abcdef01";
        var fakeArn = $"arn:aws:dynamodb:{ctx.Region}:{accountId}:table/{fakeTable}/backup/{fakeBackupId}";

        var (code, message) = await Error(async () => await ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = fakeArn }));
        Ensure(code == "BackupNotFoundException", $"expected BackupNotFoundException, got {code ?? "None"}");
        var expectedMessage = $"Backup not found: {fakeArn}";
        Ensure(message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");
    }

    /// <summary>(code, message) of the ClientError fn raises; an unexpected success raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a rejection, but the call SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }
}
