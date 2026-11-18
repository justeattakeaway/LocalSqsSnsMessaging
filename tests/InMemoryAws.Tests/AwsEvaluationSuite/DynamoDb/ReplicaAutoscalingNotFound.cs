// Translated from behavioral/dynamodb/check_replica_autoscaling_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.replica-autoscaling-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReplicaAutoscalingNotFound
{
    /// <summary>
    /// Call fn(); return its botocore error code and message. An unexpected SUCCESS raises, because
    /// an emulator ACCEPTING a call real AWS refuses on a nonexistent table is the strongest signal
    /// available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"real AWS rejects this call against a nonexistent table, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// DescribeTableReplicaAutoScaling and UpdateTableReplicaAutoScaling both
    /// return ResourceNotFoundException for a nonexistent table, rather than a
    /// default/empty success (overly-permissive boundary).
    /// </summary>
    [Test]
    [DisplayName("dynamodb.replica-autoscaling-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var missing = FidelityContext.UniqueName("sim2real-behav-no-such-table-");
        var (describeCode, describeMessage) = await Error(() => ddb.DescribeTableReplicaAutoScalingAsync(
            new DescribeTableReplicaAutoScalingRequest { TableName = missing }));
        Ensure(describeCode == "ResourceNotFoundException", $"expected ResourceNotFoundException, got {describeCode}");
        var expectedDescribe = $"Global table with name: '{missing}' does not exist.";
        Ensure(describeMessage == expectedDescribe, $"unexpected message {Py.Repr(describeMessage)}");
        var (updateCode, updateMessage) = await Error(() => ddb.UpdateTableReplicaAutoScalingAsync(new UpdateTableReplicaAutoScalingRequest
        {
            TableName = missing,
            ProvisionedWriteCapacityAutoScalingUpdate = new AutoScalingSettingsUpdate
            {
                MinimumUnits = 1,
                MaximumUnits = 10,
                ScalingPolicyUpdate = new AutoScalingPolicyUpdate
                {
                    PolicyName = "test-policy",
                    TargetTrackingScalingPolicyConfiguration = new AutoScalingTargetTrackingScalingPolicyConfigurationUpdate { TargetValue = 70.0 },
                },
            },
        }));
        Ensure(updateCode == "ResourceNotFoundException", $"expected ResourceNotFoundException, got {updateCode}");
        var expectedUpdate = $"Global table with name: '{missing}' does not exist.";
        Ensure(updateMessage == expectedUpdate, $"unexpected Update message {Py.Repr(updateMessage)}");
    }
}
