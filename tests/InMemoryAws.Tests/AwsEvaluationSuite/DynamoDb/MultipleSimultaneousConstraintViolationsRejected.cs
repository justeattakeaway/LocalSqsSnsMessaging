// Translated from behavioral/dynamodb/check_multiple_simultaneous_constraint_violations_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.multiple-simultaneous-constraint-violations-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipleSimultaneousConstraintViolationsRejected
{
    private const string CheckId = "multiple-simultaneous-constraint-violations-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>A poll's timeout: capSeconds, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetSeconds(FidelityContext ctx, double capSeconds) => Math.Max(1.0, Math.Min(capSeconds, ctx.TimeLeftSeconds - 5.0));

    /// <summary>Poll DescribeTable until TableStatus is ACTIVE, bounded by the run's own budget.</summary>
    private static async Task WaitActive(FidelityContext ctx, IAmazonDynamoDB ddb, string table, double capSeconds = 120.0)
    {
        var clock = FidelityTime.StartNew();
        var deadline = BudgetSeconds(ctx, capSeconds);
        while (true)
        {
            var status = (await ddb.DescribeTableAsync(table)).Table.TableStatus.Value;
            if (status == "ACTIVE")
            {
                return;
            }

            if (clock.Elapsed.TotalSeconds >= deadline)
            {
                throw Fail($"{table} did not reach ACTIVE in time (last status {Py.Repr(status)})");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }
    }

    /// <summary>
    /// Several independent constraint violations at once are refused with one
    /// ValidationException naming all of them, in a fixed order, exactly once each --
    /// on scalar members (UpdateItem) and on list elements (CreateTable's GSI/LSI names).
    /// </summary>
    [Test]
    [DisplayName("dynamodb.multiple-simultaneous-constraint-violations-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        // Config(parameter_validation=False): the .NET SDK doesn't validate client-side.
        var ddb = ctx.Dynamo;

        var control = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = control,
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("alt", ScalarAttributeType.S),
            ],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi-ctrl",
                    KeySchema = [new KeySchemaElement("alt", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi-ctrl",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("alt", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(control));
        await WaitActive(ctx, ddb, control);

        // Positive control: a well-formed CreateTable with legal (>=3 char) GSI/LSI names
        // succeeds in this account, and a well-formed UpdateItem on it succeeds too, so the
        // rejections below are attributable to the broken requests, not to a broken account.
        await ddb.PutItemAsync(control, Item(("pk", S("p")), ("sk", S("s")), ("alt", S("1"))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = control,
            Key = Item(("pk", S("p")), ("sk", S("s"))),
            UpdateExpression = "SET val = :v",
            ExpressionAttributeValues = Item((":v", S("updated"))),
        });
        var got = await ddb.GetItemAsync(new GetItemRequest { TableName = control, Key = Item(("pk", S("p")), ("sk", S("s"))), ConsistentRead = true });
        Ensure(Py.Equal(got.Item["val"], S("updated")), $"positive control update not visible: {Py.Repr(got.Item)}");

        // The SDK's analyzer refuses these out-of-range literals at compile time; boto3 with
        // parameter_validation=False sends them, and so does the .NET SDK at run time.
#pragma warning disable DynamoDB1000
        var (code, message) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = "",
            ReturnConsumedCapacity = new ReturnConsumedCapacity("BOGUS"),
            ReturnValues = new ReturnValue("BOGUS2"),
        }));
#pragma warning restore DynamoDB1000
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code}: {Py.Repr(message)}");
        Ensure(
            message == "4 validation errors detected: Value 'BOGUS' at 'returnConsumedCapacity' failed to satisfy constraint: Member must satisfy enum value set: [INDEXES, TOTAL, NONE]; "
                + "Value '' at 'tableName' failed to satisfy constraint: Member must have length greater than or equal to 1; "
                + "Value 'BOGUS2' at 'returnValues' failed to satisfy constraint: Member must satisfy enum value set: [ALL_NEW, UPDATED_OLD, ALL_OLD, NONE, UPDATED_NEW]; "
                + "Value null at 'key' failed to satisfy constraint: Member must not be null",
            $"unexpected message {Py.Repr(message)}");

        var broken = FidelityContext.UniqueName("sim2real-behav-");
#pragma warning disable DynamoDB1000
        (code, message) = await Error(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = broken,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "ab",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "cd",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        }));
#pragma warning restore DynamoDB1000
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code}: {Py.Repr(message)}");
        Ensure(
            message == "2 validation errors detected: Value 'ab' at 'globalSecondaryIndexes.1.member.indexName' failed to satisfy constraint: Member must have length greater than or equal to 3; "
                + "Value 'cd' at 'localSecondaryIndexes.1.member.indexName' failed to satisfy constraint: Member must have length greater than or equal to 3",
            $"unexpected message {Py.Repr(message)}");

        var (describeCode, _) = await Error(() => ddb.DescribeTableAsync(broken));
        Ensure(describeCode == "ResourceNotFoundException", $"the rejected CreateTable must not have created {Py.Repr(broken)}, got {describeCode}");
    }
}
