using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SuperSurveyor;

internal sealed class FreeMobileSms(HttpClient client)
{
    internal async Task<SmsOutcome> Send(Settings settings, string message, CancellationToken cancellationToken)
    {
        var payload = new FreeMobilePayload(settings.SubscriberId, settings.SmsKey, message);
        using var content = JsonContent.Create(payload, SurveyorJson.Default.FreeMobilePayload);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://smsapi.free-mobile.fr/sendmsg");
        request.Content = content;

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            var result = status switch
            {
                200 => SmsOutcome.Accepted,
                401 or 403 => SmsOutcome.CredentialsRejected,
                402 or 408 or 429 or >= 500 => SmsOutcome.TemporaryFailure,
                _ => SmsOutcome.RequestRejected
            };
            return result;
        }
        catch (HttpRequestException)
        {
            return SmsOutcome.TemporaryFailure;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SmsOutcome.TemporaryFailure;
        }
    }
}

internal enum SmsOutcome
{
    Accepted,
    TemporaryFailure,
    CredentialsRejected,
    RequestRejected
}

internal sealed record FreeMobilePayload(
    [property: JsonPropertyName("user")] string SubscriberId,
    [property: JsonPropertyName("pass")] string SmsKey,
    [property: JsonPropertyName("msg")] string Message);
