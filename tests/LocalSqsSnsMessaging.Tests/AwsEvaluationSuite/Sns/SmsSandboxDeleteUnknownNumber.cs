// Translated from behavioral/sns/check_sms_sandbox_delete_unknown_number.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.sms-sandbox-delete-unknown-number</summary>
[Category("AwsEvaluationSuite")]
public sealed class SmsSandboxDeleteUnknownNumber
{
    /// <summary>
    /// DeleteSMSSandboxPhoneNumber on a never-added number raises ResourceNotFound "Resource does not exist";
    /// a number that lands in the sandbox list deletes, disappears, and a second delete gets the same error,
    /// while a Create that lands nothing must be the UserError "No origination entities available to send".
    /// </summary>
    [Test]
    [DisplayName("sns.sms-sandbox-delete-unknown-number")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var suffix = Random.Shared.Next(10000);
        var knownNumber = $"+1500555{suffix:D4}";
        var neverAddedNumber = $"+1500556{suffix:D4}";

        var listed = await ListedNumbers(sns);
        if (listed.Contains(knownNumber) || listed.Contains(neverAddedNumber))
        {
            throw Fail($"test numbers already present in the sandbox list: {Py.Repr(listed)}");
        }

        ctx.OnCleanup(() => BestEffortDelete(sns, knownNumber));

        (string? Code, string? Message)? createError = null;
        var error = await FidelityContext.CaptureError(() =>
            sns.CreateSMSSandboxPhoneNumberAsync(new CreateSMSSandboxPhoneNumberRequest { PhoneNumber = knownNumber }));
        if (error is not null)
        {
            var (createCode, createMessage) = (error.Code, error.Message ?? "");
            if (createCode == "UserError" && createMessage.Contains("no origination entities available", StringComparison.OrdinalIgnoreCase))
            {
                createError = (createCode, createMessage);
            }
            else
            {
                throw error.Exception;
            }
        }

        var landed = (await ListedNumbers(sns)).Contains(knownNumber);
        if (landed)
        {
            // Without an origination entity, Create raises a UserError yet AWS still enqueues the
            // Pending sandbox entry, so the positive control keys off the list, not off Create.
            await sns.DeleteSMSSandboxPhoneNumberAsync(new DeleteSMSSandboxPhoneNumberRequest { PhoneNumber = knownNumber });
            var afterDelete = await ListedNumbers(sns);
            Ensure(!afterDelete.Contains(knownNumber),
                $"{knownNumber} still present in sandbox list after delete");

            // Negative, same identifier: once removed from the sandbox, delete must be rejected.
            var (code, message) = await DeleteError(sns, knownNumber);
            Ensure(code == "ResourceNotFound",
                $"expected ResourceNotFound re-deleting a number just removed from the sandbox, got {Py.Repr(code)}");
            Ensure(message == "Resource does not exist",
                $"unexpected ResourceNotFound message re-deleting {knownNumber}: {Py.Repr(message)}");
        }
        else if (createError is not null)
        {
            // Create had no effect: it must be the documented origination-entity refusal.
            Ensure(createError == ("UserError", "No origination entities available to send"),
                $"unexpected CreateSMSSandboxPhoneNumber refusal: {Py.Repr(new object?[] { createError.Value.Code, createError.Value.Message })}");
        }
        else
        {
            throw Fail($"{knownNumber} missing from sandbox list after a Create that raised no error");
        }

        // Negative, never-added identifier: the same specific error, not a family of failures,
        // and unaffected by the origination-entity gate (delete needs no origination entity).
        var (neverCode, neverMessage) = await DeleteError(sns, neverAddedNumber);
        Ensure(neverCode == "ResourceNotFound",
            $"expected ResourceNotFound deleting a number that was never added to the sandbox, got {Py.Repr(neverCode)}");
        Ensure(neverMessage == "Resource does not exist",
            $"unexpected ResourceNotFound message deleting {neverAddedNumber}: {Py.Repr(neverMessage)}");
    }

    private static async Task<HashSet<string>> ListedNumbers(AmazonSimpleNotificationServiceClient sns)
    {
        var numbers = new HashSet<string>(StringComparer.Ordinal);
        string? nextToken = null;
        while (true)
        {
            var resp = await sns.ListSMSSandboxPhoneNumbersAsync(new ListSMSSandboxPhoneNumbersRequest { NextToken = nextToken });
            numbers.UnionWith((resp.PhoneNumbers ?? []).Select(n => n.PhoneNumber));
            nextToken = resp.NextToken;
            if (string.IsNullOrEmpty(nextToken))
            {
                return numbers;
            }
        }
    }

    private static async Task<(string? Code, string? Message)> DeleteError(AmazonSimpleNotificationServiceClient sns, string phoneNumber)
    {
        var error = await FidelityContext.CaptureError(() =>
            sns.DeleteSMSSandboxPhoneNumberAsync(new DeleteSMSSandboxPhoneNumberRequest { PhoneNumber = phoneNumber }));
        return error is null
            ? throw Fail($"delete of {phoneNumber} unexpectedly succeeded")
            : (error.Code, error.Message);
    }

    private static async Task BestEffortDelete(AmazonSimpleNotificationServiceClient sns, string phoneNumber)
    {
        try
        {
            await sns.DeleteSMSSandboxPhoneNumberAsync(new DeleteSMSSandboxPhoneNumberRequest { PhoneNumber = phoneNumber });
        }
        catch (AmazonSimpleNotificationServiceException ex) when (ex.ErrorCode == "ResourceNotFound")
        {
        }
    }
}
