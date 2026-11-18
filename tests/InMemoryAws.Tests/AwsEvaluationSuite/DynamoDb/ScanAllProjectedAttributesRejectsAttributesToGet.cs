// Translated from behavioral/dynamodb/check_scan_all_projected_attributes_rejects_attributes_to_get.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-all-projected-attributes-rejects-attributes-to-get</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanAllProjectedAttributesRejectsAttributesToGet
{
    private const string Check = "scan-all-projected-attributes-rejects-attributes-to-get";
    private const string Gsi = "include-gsi";
    private const string ErrValidation = "ValidationException";

    private const string Message =
        "1 validation error detected: Cannot specify the AttributesToGet when choosing to get " +
        "ALL_PROJECTED_ATTRIBUTES";

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary><paramref name="fn"/>'s result once its Count reaches <paramref name="want"/>; a GSI write is eventually consistent.</summary>
    private static async Task<ScanResponse> PollCount(Func<Task<ScanResponse>> fn, int want, double timeoutS = 120.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        var result = await fn();
        while ((result.Count ?? 0) != want)
        {
            if (clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"Count never reached {want} within {timeoutS}s: {Py.Repr(result.Items)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            result = await fn();
        }

        return result;
    }

    /// <summary>
    /// Scan with Select=ALL_PROJECTED_ATTRIBUTES on a global secondary index rejects a paired
    /// AttributesToGet, while the same Select alone still serves the index's projected attributes.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-all-projected-attributes-rejects-attributes-to-get")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbscansel-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [.. new[] { "pk", "sk", "gi" }.Select(n => new AttributeDefinition(n, ScalarAttributeType.S))],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Gsi,
                    KeySchema = [new KeySchemaElement("gi", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["kept"] },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("p")), ("sk", S("s")), ("gi", S("g")), ("kept", S("K")), ("excluded", S("X"))));

        // Positive control: Select=ALL_PROJECTED_ATTRIBUTES alone succeeds and serves exactly the
        // index's projection, so the rejection below is attributable to AttributesToGet.
        var control = (await PollCount(
            () => ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = Gsi, Select = Select.ALL_PROJECTED_ATTRIBUTES }),
            1)).Items[0];
        var controlKeys = control.Keys.Order(StringComparer.Ordinal).ToList();
        Ensure(
            controlKeys.SequenceEqual(["gi", "kept", "pk", "sk"]),
            $"the control Scan must serve exactly the table key, the index key and 'kept', got {Py.Repr(controlKeys)}");
        Ensure(!control.ContainsKey("excluded"), $"the control leaked a non-projected attribute: {Py.Repr(control)}");

        // Break exactly one thing: the same request, with AttributesToGet added.
        var (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            IndexName = Gsi,
            Select = Select.ALL_PROJECTED_ATTRIBUTES,
            AttributesToGet = ["kept"],
        }));
        Ensure(
            code == ErrValidation,
            $"Scan Select=ALL_PROJECTED_ATTRIBUTES with AttributesToGet must be refused with {ErrValidation}, got {Py.Repr(code)} ({Py.Repr(message)})");
        Ensure(message == Message, $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
    }
}
