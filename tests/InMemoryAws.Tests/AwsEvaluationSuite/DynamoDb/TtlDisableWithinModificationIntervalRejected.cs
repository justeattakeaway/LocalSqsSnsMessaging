// Translated from behavioral/dynamodb/check_ttl_disable_within_modification_interval_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ttl-disable-within-modification-interval-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TtlDisableWithinModificationIntervalRejected
{
    private const string Check = "ttl-disable-within-modification-interval-rejected";
    private const string Attr = "expires_at";

    /// <summary>The (code, message) fn raised; a call that unexpectedly succeeds raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, err) = await FidelityContext.Capture(fn);
        if (err is not null)
        {
            return (err.Code, err.Message);
        }

        throw Fail($"expected the call to be rejected, but it succeeded: {result}");
    }

    private static async Task<TimeToLiveDescription> Ttl(IAmazonDynamoDB ddb, string name) =>
        (await ddb.DescribeTimeToLiveAsync(new DescribeTimeToLiveRequest { TableName = name })).TimeToLiveDescription;

    private static async Task<TimeToLiveDescription> PollTtl(IAmazonDynamoDB ddb, string name, string status, double timeoutS = 120.0)
    {
        var clock = FidelityTime.StartNew();
        var last = await Ttl(ddb, name);
        while (last.TimeToLiveStatus?.Value != status && clock.Elapsed.TotalSeconds < timeoutS)
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(3.0));
            last = await Ttl(ddb, name);
        }

        return last;
    }

    private static string TtlRepr(TimeToLiveDescription? d)
    {
        if (d is null)
        {
            return "None";
        }

        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (d.TimeToLiveStatus is not null)
        {
            dict["TimeToLiveStatus"] = d.TimeToLiveStatus;
        }

        if (d.AttributeName is not null)
        {
            dict["AttributeName"] = d.AttributeName;
        }

        return Py.Repr(dict);
    }

    private static string SpecRepr(TimeToLiveSpecification? s)
    {
        if (s is null)
        {
            return "None";
        }

        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (s.Enabled is not null)
        {
            dict["Enabled"] = s.Enabled;
        }

        if (s.AttributeName is not null)
        {
            dict["AttributeName"] = s.AttributeName;
        }

        return Py.Repr(dict);
    }

    /// <summary>
    /// Disabling TTL right after enabling it is refused with ValidationException and TTL stays
    /// ENABLED, because DynamoDB allows one TTL modification per interval; the enable is accepted.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ttl-disable-within-modification-interval-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ttlint-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 90);

        var before = await Ttl(ddb, table);
        Ensure(
            before.TimeToLiveStatus == TimeToLiveStatus.DISABLED,
            $"a new table must start with TTL DISABLED; got {TtlRepr(before)}");

        var enabled = (await ddb.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = table,
            TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = Attr },
        })).TimeToLiveSpecification;
        Ensure(
            enabled is not null && enabled.Enabled == true && enabled.AttributeName == Attr,
            $"the first TTL modification must be accepted and echoed; got {SpecRepr(enabled)}");
        var live = await PollTtl(ddb, table, "ENABLED");
        Ensure(
            live.TimeToLiveStatus == TimeToLiveStatus.ENABLED && live.AttributeName == Attr,
            $"TTL must reach ENABLED on {Py.Repr(Attr)}; got {TtlRepr(live)}");

        var (code, message) = await Error(() => ddb.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = table,
            TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = false, AttributeName = Attr },
        }));
        Ensure(
            code == "ValidationException",
            $"disabling TTL moments after enabling it must be rejected with ValidationException; got {Py.Repr(code)}");
        const string expected = "Time to live has been modified multiple times within a fixed interval";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        var after = await Ttl(ddb, table);
        Ensure(
            after.TimeToLiveStatus == TimeToLiveStatus.ENABLED && after.AttributeName == Attr,
            $"a rejected disable must leave TTL ENABLED on {Py.Repr(Attr)}; DescribeTimeToLive says {TtlRepr(after)}");
    }
}
