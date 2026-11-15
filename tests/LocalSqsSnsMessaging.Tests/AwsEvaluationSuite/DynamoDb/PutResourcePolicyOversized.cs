// Translated from behavioral/dynamodb/check_put_resource_policy_oversized.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-resource-policy-oversized</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutResourcePolicyOversized
{
    private static readonly string[] Retryable = ["ThrottlingException", "ResourceInUseException"];

    /// <summary>
    /// DynamoDB's resource-policy control-plane enforces a tight per-resource request rate;
    /// back-to-back PutResourcePolicy calls against a just-created table can transiently 429
    /// (ThrottlingException) or report the resource as still settling (ResourceInUseException) --
    /// retry for a bounded time rather than treating either as a hard failure.
    /// </summary>
    private static async Task<PutResourcePolicyResponse> PutPolicyWithRetry(
        IAmazonDynamoDB ddb, string tableArn, string policy, double timeoutS = 90, double intervalS = 5)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            try
            {
                return await ddb.PutResourcePolicyAsync(new PutResourcePolicyRequest { ResourceArn = tableArn, Policy = policy });
            }
            catch (AmazonServiceException exc) when (Retryable.Contains(exc.ErrorCode) && clock.Elapsed.TotalSeconds < timeoutS)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            }
        }
    }

    /// <summary>
    /// Call PutResourcePolicy with the oversized document, tolerating the same transient
    /// ThrottlingException/ResourceInUseException as the control put above. An unexpected SUCCESS
    /// raises rather than returning a falsy sentinel -- an emulator ACCEPTING input real AWS refuses is
    /// the strongest signal available.
    /// </summary>
    private static async Task<AwsError> RejectOversizedWithRetry(
        IAmazonDynamoDB ddb, string tableArn, string policy, double timeoutS = 90, double intervalS = 5)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var (result, error) = await FidelityContext.Capture(() =>
                ddb.PutResourcePolicyAsync(new PutResourcePolicyRequest { ResourceArn = tableArn, Policy = policy }));
            if (error is not null)
            {
                if (Retryable.Contains(error.Code) && clock.Elapsed.TotalSeconds < timeoutS)
                {
                    await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
                    continue;
                }

                return error;
            }

            throw Fail(
                "real AWS rejects an oversized resource policy, but PutResourcePolicy SUCCEEDED "
                + $"and returned {Py.Repr(new Dictionary<string, object?> { ["RevisionId"] = result!.RevisionId })}");
        }
    }

    /// <summary>
    /// PutResourcePolicy's effect can lag briefly; a GetResourcePolicy issued immediately after
    /// may still 404 (PolicyNotFoundException) or briefly return the PREVIOUS statement -- poll
    /// until the expected statement is actually visible.
    /// </summary>
    private static async Task<GetResourcePolicyResponse> GetPolicyMatchingSid(
        IAmazonDynamoDB ddb, string tableArn, string expectedSid, double timeoutS = 60, double intervalS = 3)
    {
        var clock = FidelityTime.StartNew();
        GetResourcePolicyResponse? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            GetResourcePolicyResponse resp;
            try
            {
                resp = await ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = tableArn });
            }
            catch (AmazonServiceException exc) when (exc.ErrorCode is "ThrottlingException" or "PolicyNotFoundException")
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
                continue;
            }

            using var document = JsonDocument.Parse(resp.Policy);
            if (document.RootElement.GetProperty("Statement")[0].GetProperty("Sid").GetString() == expectedSid)
            {
                return resp;
            }

            last = resp;
            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail(
            $"GetResourcePolicy never reflected Sid={Py.Repr(expectedSid)} within {timeoutS}s; last saw "
            + $"{Py.Repr(last is null ? null : new Dictionary<string, object?> { ["Policy"] = last.Policy, ["RevisionId"] = last.RevisionId })}");
    }

    private static bool JsonEqual(string a, string b)
    {
        using var x = JsonDocument.Parse(a);
        using var y = JsonDocument.Parse(b);
        return Py.Equal(x.RootElement, y.RootElement);
    }

    /// <summary>
    /// PutResourcePolicy with a policy over 20480 bytes raises ValidationException with the exact size message and
    /// leaves the table's existing policy and RevisionId unchanged, while an ordinary policy is accepted.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-resource-policy-oversized")]
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
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var arn = (await ddb.DescribeTableAsync(t)).Table.TableArn;
        var accountId = arn.Split(':')[4];

        string Policy(string sid)
        {
            // DynamoDB rejects a Sid containing non-alphanumeric characters (e.g. a hyphen) with a
            // ValidationException before size or content is even considered, so Sid stays alphanumeric.
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["Version"] = "2012-10-17",
                ["Statement"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["Sid"] = sid,
                        ["Effect"] = "Allow",
                        ["Principal"] = new Dictionary<string, string> { ["AWS"] = $"arn:aws:iam::{accountId}:root" },
                        ["Action"] = "dynamodb:DescribeTable",
                        ["Resource"] = arn,
                    },
                },
            });
        }

        // Positive control: an ordinary-sized policy MUST be accepted -- otherwise "rejects the
        // oversized one" is indistinguishable from "rejects every PutResourcePolicy on this table".
        var validPolicy = Policy("PositiveControl");
        var controlPut = await PutPolicyWithRetry(ddb, arn, validPolicy);
        var controlRevision = controlPut.RevisionId;
        // No separate delete_resource_policy cleanup: DeleteTable removes any attached resource
        // policy with the table, and an extra call here only adds another throttling-prone
        // PutResourcePolicy-surface request to the 60s teardown budget for no benefit.

        var controlRead = await GetPolicyMatchingSid(ddb, arn, "PositiveControl");
        Ensure(
            controlRead.RevisionId == controlRevision,
            $"positive control: GetResourcePolicy RevisionId {Py.Repr(controlRead.RevisionId)} does not match the PutResourcePolicy response {Py.Repr(controlRevision)}");

        // A syntactically-valid but grossly oversized policy document (the documented resource-policy
        // size ceiling is far smaller than this) built by padding the Sid field.
        var oversizedPolicy = Policy(new string('x', 500_000));

        var error = await RejectOversizedWithRetry(ddb, arn, oversizedPolicy);
        var code = error.Code;
        var message = error.Message;
        Ensure(code == "ValidationException", $"expected a size-limit rejection for an oversized policy, got {code} ({error})");
        const string expectedMessage = "One or more parameter values were invalid: Maximum policy size of 20480 bytes exceeded";
        Ensure(message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");

        // Effect absent, via an independent read at a different operation: the rejected oversized PUT
        // must not have touched the pre-existing policy at all -- not overwritten, not truncated, not
        // revision-bumped.
        var after = await ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = arn });
        Ensure(
            after.RevisionId == controlRevision,
            $"a rejected oversized PutResourcePolicy must not change the RevisionId of the existing policy, got {Py.Repr(after.RevisionId)}, expected unchanged {Py.Repr(controlRevision)}");
        Ensure(
            JsonEqual(after.Policy, validPolicy),
            $"a rejected oversized PutResourcePolicy must leave the existing policy byte-identical, got {Py.Repr(after.Policy)}, expected {Py.Repr(validPolicy)}");
    }
}
