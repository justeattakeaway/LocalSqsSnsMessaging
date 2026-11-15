// Translated from behavioral/dynamodb/check_resource_policy_explicit_deny_on_getitem_rejected_put_still_allowed.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.resource-policy-explicit-deny-on-getitem-rejected-put-still-allowed</summary>
[Category("AwsEvaluationSuite")]
public sealed class ResourcePolicyExplicitDenyOnGetitemRejectedPutStillAllowed
{
    private const string Check = "resource-policy-explicit-deny-on-getitem-rejected-put-still-allowed";
    private const string ErrAccessDenied = "AccessDeniedException";

    /// <summary>Phrase AWS uses to attribute a 403 to the resource-based policy rather than another cause.</summary>
    private const string DenyAttribution = "explicit deny in a resource-based policy";

    /// <summary>
    /// A table resource policy can take several minutes to become effective; the loop fails rather
    /// than skips at the bound because "never enforced" is the defect being checked.
    /// </summary>
    private const int PropagationBudgetS = 600;

    /// <summary>
    /// An explicit Deny on GetItem in a table resource policy rejects GetItem with
    /// AccessDeniedException while PutItem still succeeds, data is untouched, and deleting it restores
    /// GetItem; only evaluated, action-scoped enforcement passes.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbDetail, "DynamoDB resource policies are stored and returned but never enforced: the explicit Deny on GetItem is ignored")]
    [DisplayName("dynamodb.resource-policy-explicit-deny-on-getitem-rejected-put-still-allowed")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-rp-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });

        async Task DropTable()
        {
            // DeleteResourcePolicy leaves the table momentarily non-deletable; a single-shot
            // delete_table here raises ResourceInUseException and leaks the table.
            var dropClock = FidelityTime.StartNew();
            while (dropClock.Elapsed < TimeSpan.FromSeconds(180))
            {
                var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(table));
                if (error is null)
                {
                    return;
                }

                if (error.Code != "ResourceInUseException")
                {
                    throw error.Exception;
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(5));
            }

            await ddb.DeleteTableAsync(table);
        }

        ctx.OnCleanup(DropTable);
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var arn = (await ddb.DescribeTableAsync(table)).Table.TableArn;
        await ddb.PutItemAsync(table, Item(("pk", S("first"))));
        var caller = ctx.CallerArn;
        var policy = new Dictionary<string, object>
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["Sid"] = "DenyGetItemOnly",
                    ["Effect"] = "Deny",
                    ["Principal"] = new Dictionary<string, string> { ["AWS"] = caller },
                    ["Action"] = "dynamodb:GetItem",
                    ["Resource"] = arn,
                },
            },
        };
        var policyJson = JsonSerializer.Serialize(policy);
        await ddb.PutResourcePolicyAsync(new PutResourcePolicyRequest { ResourceArn = arn, Policy = policyJson });

        async Task DropPolicy()
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest { ResourceArn = arn }));
            if (error is not null && error.Code != "PolicyNotFoundException")
            {
                throw error.Exception;
            }
        }

        ctx.OnCleanup(DropPolicy);

        // A resource-policy change is eventually consistent and exposes no condition that reports
        // "effective", so this is the sanctioned bounded retry at first use of the permission.
        var clock = FidelityTime.StartNew();
        AwsError? denied = null;
        GetItemResponse? lastOk = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(PropagationBudgetS))
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("first"))),
                ConsistentRead = true,
            }));
            if (error is not null)
            {
                denied = error;
                break;
            }

            lastOk = resp;
            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        Ensure(
            denied is not null,
            $"the explicit Deny on dynamodb:GetItem was never enforced within {Py.Repr(PropagationBudgetS)}s -- GetItem still returned {Py.Repr(lastOk?.Item)} under policy {policyJson}");
        var code = denied.Code;
        var message2 = denied.Message;
        Ensure(code == ErrAccessDenied, $"expected {Py.Repr(ErrAccessDenied)} from the denied GetItem, got {Py.Repr(code)} ({Py.Repr(denied.Message)})");
        Ensure(
            message2 == $"User: {caller} is not authorized to perform: dynamodb:GetItem on resource: {arn} with an explicit deny in a resource-based policy",
            $"expected the exact AccessDeniedException message AWS returns, got {Py.Repr(message2)}");

        // Attribute the denial to the resource-based policy specifically: an AccessDeniedException
        // raised for some unrelated reason (identity policy, SCP, a throttle-adjacent misclassification)
        // must not satisfy this leg.
        var message = denied.Message ?? "";
        Ensure(
            message.Contains(DenyAttribution, StringComparison.Ordinal),
            $"expected the denial message to attribute the block to the resource-based policy (substring {Py.Repr(DenyAttribution)}), got {Py.Repr(message)}");

        // Positive control 1: the same principal, the same table, a different action.
        await ddb.PutItemAsync(table, Item(("pk", S("second"))));

        // ... and the Deny disturbed no data: both writes are there, read through an operation the
        // policy does not name.
        var scanned = new List<string>();
        Dictionary<string, AttributeValue>? startKey = null;
        while (true)
        {
            var page = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true, ExclusiveStartKey = startKey });
            scanned.AddRange(page.Items.Select(i => i["pk"].S));
            if (page.LastEvaluatedKey is null)
            {
                break;
            }

            startKey = page.LastEvaluatedKey;
        }

        Ensure(
            scanned.Order(StringComparer.Ordinal).SequenceEqual(["first", "second"]),
            $"the denied GetItem or the policy churn disturbed the table contents: {Py.Repr(scanned)}");

        // Positive control 2: remove the one broken thing and the denied call recovers.
        await ddb.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest { ResourceArn = arn });
        clock = FidelityTime.StartNew();
        GetItemResponse? restored = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(PropagationBudgetS))
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("first"))),
                ConsistentRead = true,
            }));
            if (error is null)
            {
                restored = resp;
                break;
            }

            if (error.Code != ErrAccessDenied)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        Ensure(
            restored is not null && Py.Equal(restored.Item, Item(("pk", S("first")))),
            $"GetItem did not recover after DeleteResourcePolicy within {Py.Repr(PropagationBudgetS)}s: {Py.Repr(restored?.Item)}");
    }
}
