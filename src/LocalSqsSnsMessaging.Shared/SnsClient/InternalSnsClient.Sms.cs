using System.Globalization;
using System.Text.RegularExpressions;
using LocalSqsSnsMessaging.Sns.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// SMS: the account's SMS attributes, opted-out numbers and the SMS sandbox. The bus keeps them and
/// answers for them as SNS does, but sends no SMS: a new account is in the sandbox and has no
/// origination numbers, and a sandbox number's one-time password is never revealed.
/// </summary>
internal sealed partial class InternalSnsClient
{
    private static readonly HashSet<string> SmsAttributeNames = new(StringComparer.Ordinal)
    {
        "MonthlySpendLimit", "DeliveryStatusIAMRole", "DeliveryStatusSuccessSamplingRate", "DefaultSenderID",
        "DefaultSMSType", "UsageReportS3Bucket",
    };

    /// <summary>What SNS accepts as a phone number to check or opt in: digits, optionally after a <c>+</c>.</summary>
    private static readonly Regex PhoneNumber = new(@"^\+?[0-9]{1,15}$", RegexOptions.CultureInvariant);

    /// <summary>An E.164 number, as the SMS sandbox (Pinpoint SMS) requires.</summary>
    private static readonly Regex E164 = new(@"^\+[0-9]{2,15}$", RegexOptions.CultureInvariant);

    private const int MaxSandboxPage = 100;

    public Task<GetSMSAttributesResponse> GetSMSAttributesAsync(GetSMSAttributesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requested = request.Attributes ?? [];
        if (requested.FirstOrDefault(name => !SmsAttributeNames.Contains(name)) is { } unknown)
        {
            throw new InternalInvalidParameterException($"Unknown attribute requested: {unknown}");
        }

        var sms = _resources.Sms;
        Dictionary<string, string> attributes;
        lock (sms.Gate)
        {
            attributes = sms.Attributes
                .Where(a => requested.Count == 0 || requested.Contains(a.Key))
                .ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
        }

        _bus.RecordOperation(AwsServiceName.Sns, "GetSMSAttributes");
        return Task.FromResult(new GetSMSAttributesResponse { Attributes = attributes }.SetCommonProperties());
    }

    public Task<SetSMSAttributesResponse> SetSMSAttributesAsync(SetSMSAttributesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requested = request.Attributes ?? [];
        foreach (var (name, value) in requested)
        {
            if (!SmsAttributeNames.Contains(name))
            {
                throw new InternalInvalidParameterException($"{name} is not a valid attribute");
            }

            if (name == "DefaultSMSType" && !string.IsNullOrEmpty(value) && value is not ("Promotional" or "Transactional"))
            {
                throw new InternalInvalidParameterException("DefaultSMSType is invalid");
            }
        }

        var sms = _resources.Sms;
        lock (sms.Gate)
        {
            foreach (var (name, value) in requested)
            {
                // An empty value removes the attribute.
                if (string.IsNullOrEmpty(value))
                {
                    sms.Attributes.Remove(name);
                }
                else
                {
                    sms.Attributes[name] = value;
                }
            }
        }

        _bus.RecordOperation(AwsServiceName.Sns, "SetSMSAttributes");
        return Task.FromResult(new SetSMSAttributesResponse().SetCommonProperties());
    }

    public Task<CheckIfPhoneNumberIsOptedOutResponse> CheckIfPhoneNumberIsOptedOutAsync(CheckIfPhoneNumberIsOptedOutRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var number = ValidPhoneNumber(request.PhoneNumber);
        var sms = _resources.Sms;
        bool optedOut;
        lock (sms.Gate)
        {
            optedOut = sms.OptedOut.Contains(number);
        }

        _bus.RecordOperation(AwsServiceName.Sns, "CheckIfPhoneNumberIsOptedOut");
        return Task.FromResult(new CheckIfPhoneNumberIsOptedOutResponse { IsOptedOut = optedOut }.SetCommonProperties());
    }

