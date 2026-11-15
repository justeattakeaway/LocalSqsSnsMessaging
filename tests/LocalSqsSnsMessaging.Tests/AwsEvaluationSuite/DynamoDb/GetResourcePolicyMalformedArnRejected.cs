// Translated from behavioral/dynamodb/check_get_resource_policy_malformed_arn_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.get-resource-policy-malformed-arn-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetResourcePolicyMalformedArnRejected
{
    private const string CheckId = "get-resource-policy-malformed-arn-rejected";

    /// <summary>
    /// GetResourcePolicy raises PolicyNotFoundException for an existing table without a policy,
    /// ResourceNotFoundException for a missing table, and ValidationException for a non-ARN string and an IAM ARN,
    /// each with the exact message; an attached policy reads back.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.get-resource-policy-malformed-arn-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-grp-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => DeleteTableWhenSettled(ddb, tableName));
        await ddb.WaitForTableExists(tableName, delaySeconds: 2, maxAttempts: 250);
        var tableArn = (await ddb.DescribeTableAsync(tableName)).Table.TableArn;

        var accountId = ctx.Account;
        var policy = new Dictionary<string, object>
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["Sid"] = "AllowRoot",
                    ["Effect"] = "Allow",
                    ["Principal"] = new Dictionary<string, string> { ["AWS"] = $"arn:aws:iam::{accountId}:root" },
                    ["Action"] = "dynamodb:*",
                    ["Resource"] = tableArn,
                },
            },
        };
        await ddb.PutResourcePolicyAsync(new PutResourcePolicyRequest { ResourceArn = tableArn, Policy = JsonSerializer.Serialize(policy) });
        ctx.OnCleanup(() => ddb.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest { ResourceArn = tableArn }));

        // Positive control: a well-formed ARN with an attached policy succeeds.
        // PutResourcePolicy is documented as asynchronous/eventually consistent --
        // an immediate GetResourcePolicy may still see PolicyNotFoundException --
        // so poll rather than asserting on the very first read. Only that specific
        // code is worth retrying; any other error is a real failure and must
        // surface immediately rather than being masked for the full deadline.
        GetResourcePolicyResponse? resp = null;
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(60.0))
        {
            var (got, error) = await FidelityContext.Capture(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = tableArn }));
            if (error is null)
            {
                resp = got;
                break;
            }

            if (error.Code != "PolicyNotFoundException")
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3.0));
        }

        Ensure(resp is not null, "resource policy never became readable after PutResourcePolicy");
        using (var document = JsonDocument.Parse(resp.Policy))
        {
            Ensure(document.RootElement.GetProperty("Statement")[0].GetProperty("Sid").GetString() == "AllowRoot", "assert json.loads(resp['Policy'])['Statement'][0]['Sid'] == 'AllowRoot'");
        }

        // Class 1: the table genuinely exists but carries no resource policy at
        // all -> PolicyNotFoundException (the policy, not the resource, is missing).
        var otherTableName = FidelityContext.UniqueName("sim2real-behav-grp-nopolicy-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = otherTableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(otherTableName));
        await ddb.WaitForTableExists(otherTableName, delaySeconds: 2, maxAttempts: 250);
        var otherTableArn = (await ddb.DescribeTableAsync(otherTableName)).Table.TableArn;

        var (codeNoPolicy, messageNoPolicy) = await ErrorCodeAndMessage(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = otherTableArn }));
        Ensure(codeNoPolicy == "PolicyNotFoundException", $"expected PolicyNotFoundException for an existing table with no policy, got {Py.Repr(codeNoPolicy)}");
        var expectedNoPolicy = $"Resource-based policy not found for the provided ResourceArn: {otherTableArn}";
        Ensure(messageNoPolicy == expectedNoPolicy, $"expected message {Py.Repr(expectedNoPolicy)}, got {Py.Repr(messageNoPolicy)}");

        // Class 2: a well-formed-shaped ARN whose referenced table never existed
        // -> ResourceNotFoundException (the resource itself is missing), a
        // distinct error class from Class 1 even though both are "not found".
        var missingTableName = FidelityContext.UniqueName("sim2real-behav-grp-missing-");
        var missingTableArn = tableArn[..tableArn.LastIndexOf('/')] + "/" + missingTableName;
        var (codeMissing, messageMissing) = await ErrorCodeAndMessage(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = missingTableArn }));
        Ensure(codeMissing == "ResourceNotFoundException", $"expected ResourceNotFoundException for a nonexistent table ARN, got {Py.Repr(codeMissing)}");
        var expectedMissing = $"Requested resource not found: Table: {missingTableName} not found";
        Ensure(messageMissing == expectedMissing, $"expected message {Py.Repr(expectedMissing)}, got {Py.Repr(messageMissing)}");
        Ensure(codeMissing != codeNoPolicy, "the two not-found classes must be distinguishable");

        // A non-ARN string and an ARN of another service are refused up front with a
        // ValidationException whose message names the malformed value.
        var iamArn = $"arn:aws:iam::{accountId}:role/not-a-dynamodb-resource";
        (string Bogus, string Expected)[] bogusCases =
        [
            ("not-a-valid-arn", "One or more parameter values were invalid: ARNs must start with 'arn:': not-a-valid-arn"),
            (iamArn, $"One or more parameter values were invalid: Invalid resource Arn: {iamArn}"),
        ];
        foreach (var (bogus, expectedBogus) in bogusCases)
        {
            var (bogusCode, bogusMessage) = await ErrorCodeAndMessage(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = bogus }));
            Ensure(bogusCode == "ValidationException", $"expected ValidationException for malformed ResourceArn {Py.Repr(bogus)}, got {Py.Repr(bogusCode)}");
            Ensure(bogusMessage == expectedBogus, $"expected message {Py.Repr(expectedBogus)}, got {Py.Repr(bogusMessage)}");
        }
    }

    /// <summary>
    /// DeleteTable is refused with ResourceInUseException while a resource-policy update on
    /// the table is still pending; retry for a bounded time until it settles.
    /// </summary>
    private static async Task DeleteTableWhenSettled(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 90.0, double intervalSeconds = 5.0)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
            if (error is null)
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }
    }

    /// <summary>
    /// Call fn(); return its error code and message. An unexpected SUCCESS raises, so an
    /// emulator that serves a policy for a resource that has none never reads as a code mismatch.
    /// </summary>
    private static async Task<(string? Code, string? Message)> ErrorCodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected GetResourcePolicy to be rejected, but it SUCCEEDED and returned {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }
}
