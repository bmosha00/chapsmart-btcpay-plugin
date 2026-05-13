using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ChapSmart.Services;

/// <summary>
/// Catches BTCPay invoice settlements and triggers ChapSmart cashout.
/// Flow: Invoice settles → call /cashout → verify bolt11 destination → pay bolt11 → merchant gets M-Pesa.
/// </summary>
public class ChapSmartInvoiceHandler : IHostedService
{
    private readonly ILogger<ChapSmartInvoiceHandler> _logger;
    private readonly ChapSmartService _chapSmartService;
    private readonly ChapSmartSettingsRepository _settingsRepository;
    private readonly ChapSmartDbContextFactory _dbFactory;
    private readonly EventAggregator _eventAggregator;
    private readonly LightningClientFactoryService _lightningClientFactory;
    private readonly IOptions<LightningNetworkOptions> _lightningOptions;
    private readonly StoreRepository _storeRepository;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private IEventAggregatorSubscription _subscription;

    // SECURITY: In-memory dedup to prevent race conditions
    private static readonly ConcurrentDictionary<string, byte> _processingInvoices = new();

    // SECURITY: Known ChapSmart Lightning node pubkey(s)
    // Update this when you get the pubkey from: BTCPay Dashboard → Wallets → Lightning → Connection Info
    // Or via API: GET /api/v1/stores/{storeId}/lightning/BTC/info (needs canuselightningnode permission)
    // Leave empty to skip destination verification (NOT RECOMMENDED for production)
    private static readonly string[] TrustedNodePubkeys = new[] { "029fc6217803748cc8fb6b041acbe81e6e295a914487dcea7e5d7e141b64883826" };

    // SECURITY: MerchantId format validation
    private static readonly Regex MerchantIdPattern = new(@"^mch_[a-f0-9]{16}$", RegexOptions.Compiled);

    public ChapSmartInvoiceHandler(
        ILogger<ChapSmartInvoiceHandler> logger,
        ChapSmartService chapSmartService,
        ChapSmartSettingsRepository settingsRepository,
        ChapSmartDbContextFactory dbFactory,
        EventAggregator eventAggregator,
        LightningClientFactoryService lightningClientFactory,
        IOptions<LightningNetworkOptions> lightningOptions,
        StoreRepository storeRepository,
        BTCPayNetworkProvider networkProvider,
        PaymentMethodHandlerDictionary handlers)
    {
        _logger = logger;
        _chapSmartService = chapSmartService;
        _settingsRepository = settingsRepository;
        _dbFactory = dbFactory;
        _eventAggregator = eventAggregator;
        _lightningClientFactory = lightningClientFactory;
        _lightningOptions = lightningOptions;
        _storeRepository = storeRepository;
        _networkProvider = networkProvider;
        _handlers = handlers;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[ChapSmart] Cashout handler started — listening for settled invoices");
        _subscription = _eventAggregator.SubscribeAsync<InvoiceEvent>(HandleInvoiceEvent);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[ChapSmart] Cashout handler stopped");
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    private async Task HandleInvoiceEvent(InvoiceEvent invoiceEvent)
    {
        if (invoiceEvent.Name != InvoiceEvent.MarkedCompleted &&
            invoiceEvent.Name != InvoiceEvent.Confirmed)
        {
            return;
        }

        try
        {
            var invoice = invoiceEvent.Invoice;
            var storeId = invoice.StoreId;

            // Check if ChapSmart cashout is enabled for this store
            var settings = await _settingsRepository.GetSettings(storeId);
            if (settings == null || !settings.Enabled || !settings.AutoCashout)
                return;

            // SECURITY: Validate MerchantId format
            if (string.IsNullOrEmpty(settings.MerchantId))
            {
                _logger.LogWarning("[ChapSmart] No MerchantId configured for store {StoreId}", storeId);
                return;
            }

            if (!MerchantIdPattern.IsMatch(settings.MerchantId))
            {
                _logger.LogWarning("[ChapSmart] Invalid MerchantId format for store {StoreId}: {Id}",
                    storeId, settings.MerchantId);
                return;
            }

            // Determine amountTZS from metadata
            decimal amountTZS = 0;
            var metadata = invoice.Metadata?.ToJObject();
            if (metadata != null)
            {
                var metaAmount = metadata.Value<decimal?>("amountTZS");
                if (metaAmount.HasValue && metaAmount.Value > 0)
                    amountTZS = metaAmount.Value;
            }

            // If no amountTZS in metadata, this invoice isn't a cashout
            if (amountTZS <= 0)
                return;

            // Check minimum
            if (amountTZS < settings.MinCashout)
            {
                _logger.LogInformation(
                    "[ChapSmart] Amount {Amount} TZS below minimum {Min} TZS. Skipping.",
                    amountTZS, settings.MinCashout);
                return;
            }

            // SECURITY: In-memory dedup — prevent race condition with parallel events
            if (!_processingInvoices.TryAdd(invoice.Id, 0))
            {
                _logger.LogInformation(
                    "[ChapSmart] Invoice {InvoiceId} already being processed (in-memory dedup). Skipping.",
                    invoice.Id);
                return;
            }

            try
            {
                await ProcessCashout(invoice, storeId, settings, amountTZS);
            }
            finally
            {
                _processingInvoices.TryRemove(invoice.Id, out _);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ChapSmart] Error handling invoice event for {InvoiceId}",
                invoiceEvent.Invoice?.Id);
        }
    }

