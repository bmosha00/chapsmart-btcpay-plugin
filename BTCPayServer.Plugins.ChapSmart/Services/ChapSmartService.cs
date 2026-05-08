using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
    /// Call ChapSmart API to trigger M-Pesa payout
    /// This is the Phase A bridge — calls the existing Node.js backend
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

            // Call ChapSmart internal payout endpoint
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

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "[ChapSmart] M-Pesa payout triggered: {Amount} TZS to {Phone} for invoice {InvoiceId}",
                    amountTZS, phoneNumber, invoiceId);

                return new ChapSmartPayoutResult
                {
                    Success = true,
                    Message = "Payout initiated",
                    ResponseData = responseBody
                };
            }
            else
            {
                _logger.LogWarning(
                    "[ChapSmart] Payout failed: {StatusCode} - {Response} for invoice {InvoiceId}",
                    response.StatusCode, responseBody, invoiceId);

                return new ChapSmartPayoutResult
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    ResponseData = responseBody
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ChapSmart] Payout error for invoice {InvoiceId}", invoiceId);
            return new ChapSmartPayoutResult
            {
                Success = false,
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
    public bool Success { get; set; }
    public string Message { get; set; }
    public string ResponseData { get; set; }
}

public class ChapSmartSettings
{
    public string StoreId { get; set; }
    public string ChapSmartApiUrl { get; set; } = "";
    public string ChapSmartApiKey { get; set; }
    public string ChapSmartApiSecret { get; set; }
    public decimal FeePercent { get; set; } = 2.2m;
    public decimal UsdToTzsRate { get; set; } = 2520m;
    public bool Enabled { get; set; }
    public bool AutoPayout { get; set; } = true;
    public decimal DailyLimit { get; set; } = 1000000m; // 1M TZS daily limit
}