    public Task<OptInPhoneNumberResponse> OptInPhoneNumberAsync(OptInPhoneNumberRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var number = ValidPhoneNumber(request.PhoneNumber);
        var sms = _resources.Sms;
        lock (sms.Gate)
        {
            sms.OptedOut.Remove(number);
        }

        _bus.RecordOperation(AwsServiceName.Sns, "OptInPhoneNumber");
        return Task.FromResult(new OptInPhoneNumberResponse().SetCommonProperties());
    }

    public Task<ListPhoneNumbersOptedOutResponse> ListPhoneNumbersOptedOutAsync(ListPhoneNumbersOptedOutRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sms = _resources.Sms;
        List<string> numbers;
        string? nextToken;
        lock (sms.Gate)
        {
            (numbers, nextToken) = SnsPagination.Page(sms.OptedOut.ToList(), request.NextToken);
        }

        _bus.RecordOperation(AwsServiceName.Sns, "ListPhoneNumbersOptedOut");
        return Task.FromResult(new ListPhoneNumbersOptedOutResponse { PhoneNumbers = numbers, NextToken = nextToken }.SetCommonProperties());
    }

    public Task<GetSMSSandboxAccountStatusResponse> GetSMSSandboxAccountStatusAsync(GetSMSSandboxAccountStatusRequest request, CancellationToken cancellationToken = default)
    {
        _bus.RecordOperation(AwsServiceName.Sns, "GetSMSSandboxAccountStatus");
        return Task.FromResult(new GetSMSSandboxAccountStatusResponse { IsInSandbox = true }.SetCommonProperties());
    }

    public Task<ListOriginationNumbersResponse> ListOriginationNumbersAsync(ListOriginationNumbersRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateMaxResults(request.MaxResults, 30);
        _bus.RecordOperation(AwsServiceName.Sns, "ListOriginationNumbers");
        return Task.FromResult(new ListOriginationNumbersResponse { PhoneNumbers = [] }.SetCommonProperties());
    }

    public Task<CreateSMSSandboxPhoneNumberResponse> CreateSMSSandboxPhoneNumberAsync(CreateSMSSandboxPhoneNumberRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // SNS relays the SMS service's refusal, request id and all.
        var number = request.PhoneNumber ?? string.Empty;
        if (!E164.IsMatch(number))
        {
            throw new InternalInvalidParameterException(
                "Invalid parameter: Validation Error Occurred - Reason=\"INVALID_PARAMETER\" Fields=\"destinationPhoneNumber\" " +
                $"(Service: PinpointSmsVoiceV2, Status Code: 400, Request ID: {Guid.NewGuid():D})");
        }

        if (number.StartsWith("+0", StringComparison.Ordinal))
        {
            throw new InternalInvalidParameterException(
                "Invalid parameter: The following parameter is null, empty or invalid: ['destinationPhoneNumber'] " +
                $"(Service: PinpointSmsVoiceV2, Status Code: 400, Request ID: {Guid.NewGuid():D})");
        }

        var sms = _resources.Sms;
        lock (sms.Gate)
        {
            if (!sms.Sandbox.ContainsKey(number))
            {
                // Six digits, never with a leading zero.
                var password = (100_000 + RandomNumber(900_000)).ToString(CultureInfo.InvariantCulture);
                sms.Sandbox[number] = new SnsSmsAccount.SandboxNumber(password);
            }
        }

        _bus.RecordOperation(AwsServiceName.Sns, "CreateSMSSandboxPhoneNumber");
        return Task.FromResult(new CreateSMSSandboxPhoneNumberResponse().SetCommonProperties());
    }

