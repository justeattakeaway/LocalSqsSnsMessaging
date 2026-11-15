// Translated from behavioral/dynamodb/check_update_table_on_a_missing_table_is_resource_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-on-a-missing-table-is-resource-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableOnAMissingTableIsResourceNotFound
{
    /// <summary>
    /// UpdateTable against a well-formed name that does not exist is ResourceNotFoundException, not a
    /// validation error -- the name was fine, the table was not there.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-on-a-missing-table-is-resource-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var absent = FidelityContext.UniqueName("sim2real-behav-absent-");
        var err = await Error(
            () => ddb.UpdateTableAsync(new UpdateTableRequest { TableName = absent, BillingMode = BillingMode.PAY_PER_REQUEST }),
            "UpdateTable on a table that does not exist");
        Ensure(err.Code == "ResourceNotFoundException", $"expected ResourceNotFoundException, got {err.Code}");
        var expected = $"Requested resource not found: Table: {absent} not found";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
    }

    /// <summary>The Error from a call that MUST be refused.</summary>
    private static async Task<AwsError> Error(Func<Task> call, string what) =>
        await FidelityContext.CaptureError(call) ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
}
