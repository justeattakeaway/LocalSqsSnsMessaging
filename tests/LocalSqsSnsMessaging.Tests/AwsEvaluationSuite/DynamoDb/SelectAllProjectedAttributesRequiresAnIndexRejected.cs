// Translated from behavioral/dynamodb/check_select_all_projected_attributes_requires_an_index_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.select-all-projected-attributes-requires-an-index-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SelectAllProjectedAttributesRequiresAnIndexRejected
{
    private const string CheckId = "select-all-projected-attributes-requires-an-index-rejected";
    private const string ErrValidation = "ValidationException";
    private const string Index = "keys-only-index";

    private static readonly Dictionary<string, string> ConflictMessage = new(StringComparer.Ordinal)
    {
        ["ALL_ATTRIBUTES"] = "1 validation error detected: Cannot specify the ProjectionExpression when choosing to get ALL_ATTRIBUTES",
        ["COUNT"] = "1 validation error detected: Cannot specify the ProjectionExpression when choosing to get only the Count",
        ["ALL_PROJECTED_ATTRIBUTES"] = "1 validation error detected: Cannot specify the ProjectionExpression when choosing to get ALL_PROJECTED_ATTRIBUTES",
    };

    /// <summary>The error code and message <paramref name="fn"/> raises; an unexpected success fails loudly instead.</summary>
    private static async Task<(string? Code, string? Message)> Code<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"real AWS refuses this call, but it SUCCEEDED and returned {Describe(result)}");
        }

        return (error.Code, error.Message);
    }

    private static string Describe(object? result) => result switch
    {
        QueryResponse q => Py.Repr(new Dictionary<string, object?>(StringComparer.Ordinal) { ["Items"] = q.Items, ["Count"] = q.Count }),
        ScanResponse s => Py.Repr(new Dictionary<string, object?>(StringComparer.Ordinal) { ["Items"] = s.Items, ["Count"] = s.Count }),
        _ => Py.Repr(result),
    };

    /// <summary>
    /// <paramref name="fn"/>'s result once its Count reaches <paramref name="want"/>, bounded by the check's own time budget.
    /// A GSI is updated asynchronously from the base table it indexes, so a query against it right
    /// after the put may still lag; this is the one bounded wait that is legitimate here.
    /// </summary>
    private static async Task<T> PollUntilCount<T>(Func<Task<T>> fn, Func<T, int?> count, int want, FidelityContext ctx)
    {
        const double gsiPollBudgetS = 30.0;
        var budget = Math.Min(gsiPollBudgetS, ctx.TimeLeftSeconds);
        var clock = FidelityTime.StartNew();
        var result = await fn();
        while ((count(result) ?? 0) != want && clock.Elapsed < TimeSpan.FromSeconds(budget))
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
            result = await fn();
        }

        return result;
    }

    /// <summary>
    /// Select is validated against the rest of a Query or Scan request -- a missing IndexName or
    /// ProjectionExpression, a KEYS_ONLY index, or a ProjectionExpression paired with any Select
    /// other than SPECIFIC_ATTRIBUTES -- while each legal combination still succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.select-all-projected-attributes-requires-an-index-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-select-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gsi_pk", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = [new KeySchemaElement("gsi_pk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("P")), ("sk", S("S")), ("gsi_pk", S("G")), ("body", S("payload"))));

        QueryRequest Base() => new()
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("P"))),
        };
        QueryRequest OnIndex() => new()
        {
            TableName = table,
            IndexName = Index,
            KeyConditionExpression = "gsi_pk = :g",
            ExpressionAttributeValues = Item((":g", S("G"))),
        };
        QueryRequest With(QueryRequest request, Select select, string? projection = null, bool? consistentRead = null)
        {
            request.Select = select;
            request.ProjectionExpression = projection;
            request.ConsistentRead = consistentRead;
            return request;
        }

        // --- ALL_PROJECTED_ATTRIBUTES names an index's projection and needs an index ---
        var (code, message) = await Code(() => ddb.QueryAsync(With(Base(), Select.ALL_PROJECTED_ATTRIBUTES)));
        Ensure(
            code == ErrValidation,
            $"Select=ALL_PROJECTED_ATTRIBUTES refers to an index projection and must be refused with {ErrValidation} when no IndexName accompanies it; got {code}");
        Ensure(
            message == "1 validation error detected: ALL_PROJECTED_ATTRIBUTES can be used only when Querying using an IndexName",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        var projected = await PollUntilCount(() => ddb.QueryAsync(With(OnIndex(), Select.ALL_PROJECTED_ATTRIBUTES)), r => r.Count, 1, ctx);
        Ensure(
            projected.Count == 1,
            $"the same Select value WITH an IndexName is legal and must return the indexed item, got {Describe(projected)}");

        // --- SPECIFIC_ATTRIBUTES needs something to be specific about ---
        (code, message) = await Code(() => ddb.QueryAsync(With(Base(), Select.SPECIFIC_ATTRIBUTES)));
        Ensure(
            code == ErrValidation,
            $"Select=SPECIFIC_ATTRIBUTES with no ProjectionExpression names no attributes and must be refused with {ErrValidation}; got {code}");
        Ensure(
            message == "1 validation error detected: Must specify the AttributesToGet or ProjectionExpression when choosing to get SPECIFIC_ATTRIBUTES",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        var specific = await ddb.QueryAsync(With(Base(), Select.SPECIFIC_ATTRIBUTES, "body"));
        Ensure(
            Py.Equal(specific.Items, new[] { Item(("body", S("payload"))) }),
            $"the same Select value WITH a ProjectionExpression is legal and must return only that attribute, got {Py.Repr(specific.Items)}");

        // --- ALL_ATTRIBUTES cannot be served from a KEYS_ONLY index ---
        (code, message) = await Code(() => ddb.QueryAsync(With(OnIndex(), Select.ALL_ATTRIBUTES)));
        Ensure(
            code == ErrValidation,
            $"Select=ALL_ATTRIBUTES against a KEYS_ONLY index asks for attributes the index does not hold and must be refused with {ErrValidation} rather than served from the base table; got {code}");
        Ensure(
            message == $"One or more parameter values were invalid: Select type ALL_ATTRIBUTES is not supported for global secondary index {Index} because its projection type is not ALL",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        var allAttrs = await ddb.QueryAsync(With(Base(), Select.ALL_ATTRIBUTES, consistentRead: true));
        Ensure(
            Py.Equal(allAttrs.Items, new[] { Item(("pk", S("P")), ("sk", S("S")), ("gsi_pk", S("G")), ("body", S("payload"))) }),
            $"the same Select value against the BASE table is legal and must return the whole item, got {Py.Repr(allAttrs.Items)}");

        // --- Select + ProjectionExpression is a disallowed combination on Query, for every Select
        // value other than SPECIFIC_ATTRIBUTES (already proven legal above) ---
        foreach (var select in new[] { "ALL_ATTRIBUTES", "COUNT" })
        {
            (code, message) = await Code(() => ddb.QueryAsync(With(Base(), new Select(select), "body")));
            Ensure(
                code == ErrValidation,
                $"Select={select} together with a ProjectionExpression on Query must be refused with {ErrValidation}; got {code}");
            Ensure(
                message == ConflictMessage[select],
                $"expected AWS's exact ValidationException message for Select={select}+ProjectionExpression on Query, got {Py.Repr(message)}");
        }

        (code, message) = await Code(() => ddb.QueryAsync(With(OnIndex(), Select.ALL_PROJECTED_ATTRIBUTES, "body")));
        Ensure(
            code == ErrValidation,
            $"Select=ALL_PROJECTED_ATTRIBUTES together with a ProjectionExpression on Query must be refused with {ErrValidation} even though an IndexName is present; got {code}");
        Ensure(
            message == ConflictMessage["ALL_PROJECTED_ATTRIBUTES"],
            $"expected AWS's exact ValidationException message for Select=ALL_PROJECTED_ATTRIBUTES+ProjectionExpression on Query, got {Py.Repr(message)}");
        var countQuery = await ddb.QueryAsync(With(Base(), Select.COUNT, consistentRead: true));
        Ensure(
            countQuery.Count == 1,
            $"Select=COUNT with no ProjectionExpression is legal on Query and must count the item, got {Describe(countQuery)}");

        // --- the same conflict class holds on Scan: this validation is not pinned to Query alone ---
        ScanRequest BaseScan(Select select, string? projection = null, bool? consistentRead = null) =>
            new() { TableName = table, Select = select, ProjectionExpression = projection, ConsistentRead = consistentRead };
        ScanRequest OnIndexScan(Select select, string? projection = null) =>
            new() { TableName = table, IndexName = Index, Select = select, ProjectionExpression = projection };
        foreach (var select in new[] { "ALL_ATTRIBUTES", "COUNT" })
        {
            (code, message) = await Code(() => ddb.ScanAsync(BaseScan(new Select(select), "body")));
            Ensure(
                code == ErrValidation,
                $"Select={select} together with a ProjectionExpression on Scan must be refused with {ErrValidation}; got {code}");
            Ensure(
                message == ConflictMessage[select],
                $"expected AWS's exact ValidationException message for Select={select}+ProjectionExpression on Scan, got {Py.Repr(message)}");
        }

        (code, message) = await Code(() => ddb.ScanAsync(OnIndexScan(Select.ALL_PROJECTED_ATTRIBUTES, "body")));
        Ensure(
            code == ErrValidation,
            $"Select=ALL_PROJECTED_ATTRIBUTES together with a ProjectionExpression on Scan must be refused with {ErrValidation} even though an IndexName is present; got {code}");
        Ensure(
            message == ConflictMessage["ALL_PROJECTED_ATTRIBUTES"],
            $"expected AWS's exact ValidationException message for Select=ALL_PROJECTED_ATTRIBUTES+ProjectionExpression on Scan, got {Py.Repr(message)}");

        // Positive controls: each Select value with no ProjectionExpression remains legal on Scan.
        var allAttrsScan = await ddb.ScanAsync(BaseScan(Select.ALL_ATTRIBUTES, consistentRead: true));
        Ensure(
            Py.Equal(allAttrsScan.Items, new[] { Item(("pk", S("P")), ("sk", S("S")), ("gsi_pk", S("G")), ("body", S("payload"))) }),
            $"Select=ALL_ATTRIBUTES with no ProjectionExpression is legal on a base-table Scan, got {Py.Repr(allAttrsScan.Items)}");
        var countScan = await ddb.ScanAsync(BaseScan(Select.COUNT, consistentRead: true));
        Ensure(
            countScan.Count == 1,
            $"Select=COUNT with no ProjectionExpression is legal on Scan and must count the item, got {Describe(countScan)}");
        var projectedScan = await PollUntilCount(() => ddb.ScanAsync(OnIndexScan(Select.ALL_PROJECTED_ATTRIBUTES)), r => r.Count, 1, ctx);
        Ensure(
            Py.Equal(projectedScan.Items, new[] { Item(("sk", S("S")), ("pk", S("P")), ("gsi_pk", S("G"))) }),
            $"Select=ALL_PROJECTED_ATTRIBUTES with no ProjectionExpression is legal on a Scan of the index, got {Py.Repr(projectedScan.Items)}");
    }
}