    public Task<VerifySMSSandboxPhoneNumberResponse> VerifySMSSandboxPhoneNumberAsync(VerifySMSSandboxPhoneNumberRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sms = _resources.Sms;
        lock (sms.Gate)
        {
            if (request.PhoneNumber is null || !sms.Sandbox.TryGetValue(request.PhoneNumber, out var entry))
            {
                throw SandboxNumberNotFound();
            }

            if (entry.Status != "Verified")
            {
                if (!string.Equals(entry.OneTimePassword, request.OneTimePassword, StringComparison.Ordinal))
                {
                    throw new AwsServiceException("Verification code has expired, cannot verify the destination phone number.")
                    {
                        ErrorCode = "VerificationException"
                    };
                }

                entry.Status = "Verified";
            }
        }

        _bus.RecordOperation(AwsServiceName.Sns, "VerifySMSSandboxPhoneNumber");
        return Task.FromResult(new VerifySMSSandboxPhoneNumberResponse().SetCommonProperties());
    }

    public Task<DeleteSMSSandboxPhoneNumberResponse> DeleteSMSSandboxPhoneNumberAsync(DeleteSMSSandboxPhoneNumberRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sms = _resources.Sms;
        lock (sms.Gate)
        {
            if (request.PhoneNumber is null || !sms.Sandbox.Remove(request.PhoneNumber))
            {
                throw SandboxNumberNotFound();
            }
        }

        _bus.RecordOperation(AwsServiceName.Sns, "DeleteSMSSandboxPhoneNumber");
        return Task.FromResult(new DeleteSMSSandboxPhoneNumberResponse().SetCommonProperties());
    }

    public Task<ListSMSSandboxPhoneNumbersResponse> ListSMSSandboxPhoneNumbersAsync(ListSMSSandboxPhoneNumbersRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateMaxResults(request.MaxResults, MaxSandboxPage);
        var sms = _resources.Sms;
        List<SMSSandboxPhoneNumber> numbers;
        string? nextToken;
        lock (sms.Gate)
        {
            (var page, nextToken) = SnsPagination.Page(sms.Sandbox.Keys.ToList(), request.NextToken, request.MaxResults ?? MaxSandboxPage);
            numbers = page.Select(n => new SMSSandboxPhoneNumber { PhoneNumber = n, Status = sms.Sandbox[n].Status }).ToList();
        }

        _bus.RecordOperation(AwsServiceName.Sns, "ListSMSSandboxPhoneNumbers");
        return Task.FromResult(new ListSMSSandboxPhoneNumbersResponse { PhoneNumbers = numbers, NextToken = nextToken }.SetCommonProperties());
    }

    /// <summary><paramref name="phoneNumber"/>, or SNS's refusal of a value that isn't a phone number.</summary>
    private static string ValidPhoneNumber(string? phoneNumber) =>
        phoneNumber is not null && PhoneNumber.IsMatch(phoneNumber)
            ? phoneNumber
            : throw new InternalInvalidParameterException("Invalid parameter: PhoneNumber Reason: input incorrectly formatted");

    /// <summary>Refuses a <c>MaxResults</c> outside 1 to <paramref name="maximum"/>, as SNS's request validation words it.</summary>
    private static void ValidateMaxResults(int? maxResults, int maximum)
    {
        var constraint = maxResults switch
        {
            > 0 when maxResults > maximum => $"Member must have value less than or equal to {maximum}",
            < 1 => "Member must have value greater than or equal to 1",
            _ => null,
        };
        if (constraint is not null)
        {
            throw new AwsServiceException(
                $"1 validation error detected: Value '{maxResults}' at 'maxResults' failed to satisfy constraint: {constraint}")
            {
                ErrorCode = "ValidationError"
            };
        }
    }

    private static AwsServiceException SandboxNumberNotFound() =>
        new("Resource does not exist")
        {
            ErrorCode = "ResourceNotFound",
            StatusCode = System.Net.HttpStatusCode.NotFound
        };

    private static int RandomNumber(int exclusiveMaximum)
    {
#if NETSTANDARD2_0
        var bytes = Guid.NewGuid().ToByteArray();
        return (int)(BitConverter.ToUInt32(bytes, 0) % (uint)exclusiveMaximum);
#else
        return System.Security.Cryptography.RandomNumberGenerator.GetInt32(exclusiveMaximum);
#endif
    }
}
