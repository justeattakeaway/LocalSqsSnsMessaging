using Amazon;
using Amazon.EventBridge;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;

namespace LocalSqsSnsMessaging.Tests.Verification;
#pragma warning disable CA1724
public static class ClientFactory
#pragma warning restore CA1724
{
    private static readonly CompositeFormat ServiceUrlFormatString = CompositeFormat.Parse("http://localhost:{0}");

    // Opt-in: run the verification suite against real AWS instead of Floci. Uses the default
    // AWS credential chain (env vars, ~/.aws/credentials, etc.). The tests still construct ARNs
    // from `AccountId`, so the verification test classes call `EffectiveAccountId` to substitute
    // the caller's real account when this mode is on. The real account ID must be supplied via
    // the AWS_ACCOUNT_ID env var to avoid pulling in the AWSSDK.SecurityToken package just for
    // this verification path.
    public static bool IsRealAwsMode =>
        string.Equals(Environment.GetEnvironmentVariable("USE_REAL_AWS"), "1", StringComparison.Ordinal);

    private static readonly Lazy<string> RealAccountIdLazy = new(() =>
        Environment.GetEnvironmentVariable("AWS_ACCOUNT_ID")
            ?? throw new InvalidOperationException(
                "USE_REAL_AWS=1 requires AWS_ACCOUNT_ID env var to be set to the calling account."));

    public static string EffectiveAccountId(string randomAccountId)
        => IsRealAwsMode ? RealAccountIdLazy.Value : randomAccountId;

    public static IAmazonSQS CreateSqsClient(string accountId, int? servicePort)
    {
        if (IsRealAwsMode)
        {
            return new AmazonSQSClient(new AmazonSQSConfig { RegionEndpoint = RegionEndpoint.USEast1 });
        }
        return new AmazonSQSClient(
            new BasicAWSCredentials(accountId, "shh"),
            new AmazonSQSConfig
            {
                ServiceURL = string.Format(CultureInfo.InvariantCulture, ServiceUrlFormatString, servicePort!.Value)
            });
    }

    public static IAmazonEventBridge CreateEventBridgeClient(string accountId, int? servicePort)
    {
        if (IsRealAwsMode)
        {
            return new AmazonEventBridgeClient(new AmazonEventBridgeConfig { RegionEndpoint = RegionEndpoint.USEast1 });
        }
        return new AmazonEventBridgeClient(
            new BasicAWSCredentials(accountId, "shh"),
            new AmazonEventBridgeConfig
            {
                ServiceURL = string.Format(CultureInfo.InvariantCulture, ServiceUrlFormatString, servicePort!.Value)
            });
    }

    public static IAmazonSimpleNotificationService CreateSnsClient(string accountId, int? servicePort)
    {
        if (IsRealAwsMode)
        {
            return new AmazonSimpleNotificationServiceClient(
                new AmazonSimpleNotificationServiceConfig { RegionEndpoint = RegionEndpoint.USEast1 });
        }
        return new AmazonSimpleNotificationServiceClient(
            new BasicAWSCredentials(accountId, "shh"),
            new AmazonSimpleNotificationServiceConfig
            {
                ServiceURL = string.Format(CultureInfo.InvariantCulture, ServiceUrlFormatString, servicePort!.Value)
            });
    }
}
