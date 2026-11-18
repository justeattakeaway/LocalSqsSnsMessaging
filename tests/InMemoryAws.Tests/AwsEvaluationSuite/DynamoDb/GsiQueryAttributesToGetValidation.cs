// Translated from behavioral/dynamodb/check_gsi_query_attributes_to_get_validation.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-query-attributes-to-get-validation</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiQueryAttributesToGetValidation
{
    private const string CheckId = "gsi-query-attributes-to-get-validation";
    private const string ErrValidation = "ValidationException";
    private const string Index = "gsi1";
    private const int PropagateSeconds = 180;

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the Query to be rejected, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>
    /// Legacy AttributesToGet on a GSI Query: an empty list, a duplicate name and an attribute the
    /// index does not project are each ValidationException, while the projected attribute succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-query-attributes-to-get-validation")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        // AttributesToGet carries a model length-min of 1; the empty-list case needs the request to
        // actually reach the service rather than be rejected by the client's own shape check.
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbattg-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gsipk", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = [new KeySchemaElement("gsipk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["kept"] },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: 60);

        var clock = FidelityTime.StartNew();
        Dictionary<string, string> indexStatus = [];
        while (clock.Elapsed < TimeSpan.FromSeconds(PropagateSeconds))
        {
            var described = (await ddb.DescribeTableAsync(table)).Table;
            indexStatus = (described.GlobalSecondaryIndexes ?? []).ToDictionary(i => i.IndexName, i => i.IndexStatus.Value, StringComparer.Ordinal);
            if (indexStatus.GetValueOrDefault(Index) == "ACTIVE")
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(indexStatus.GetValueOrDefault(Index) == "ACTIVE", $"{Index} never became ACTIVE within {PropagateSeconds}s: {Py.Repr(indexStatus)}");

        await ddb.PutItemAsync(table, Item(
            ("pk", S("ITEM#1")),
            ("sk", S("S1")),
            ("gsipk", S("BUCKET#A")),
            ("kept", S("kept-value")),
            ("dropped", S("excluded-value"))));

        Task<QueryResponse> Query(List<string> attributesToGet) => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Index,
            KeyConditions = new Dictionary<string, Condition>(StringComparer.Ordinal)
            {
                ["gsipk"] = new Condition { AttributeValueList = [S("BUCKET#A")], ComparisonOperator = ComparisonOperator.EQ },
            },
            AttributesToGet = attributesToGet,
        });

        // Positive control: the projected attribute alone is served from the index, once it arrives.
        List<Dictionary<string, AttributeValue>> controlItems = [];
        clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(PropagateSeconds))
        {
            controlItems = (await Query(["kept"])).Items;
            if (controlItems.Count > 0)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(
            Py.Equal(controlItems, new[] { Item(("kept", S("kept-value"))) }),
            $"the control AttributesToGet=[\"kept\"] never returned the projected value within {PropagateSeconds}s: {Py.Repr(controlItems)}");

        // An empty AttributesToGet list.
        var (code, message) = await Error(() => Query([]));
        Ensure(code == ErrValidation, $"expected {Py.Repr(ErrValidation)} for an empty list, got {Py.Repr(code)}");
        Ensure(
            message == "1 validation error detected: Value at 'AttributesToGet' failed to satisfy constraint: Member must have length greater than or equal to 1",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");

        // A duplicate of the same (projected) attribute name.
        (code, message) = await Error(() => Query(["kept", "kept"]));
        Ensure(code == ErrValidation, $"expected {Py.Repr(ErrValidation)} for a duplicate name, got {Py.Repr(code)}");
        Ensure(
            message == "One or more parameter values were invalid: Duplicate value in attribute name: kept",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");

        // An attribute the GSI's projection does not carry.
        (code, message) = await Error(() => Query(["dropped"]));
        Ensure(code == ErrValidation, $"expected {Py.Repr(ErrValidation)} for an attribute {Index} does not project, got {Py.Repr(code)}");
        Ensure(
            message == $"One or more parameter values were invalid: Global secondary index {Index} does not project [dropped]",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");

        // None of the three rejected calls wrote anything: the base table still holds only the one item.
        var baseScan = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true });
        Ensure(baseScan.Count == 1, $"expected the one item put above and nothing else, got {Py.Repr(baseScan.Items)}");
    }
}
