// Translated from behavioral/dynamodb/check_ttl_rename_while_enabled_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ttl-rename-while-enabled-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TtlRenameWhileEnabledRejected
{
    private const string Check = "ttl-rename-while-enabled-rejected";
    private const string AttrLive = "expires_at";
    private const string AttrOther = "purge_after";

    /// <summary>The (code, message) fn raised; an unexpected success raises AssertionError.</summary>
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

    private static async Task<string> MakeTable(FidelityContext ctx, IAmazonDynamoDB ddb, string prefix)
    {
        var name = FidelityContext.UniqueName(prefix);
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 90);
        return name;
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
    /// Once TTL is ENABLED on one attribute, enabling it on a different attribute is refused with
    /// ValidationException and the live TTL is untouched; a table without TTL accepts that attribute.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ttl-rename-while-enabled-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        // --- set up: TTL live on ATTR_LIVE ----------------------------------------------------------
        var table = await MakeTable(ctx, ddb, "sim2real-behav-ttlren-");
        var enabled = (await ddb.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = table,
            TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = AttrLive },
        })).TimeToLiveSpecification;
        Ensure(
            enabled is not null && enabled.Enabled == true && enabled.AttributeName == AttrLive,
            $"UpdateTimeToLive must echo the enabled specification; got {SpecRepr(enabled)}");
        var live = await PollTtl(ddb, table, "ENABLED");
        Ensure(
            live.TimeToLiveStatus == TimeToLiveStatus.ENABLED && live.AttributeName == AttrLive,
            $"TTL must reach ENABLED on {Py.Repr(AttrLive)} before the rename is attempted; got {TtlRepr(live)}");

        // --- break exactly one thing: point the enabled TTL at a different attribute ----------------
        var (code, message) = await Error(() => ddb.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = table,
            TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = AttrOther },
        }));
        Ensure(
            code == "ValidationException",
            $"enabling TTL on {Py.Repr(AttrOther)} while it is active on {Py.Repr(AttrLive)} must be rejected with ValidationException; got {Py.Repr(code)}");
        const string expected = $"TimeToLive is active on a different AttributeName: current AttributeName is {AttrLive}";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        // --- effect absent: TTL is still bound to the original attribute ----------------------------
        var after = await Ttl(ddb, table);
        Ensure(
            after.TimeToLiveStatus == TimeToLiveStatus.ENABLED && after.AttributeName == AttrLive,
            $"a rejected rename must leave TTL ENABLED on {Py.Repr(AttrLive)}; DescribeTimeToLive says {TtlRepr(after)}");

        // --- positive control: the rejected attribute name is itself valid --------------------------
        var control = await MakeTable(ctx, ddb, "sim2real-behav-ttlctl-");
        var ok = (await ddb.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = control,
            TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = AttrOther },
        })).TimeToLiveSpecification;
        Ensure(
            ok is not null && ok.Enabled == true && ok.AttributeName == AttrOther,
            $"a table with no TTL yet must accept {Py.Repr(AttrOther)}; UpdateTimeToLive echoed {SpecRepr(ok)}");
        var ctl = await PollTtl(ddb, control, "ENABLED");
        Ensure(
            ctl.TimeToLiveStatus == TimeToLiveStatus.ENABLED && ctl.AttributeName == AttrOther,
            $"the control table's TTL must reach ENABLED on {Py.Repr(AttrOther)}; got {TtlRepr(ctl)}");
    }
}
