namespace LocalSqsSnsMessaging;

/// <summary>
/// An SNS platform application (mobile push: <c>CreatePlatformApplication</c>) and its endpoints. The
/// bus contacts no push service, so credentials are stored unchecked and nothing is pushed to an
/// endpoint. Every member is read and written under <see cref="Gate"/>.
/// </summary>
internal sealed class SnsPlatformApplication
{
    public SnsPlatformApplication(string arn, string name, string platform, Dictionary<string, string> attributes)
    {
        Arn = arn;
        Name = name;
        Platform = platform;
        Attributes = attributes;
    }

    /// <summary>Guards <see cref="Attributes"/> and <see cref="Endpoints"/>.</summary>
    public object Gate { get; } = new();

    /// <summary><c>arn:aws:sns:{region}:{account}:app/{platform}/{name}</c>.</summary>
    public string Arn { get; }

    public string Name { get; }

    /// <summary><c>GCM</c>, <c>APNS</c>, <c>BAIDU</c>, ...</summary>
    public string Platform { get; }

    /// <summary>Every attribute set, credentials included (<c>GetPlatformApplicationAttributes</c> leaves those out).</summary>
    public Dictionary<string, string> Attributes { get; }

    /// <summary>The application's endpoints, keyed by ARN.</summary>
    public Dictionary<string, SnsPlatformEndpoint> Endpoints { get; } = new(StringComparer.Ordinal);
}

/// <summary>A device registered with a <see cref="SnsPlatformApplication"/> (<c>CreatePlatformEndpoint</c>).</summary>
internal sealed class SnsPlatformEndpoint(string arn, SnsPlatformApplication application, Dictionary<string, string> attributes)
{
    /// <summary><c>arn:aws:sns:{region}:{account}:endpoint/{platform}/{application}/{id}</c>.</summary>
    public string Arn { get; } = arn;

    public SnsPlatformApplication Application { get; } = application;

    /// <summary><c>Token</c>, <c>Enabled</c>, <c>CustomUserData</c> and any platform-specific attributes. Under the application's gate.</summary>
    public Dictionary<string, string> Attributes { get; } = attributes;
}

/// <summary>
/// One region's SMS settings for the account: the attributes <c>SetSMSAttributes</c> sets, the phone
/// numbers opted out of SMS, and the SMS sandbox's destination numbers. The bus sends no SMS. Every
/// member is read and written under <see cref="Gate"/>.
/// </summary>
internal sealed class SnsSmsAccount
{
    public object Gate { get; } = new();

    /// <summary>The account's SMS attributes. A new account has a <c>MonthlySpendLimit</c> of 1 (USD).</summary>
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal)
    {
        ["MonthlySpendLimit"] = "1",
    };

    /// <summary>
    /// Numbers opted out of receiving SMS. A number opts out by replying STOP, which can't happen
    /// here, so only <c>OptInPhoneNumber</c> ever changes it.
    /// </summary>
    public HashSet<string> OptedOut { get; } = new(StringComparer.Ordinal);

    /// <summary>The sandbox's destination numbers, each with its <c>Pending</c> or <c>Verified</c> status and the one-time password it was sent.</summary>
    public Dictionary<string, SandboxNumber> Sandbox { get; } = new(StringComparer.Ordinal);

    internal sealed class SandboxNumber(string oneTimePassword)
    {
        public string Status { get; set; } = "Pending";

        /// <summary>The code SNS would text the number. The bus sends no SMS, so nothing reveals it.</summary>
        public string OneTimePassword { get; } = oneTimePassword;
    }
}