    private async Task ProcessCashout(InvoiceEntity invoice, string storeId,
        ChapSmartSettings settings, decimal amountTZS)
    {
        _logger.LogInformation(
            "[ChapSmart] Invoice {InvoiceId} settled — requesting cashout: {Amount} TZS",
            invoice.Id, amountTZS);

        // DB dedup: check if we already processed this invoice
        try
        {
            await using var dbCheck = _dbFactory.CreateContext();
            var existingPayout = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .FirstOrDefaultAsync(dbCheck.Payouts, p => p.InvoiceId == invoice.Id);
            if (existingPayout != null && existingPayout.Status != "failed")
            {
                _logger.LogInformation(
                    "[ChapSmart] Invoice {InvoiceId} already processed (status: {Status}). Skipping.",
                    invoice.Id, existingPayout.Status);
                return;
            }
        }
        catch (Exception dbEx)
        {
            _logger.LogWarning(dbEx, "[ChapSmart] Could not check dedup in DB. Continuing.");
        }

        // Record the cashout attempt
        Data.ChapSmartPayout payout = null;
        try
        {
            await using var db = _dbFactory.CreateContext();
            payout = new Data.ChapSmartPayout
            {
                Id = Guid.NewGuid().ToString(),
                StoreId = storeId,
                InvoiceId = invoice.Id,
                PhoneNumber = settings.MerchantId,
                RecipientName = "Merchant Cashout",
                AmountTZS = amountTZS,
                AmountBTC = invoice.Price,
                Status = "processing",
                CreatedAt = DateTimeOffset.UtcNow
            };
            await db.Payouts.AddAsync(payout);
            await db.SaveChangesAsync();
        }
        catch (Exception dbEx)
        {
            _logger.LogWarning(dbEx, "[ChapSmart] Could not save payout to DB. Continuing with API call.");
        }

        // Call ChapSmart cashout API
        var result = await _chapSmartService.RequestCashout(
            settings.ApiUrl, settings.MerchantId, amountTZS);

        if (!result.Success || string.IsNullOrEmpty(result.Bolt11))
        {
            _logger.LogWarning(
                "[ChapSmart] Cashout API error for invoice {InvoiceId}: {Error}",
                invoice.Id, result.Message);
            await UpdatePayoutStatus(payout, "failed", result.Message, result.ResponseData, null);
            return;
        }

        _logger.LogInformation(
            "[ChapSmart] Cashout ready: {Sats} sats, cashoutId: {CashoutId}, invoice: {InvoiceId}",
            result.AmountSats, result.CashoutId, invoice.Id);

        // SECURITY: Verify bolt11 destination before paying
        if (!VerifyBolt11Destination(result.Bolt11))
        {
            _logger.LogError(
                "[ChapSmart] BLOCKED: bolt11 destination verification failed for invoice {InvoiceId}. " +
                "The Lightning invoice may not belong to ChapSmart.",
                invoice.Id);
            await UpdatePayoutStatus(payout, "failed",
                "Security: bolt11 destination verification failed", result.ResponseData, result.CashoutId);
            return;
        }

        await UpdatePayoutStatus(payout, "paying_lightning", null, result.ResponseData, result.CashoutId);

        // Pay the bolt11 from merchant's Lightning wallet
        var lightningSuccess = await PayLightningInvoice(storeId, result.Bolt11);

        if (lightningSuccess)
        {
            _logger.LogInformation(
                "[ChapSmart] ✅ Lightning paid: {Sats} sats for invoice {InvoiceId}. " +
                "CashoutId: {CashoutId}. Backend will send M-Pesa.",
                result.AmountSats, invoice.Id, result.CashoutId);

            await UpdatePayoutStatus(payout, "lightning_paid",
                $"Paid {result.AmountSats} sats. CashoutId: {result.CashoutId}. Awaiting M-Pesa.", null, result.CashoutId);
        }
        else
        {
            _logger.LogWarning(
                "[ChapSmart] ❌ Lightning payment FAILED for invoice {InvoiceId}. Check wallet balance.",
                invoice.Id);

            await UpdatePayoutStatus(payout, "failed",
                "Lightning payment failed — check wallet balance", null, result.CashoutId);
        }
    }

