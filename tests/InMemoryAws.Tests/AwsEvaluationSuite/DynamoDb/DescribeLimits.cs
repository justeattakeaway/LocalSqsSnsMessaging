// Translated from behavioral/dynamodb/check_describe_limits.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.describe-limits</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribeLimits
{
    private const string CheckId = "describe-limits";

    /// <summary>
    /// DescribeLimits reports positive account and table capacity limits with TableMax* &lt;=
    /// AccountMax*, and the account-level ceiling is unchanged after creating a provisioned table.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.describe-limits")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var limits1 = Limits(await ddb.DescribeLimitsAsync(new DescribeLimitsRequest()));
        foreach (var key in (string[])["AccountMaxReadCapacityUnits", "AccountMaxWriteCapacityUnits", "TableMaxReadCapacityUnits", "TableMaxWriteCapacityUnits"])
        {
            Ensure(limits1[key] is > 0, Py.Repr(limits1));
        }

        Ensure(limits1["TableMaxReadCapacityUnits"] <= limits1["AccountMaxReadCapacityUnits"], Py.Repr(limits1));
        Ensure(limits1["TableMaxWriteCapacityUnits"] <= limits1["AccountMaxWriteCapacityUnits"], Py.Repr(limits1));

        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 5, WriteCapacityUnits = 5 },
            Tags = [new Tag { Key = "sim2real-behav", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var limits2 = Limits(await ddb.DescribeLimitsAsync(new DescribeLimitsRequest()));
        Ensure(limits2["AccountMaxReadCapacityUnits"] == limits1["AccountMaxReadCapacityUnits"], Py.Repr((limits1, limits2)));
        Ensure(limits2["AccountMaxWriteCapacityUnits"] == limits1["AccountMaxWriteCapacityUnits"], Py.Repr((limits1, limits2)));
    }

    private static Dictionary<string, long?> Limits(DescribeLimitsResponse response) => new(StringComparer.Ordinal)
    {
        ["AccountMaxReadCapacityUnits"] = response.AccountMaxReadCapacityUnits,
        ["AccountMaxWriteCapacityUnits"] = response.AccountMaxWriteCapacityUnits,
        ["TableMaxReadCapacityUnits"] = response.TableMaxReadCapacityUnits,
        ["TableMaxWriteCapacityUnits"] = response.TableMaxWriteCapacityUnits,
    };
}
