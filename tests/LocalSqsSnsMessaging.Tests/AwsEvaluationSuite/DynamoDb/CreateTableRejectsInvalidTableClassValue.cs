// Translated from behavioral/dynamodb/check_create_table_rejects_invalid_table_class_value.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-rejects-invalid-table-class-value</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableRejectsInvalidTableClassValue
{
    private static readonly (string Code, string Message) InvalidTableClass = (
        "ValidationException",
        "Invalid table-class parameter provided. Please try again with a valid table-class "
        + "value: [STANDARD, STANDARD_INFREQUENT_ACCESS].");

    /// <summary>
    /// CreateTable rejects a TableClass value outside the enum with the exact AWS message,
    /// creating no table, while a valid TableClass on the same call shape still succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-rejects-invalid-table-class-value")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var rejected = FidelityContext.UniqueName("sim2real-behav-");

        await ExpectError(
            () => ddb.CreateTableAsync(new CreateTableRequest
            {
                TableName = rejected,
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
                TableClass = new TableClass("FREQUENT_ACCESS"),
            }),
            InvalidTableClass.Code,
            InvalidTableClass.Message);
        var code = await Code(() => ddb.DescribeTableAsync(rejected));
        Ensure(
            code == "ResourceNotFoundException",
            $"the rejected CreateTable must not have created a table, DescribeTable gave {Py.Repr(code)} instead of ResourceNotFoundException");

        var accepted = FidelityContext.UniqueName("sim2real-behav-");
        var created = await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = accepted,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            TableClass = TableClass.STANDARD,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(accepted));
        await ddb.WaitForTableExists(accepted, delaySeconds: 2, maxAttempts: 250);
        var summary = Boto(created.TableDescription.TableClassSummary);
        Ensure(
            Py.Equal(summary, new Dictionary<string, object?> { ["TableClass"] = "STANDARD" }),
            $"a valid TableClass on the same call shape must still succeed and be reported, got {Py.Repr(summary)}");
    }

    private static Dictionary<string, object?>? Boto(TableClassSummary? summary)
    {
        if (summary is null)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (summary.TableClass is not null)
        {
            result["TableClass"] = summary.TableClass.Value;
        }

        if (summary.LastUpdateDateTime is not null)
        {
            result["LastUpdateDateTime"] = summary.LastUpdateDateTime;
        }

        return result;
    }

    /// <summary>Assert <c>fn()</c> raises exactly <c>(code, message)</c>; an unexpected success raises too.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            var got = (error.Code, error.Message);
            Ensure(got == (code, message), $"expected {Py.Repr((code, message))}, got {Py.Repr(got)}");
            return;
        }

        throw Fail($"expected {code} but the call succeeded: {result}");
    }

    /// <summary>The Error.Code <paramref name="fn"/> raises. An unexpected success raises too.</summary>
    private static async Task<string?> Code<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return error.Code;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }
}