    /// <summary>
    /// SECURITY: Verify the bolt11 Lightning invoice points to a trusted ChapSmart node.
    /// If TrustedNodePubkeys is empty, verification is skipped with a warning.
    /// </summary>
    private bool VerifyBolt11Destination(string bolt11)
    {
        if (TrustedNodePubkeys.Length == 0)
        {
            _logger.LogWarning(
                "[ChapSmart] bolt11 destination verification SKIPPED — no trusted pubkeys configured. " +
                "Add ChapSmart's node pubkey to TrustedNodePubkeys for production security.");
            return true; // Allow but warn
        }

        try
        {
            var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
            if (network == null) return false;

            var decoded = BOLT11PaymentRequest.Parse(bolt11, network.NBitcoinNetwork);
            var payeePubkey = decoded.GetPayeePubKey()?.ToString();

            if (string.IsNullOrEmpty(payeePubkey))
            {
                _logger.LogError("[ChapSmart] Could not extract payee pubkey from bolt11");
                return false;
            }

            foreach (var trusted in TrustedNodePubkeys)
            {
                if (payeePubkey.Equals(trusted, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("[ChapSmart] bolt11 destination verified: trusted ChapSmart node");
                    return true;
                }
            }

            _logger.LogError(
                "[ChapSmart] UNTRUSTED bolt11 destination: {Pubkey}. Expected one of ChapSmart's nodes.",
                payeePubkey[..Math.Min(16, payeePubkey.Length)] + "...");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ChapSmart] Failed to decode bolt11 for verification");
            return false;
        }
    }

    private async Task UpdatePayoutStatus(Data.ChapSmartPayout payout, string status,
        string errorMessage, string responseData, string cashoutId)
    {
        if (payout == null) return;
        try
        {
            await using var db = _dbFactory.CreateContext();
            payout.Status = status;
            if (errorMessage != null) payout.ErrorMessage = errorMessage;
            if (responseData != null) payout.ResponseData = responseData;
            if (cashoutId != null) payout.PaymentProviderTransId = cashoutId;
            if (status is "completed" or "failed")
                payout.CompletedAt = DateTimeOffset.UtcNow;
            db.Payouts.Update(payout);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ChapSmart] Could not update payout status in DB");
        }
    }

    private async Task<bool> PayLightningInvoice(string storeId, string bolt11)
    {
        try
        {
            var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
            if (network == null)
            {
                _logger.LogError("[ChapSmart] BTC network not found");
                return false;
            }

            var store = await _storeRepository.FindStore(storeId);
            if (store == null)
            {
                _logger.LogError("[ChapSmart] Store {StoreId} not found", storeId);
                return false;
            }

            var pmi = PaymentTypes.LN.GetPaymentMethodId("BTC");
            var paymentMethod = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(pmi, _handlers);
            if (paymentMethod == null)
            {
                _logger.LogError("[ChapSmart] Lightning not configured for store {StoreId}", storeId);
                return false;
            }

            var client = paymentMethod.CreateLightningClient(
                network, _lightningOptions.Value, _lightningClientFactory);

            _logger.LogInformation("[ChapSmart] Sending Lightning payment: {Bolt11}",
                bolt11[..Math.Min(40, bolt11.Length)] + "...");

            var payResponse = await client.Pay(bolt11, new PayInvoiceParams());

            if (payResponse.Result == PayResult.Ok)
            {
                _logger.LogInformation("[ChapSmart] Lightning payment successful");
                return true;
            }
            else
            {
                _logger.LogWarning("[ChapSmart] Lightning payment result: {Result} — {Details}",
                    payResponse.Result, payResponse.ErrorDetail);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ChapSmart] Lightning payment error");
            return false;
        }
    }
}
