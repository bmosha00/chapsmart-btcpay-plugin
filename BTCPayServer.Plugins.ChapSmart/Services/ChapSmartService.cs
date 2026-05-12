using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.ChapSmart.Services;

public class ChapSmartService
{
    private readonly ILogger<ChapSmartService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public ChapSmartService(
        ILogger<ChapSmartService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Call ChapSmart API to trigger M-Pesa payout.
    /// The backend returns one of:
    ///   - paymentRequired: true + bolt11 (plugin must pay the Lightning invoice)
    ///   - alreadyProcessed: true (dedup — same invoiceId was already submitted)
    ///   - error (validation failure, price fetch failure, etc.)
    /// </summary>
    public async Task<ChapSmartPayoutResult> TriggerMpesaPayout(
        ChapSmartSettings settings,
        string phoneNumber,
        decimal amountTZS,
        string recipientName,
        string invoiceId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("ChapSmart");
            client.BaseAddress = new Uri(settings.ChapSmartApiUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Add("X-API-Key", settings.ChapSmartApiKey);
            client.DefaultRequestHeaders.Add("X-API-Secret", settings.ChapSmartApiSecret);

            var payload = new
            {
                phoneNumber,
                amountTZS,
                recipientName,
                invoiceId,
                source = "btcpay-plugin"
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync("api/v1/internal/payout", content);
            var responseBody = await response.Content.ReadAsStringAsync();

            _logger.LogInformation(
                "[ChapSmart] /internal/payout response: {StatusCode} - {Body}",
                response.StatusCode, responseBody);

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                // Check for dedup response
                if (root.TryGetProperty("alreadyProcessed", out var alreadyProcessed) &&
                    alreadyProcessed.GetBoolean())
                {
                    _logger.LogInformation(
                        "[ChapSmart] Already processed (dedup) for invoice {InvoiceId}", invoiceId);

                    return new ChapSmartPayoutResult
                    {
                        AlreadyProcessed = true,
                        Message = "Already processed",
                        ResponseData = responseBody
                    };
                }

                // Check for payment required (Case 2)
                if (root.TryGetProperty("paymentRequired", out var paymentRequired) &&
                    paymentRequired.GetBoolean())
                {
                    var bolt11 = root.GetProperty("bolt11").GetString();
                    var amountSats = root.TryGetProperty("amountSats", out var sats) ? sats.GetInt64() : 0;
                    var payoutId = root.TryGetProperty("payoutId", out var pid) ? pid.GetString() : null;
                    var expiresIn = root.TryGetProperty("expiresIn", out var exp) ? exp.GetInt32() : 600;

                    _logger.LogInformation(
                        "[ChapSmart] Payment required: {Sats} sats bolt11 for invoice {InvoiceId}, payoutId: {PayoutId}",
                        amountSats, invoiceId, payoutId);

                    return new ChapSmartPayoutResult
                    {
                        PaymentRequired = true,
                        Bolt11 = bolt11,
                        AmountSats = amountSats,
                        PayoutId = payoutId,
                        ExpiresIn = expiresIn,
                        Message = "Lightning payment required",
                        ResponseData = responseBody
                    };
                }

                // Unexpected success response
                _logger.LogWarning(
                    "[ChapSmart] Unexpected 200 response for invoice {InvoiceId}: {Body}",
                    invoiceId, responseBody);

                return new ChapSmartPayoutResult
                {
                    Message = "Unexpected response format",
                    ResponseData = responseBody
                };
            }
            else
            {
                // Error response
                var errorMsg = responseBody;
                try
                {
                    using var errDoc = JsonDocument.Parse(responseBody);
                    if (errDoc.RootElement.TryGetProperty("error", out var errProp))
                        errorMsg = errProp.GetString();
                }
                catch { /* use raw body */ }

                _logger.LogWarning(
                    "[ChapSmart] Payout error: {StatusCode} - {Error} for invoice {InvoiceId}",
                    response.StatusCode, errorMsg, invoiceId);

                return new ChapSmartPayoutResult
                {
                    Message = $"{response.StatusCode}: {errorMsg}",
                    ResponseData = responseBody
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ChapSmart] Payout error for invoice {InvoiceId}", invoiceId);
            return new ChapSmartPayoutResult
            {
                Message = ex.Message,
                ResponseData = null
            };
        }
    }

    /// <summary>
    /// Test the connection to ChapSmart API
    /// </summary>
    public async Task<bool> TestConnection(ChapSmartSettings settings)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("ChapSmart");
            client.BaseAddress = new Uri(settings.ChapSmartApiUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Add("X-API-Key", settings.ChapSmartApiKey);
            client.DefaultRequestHeaders.Add("X-API-Secret", settings.ChapSmartApiSecret);

            var response = await client.GetAsync("api/v1/key/info");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

public class ChapSmartPayoutResult
{
    public string Message { get; set; }
    public string ResponseData { get; set; }

    // Dedup
    public bool AlreadyProcessed { get; set; }

    // Case 2: Lightning payment required
    public bool PaymentRequired { get; set; }
    public string Bolt11 { get; set; }
    public long AmountSats { get; set; }
    public string PayoutId { get; set; }
    public int ExpiresIn { get; set; }
}

public class ChapSmartSettings
{
    public string StoreId { get; set; }
    public string ChapSmartApiUrl { get; set; } = "";
    public string ChapSmartApiKey { get; set; }
    public string ChapSmartApiSecret { get; set; }
    public bool Enabled { get; set; }
    public bool AutoPayout { get; set; } = true;
}
