// Translated from behavioral/dynamodb/check_table_arn_addresses_a_table_wherever_a_name_does.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.table-arn-addresses-a-table-wherever-a-name-does</summary>
[Category("AwsEvaluationSuite")]
public sealed class TableArnAddressesATableWhereverANameDoes
{
    private const string CheckId = "table-arn-addresses-a-table-wherever-a-name-does";
    private const string ErrValidation = "ValidationException";

    /// <summary>The AWS error code and message <paramref name="fn"/> raises; an unexpected success fails loudly.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode(Func<Task> fn, string what)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
    }

    /// <summary>
    /// A table's ARN and bare name address one table in TableName: writes by either spelling are read
    /// by the other, and Query, DeleteItem and DescribeTable resolve the ARN; malformed names and ARNs
    /// are still ValidationException and leave the table untouched.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.table-arn-addresses-a-table-wherever-a-name-does")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-arn-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        var arn = (await ddb.DescribeTableAsync(t)).Table.TableArn;
        Ensure(
            arn.EndsWith($":table/{t}", StringComparison.Ordinal),
            $"setup: the ARN must name this table so the substitutions below are meaningful, got {Py.Repr(arn)}");

        // --- write by name, read by ARN ---
        await ddb.PutItemAsync(t, Item(("pk", S("by-name")), ("d", S("written-by-name"))));
        var viaArn = await ddb.GetItemAsync(new GetItemRequest { TableName = arn, Key = Item(("pk", S("by-name"))), ConsistentRead = true });
        Ensure(
            Py.Equal(viaArn.Item, Item(("pk", S("by-name")), ("d", S("written-by-name")))),
            $"an item written by bare name must be readable by ARN -- the two spellings address one table, not two stores. Got {Py.Repr(viaArn.Item)}");

        // --- write by ARN, read by name ---
        await ddb.PutItemAsync(arn, Item(("pk", S("by-arn")), ("d", S("written-by-arn"))));
        var viaName = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("by-arn"))), ConsistentRead = true });
        Ensure(
            Py.Equal(viaName.Item, Item(("pk", S("by-arn")), ("d", S("written-by-arn")))),
            $"an item written by ARN must be readable by bare name, got {Py.Repr(viaName.Item)}");

        // --- Query, DescribeTable and DeleteItem accept the ARN too ---
        var q = await ddb.QueryAsync(new QueryRequest
        {
            TableName = arn,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("by-arn"))),
            ConsistentRead = true,
        });
        Ensure(
            q.Count == 1 && Py.Equal(q.Items[0]["d"], S("written-by-arn")),
            $"Query must accept the ARN and return the item, got {Py.Repr(q.Items)}");
        var desc = (await ddb.DescribeTableAsync(arn)).Table;
        Ensure(
            desc.TableName == t && desc.TableArn == arn,
            $"DescribeTable by ARN must resolve to the same table, got TableName={Py.Repr(desc.TableName)} TableArn={Py.Repr(desc.TableArn)}");
        await ddb.DeleteItemAsync(arn, Item(("pk", S("by-name"))));
        var gone = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("by-name"))), ConsistentRead = true });
        Ensure(
            gone.Item is null,
            $"a DeleteItem addressed by ARN must remove the item the bare name reads, got {Py.Repr(gone.Item)}");

        // --- the grammar is not loosened by accepting the ARN spelling ---
        var segments = arn.Split(':');
        var malformed = new List<(string Why, string Bad)>
        {
            ("a bare name containing '/'", $"{t}/x"),
            ("an ARN with an empty vendor segment", string.Join(':', segments[0], segments[1], "", segments[3], segments[4], segments[5])),
            ("an ARN naming a different resource type", arn.Replace(":table/", ":stream/", StringComparison.Ordinal)),
        };
        var constraint = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a bare name containing '/'"] = "Member must satisfy regular expression pattern: [a-zA-Z0-9_.-]+",
            ["an ARN with an empty vendor segment"] =
                "Valid ARN format is 'arn:<awsPartition>:<vendor>:<region>:<subscriber>:"
                + "resourceType/resourceName', where Resource name must have length less than or equal "
                + "to 255, Resource name must have length greater than or equal to 3, Resource name must "
                + "satisfy regular expression pattern: [a-zA-Z0-9_.-]+",
            ["an ARN naming a different resource type"] = "Invalid resource type",
        };
        foreach (var (why, bad) in malformed)
        {
            var expectedMessage = $"1 validation error detected: Value '{bad}' at 'tableName' failed to satisfy constraint: {constraint[why]}";
            var (code, message) = await ErrorCode(
                () => ddb.GetItemAsync(new GetItemRequest { TableName = bad, Key = Item(("pk", S("by-arn"))) }),
                $"GetItem addressed by {why}");
            Ensure(
                code == ErrValidation,
                $"{why} is not a legal TableName and must be refused {ErrValidation} rather than resolved leniently, got {Py.Repr(code)}");
            Ensure(
                message == expectedMessage,
                $"GetItem addressed by {why}: expected the message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");
            (code, message) = await ErrorCode(
                () => ddb.PutItemAsync(new PutItemRequest { TableName = bad, Item = Item(("pk", S("smuggled"))) }),
                $"PutItem addressed by {why}");
            Ensure(code == ErrValidation, $"{why} must be refused {ErrValidation} on a write too, got {Py.Repr(code)}");
            Ensure(
                message == expectedMessage,
                $"PutItem addressed by {why}: expected the message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");
        }

        // --- effect absent: no refused write reached the table under either spelling ---
        foreach (var (spelling, name) in new[] { ("bare name", t), ("ARN", arn) })
        {
            var smuggled = await ddb.GetItemAsync(new GetItemRequest { TableName = name, Key = Item(("pk", S("smuggled"))), ConsistentRead = true });
            Ensure(
                smuggled.Item is null,
                $"a write refused for a malformed identifier must not land, but reading by {spelling} found {Py.Repr(smuggled.Item)}");
            var scanned = await ddb.ScanAsync(new ScanRequest { TableName = name, ConsistentRead = true, Select = Select.COUNT });
            Ensure(
                scanned.Count == 1,
                $"only the surviving by-arn item may remain; scanning by {spelling} counted {Py.Repr(scanned.Count)}");
        }
    }
}
