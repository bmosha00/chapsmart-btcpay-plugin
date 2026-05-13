using System;
using System.Collections.Concurrent;
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

    // Allowed API hosts — only these domains can receive cashout requests
    private static readonly string[] AllowedHosts = new[]
    {
        "backend.chapsmart.com",
        "staging.chapsmart.com",
        "localhost"
    };

    public ChapSmartService(
        ILogger<ChapSmartService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Call ChapSmart cashout API. No authentication required.
    /// Returns a bolt11 Lightning invoice to pay, or an error.
    /// </summary>
    public async Task<CashoutResult> RequestCashout(string apiUrl, string merchantId, decimal amountTZS)
    {
        try
        {
            var baseUrl = (apiUrl ?? "https://backend.chapsmart.com").TrimEnd('/');

            // SECURITY: Enforce HTTPS (allow localhost for testing)
            if (!baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !baseUrl.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) &&
                !baseUrl.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("[ChapSmart] BLOCKED: HTTPS required. Got: {Url}", baseUrl);
                return new CashoutResult { Success = false, Message = "HTTPS required for API URL" };
            }

            // SECURITY: Only allow requests to known ChapSmart hosts
            var uri = new Uri(baseUrl);
            if (!Array.Exists(AllowedHosts, host =>
                    uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogError("[ChapSmart] BLOCKED: Unauthorized API host: {Host}", uri.Host);
                return new CashoutResult { Success = false, Message = "Unauthorized API host" };
            }

            var client = _httpClientFactory.CreateClient("ChapSmart");

            var payload = new { merchantId, amountTZS };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation(
                "[ChapSmart] Requesting cashout: {Amount} TZS for merchant {MerchantId}",
                amountTZS, merchantId);

            var response = await client.PostAsync($"{baseUrl}/api/v1/cashout", content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                var bolt11 = root.TryGetProperty("bolt11", out var b) ? b.GetString() : null;
                if (string.IsNullOrEmpty(bolt11))
                {
                    _logger.LogWarning("[ChapSmart] No bolt11 in response");
                    return new CashoutResult
                    {
                        Success = false,
                        Message = "No bolt11 in response",
                        ResponseData = responseBody
                    };
                }

                var amountSats = root.TryGetProperty("amountSats", out var s) ? s.GetInt64() : 0;
                var cashoutId = root.TryGetProperty("cashoutId", out var c) ? c.GetString() : null;
                var expiresIn = root.TryGetProperty("expiresIn", out var e) ? e.GetInt32() : 600;
                var merchantName = root.TryGetProperty("merchantName", out var m) ? m.GetString() : null;
                var returnedTZS = root.TryGetProperty("amountTZS", out var t) ? t.GetDecimal() : amountTZS;

                // SECURITY: Log only non-sensitive fields
                _logger.LogInformation(
                    "[ChapSmart] Cashout response: OK — cashoutId: {CashoutId}, sats: {Sats}, TZS: {TZS}",
                    cashoutId, amountSats, returnedTZS);

                return new CashoutResult
                {
                    Success = true,
                    Bolt11 = bolt11,
                    AmountSats = amountSats,
                    AmountTZS = returnedTZS,
                    CashoutId = cashoutId,
                    ExpiresIn = expiresIn,
                    MerchantName = merchantName,
                    Message = "Cashout ready",
                    ResponseData = responseBody
                };
            }
            else
            {
                var errorMsg = responseBody;
                try
                {
                    using var errDoc = JsonDocument.Parse(responseBody);
                    if (errDoc.RootElement.TryGetProperty("error", out var errProp))
                        errorMsg = errProp.GetString();
                }
                catch { }

                _logger.LogWarning(
                    "[ChapSmart] Cashout error: {StatusCode} - {Error}",
                    response.StatusCode, errorMsg);

                return new CashoutResult
                {
                    Success = false,
                    Message = $"{response.StatusCode}: {errorMsg}",
                    ResponseData = responseBody
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ChapSmart] Cashout request failed");
            return new CashoutResult
            {
                Success = false,
                Message = ex.Message
            };
        }
    }

    /// <summary>
    /// Check cashout status (optional, for dashboard polling)
    /// </summary>
    public async Task<string> CheckCashoutStatus(string apiUrl, string cashoutId)
    {
        try
        {
            var baseUrl = (apiUrl ?? "https://backend.chapsmart.com").TrimEnd('/');
            var client = _httpClientFactory.CreateClient("ChapSmart");
            var response = await client.GetAsync($"{baseUrl}/api/v1/cashout/status/{cashoutId}");
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ChapSmart] Status check failed for {CashoutId}", cashoutId);
            return null;
        }
    }
}

public class CashoutResult
{
    public bool Success { get; set; }
    public string Message { get; set; }
    public string ResponseData { get; set; }

    public string Bolt11 { get; set; }
    public long AmountSats { get; set; }
    public decimal AmountTZS { get; set; }
    public string CashoutId { get; set; }
    public int ExpiresIn { get; set; }
    public string MerchantName { get; set; }
}

public class ChapSmartSettings
{
    public string StoreId { get; set; }
    public string MerchantId { get; set; } = "";
    public string ApiUrl { get; set; } = "https://backend.chapsmart.com";
    public bool Enabled { get; set; }
    public bool AutoCashout { get; set; } = true;
    public decimal MinCashout { get; set; } = 2500m;
}
