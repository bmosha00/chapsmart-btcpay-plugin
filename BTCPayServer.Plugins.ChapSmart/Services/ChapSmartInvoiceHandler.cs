using System;
using System.Linq;
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
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ChapSmart.Services;

/// <summary>
/// Background service that subscribes to BTCPay's event bus
/// and triggers M-Pesa payout when an invoice is settled.
/// 
/// Case 2 only: The plugin calls /internal/payout, receives a bolt11
/// Lightning invoice, and pays it from the merchant's Lightning wallet.
/// ChapSmart receives the BTC and sends M-Pesa via webhook.
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
        _logger.LogInformation("[ChapSmart] Invoice handler started — listening for settled invoices");
        _subscription = _eventAggregator.SubscribeAsync<InvoiceEvent>(HandleInvoiceEvent);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[ChapSmart] Invoice handler stopped");
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    private async Task HandleInvoiceEvent(InvoiceEvent invoiceEvent)
    {
        // Only handle settled invoices
        if (invoiceEvent.Name != InvoiceEvent.MarkedCompleted &&
            invoiceEvent.Name != InvoiceEvent.Confirmed)
        {
            return;
        }

        try
        {
            var invoice = invoiceEvent.Invoice;
            var storeId = invoice.StoreId;

            // Check if ChapSmart is enabled for this store
            var settings = await _settingsRepository.GetSettings(storeId);
            if (settings == null || !settings.Enabled || !settings.AutoPayout)
            {
                return;
            }

            // Extract M-Pesa metadata from the invoice
            var metadata = invoice.Metadata?.ToJObject();
            if (metadata == null) return;

            var phoneNumber = metadata.Value<string>("phoneNumber");
            var amountTZS = metadata.Value<decimal?>("amountTZS");
            var recipientName = metadata.Value<string>("recipientName") ?? "Customer";

            // If no phone number or amount, this invoice isn't a ChapSmart payout
            if (string.IsNullOrEmpty(phoneNumber) || !amountTZS.HasValue || amountTZS.Value <= 0)
            {
                return;
            }

            _logger.LogInformation(
                "[ChapSmart] Invoice {InvoiceId} settled — processing payout: {Amount} TZS to {Phone}",
                invoice.Id, amountTZS.Value, phoneNumber);

            // Record the payout in our database
            Data.ChapSmartPayout payout = null;
            try
            {
                await using var db = _dbFactory.CreateContext();
                payout = new Data.ChapSmartPayout
                {
                    Id = Guid.NewGuid().ToString(),
                    StoreId = storeId,
                    InvoiceId = invoice.Id,
                    PhoneNumber = phoneNumber,
                    RecipientName = recipientName,
                    AmountTZS = amountTZS.Value,
                    AmountBTC = invoice.Price,
                    Status = "processing",
                    CreatedAt = DateTimeOffset.UtcNow
                };
                await db.Payouts.AddAsync(payout);
                await db.SaveChangesAsync();
            }
            catch (Exception dbEx)
            {
                _logger.LogWarning(dbEx, "[ChapSmart] Could not save payout to DB (table may not exist). Continuing with API call.");
            }

            // Call ChapSmart backend
            var result = await _chapSmartService.TriggerMpesaPayout(
                settings, phoneNumber, amountTZS.Value, recipientName, invoice.Id);

            if (result.AlreadyProcessed)
            {
                // Dedup — same invoiceId was already submitted
                _logger.LogInformation(
                    "[ChapSmart] Already processed (dedup) for invoice {InvoiceId}", invoice.Id);

                await UpdatePayoutStatus(payout, "completed", "Already processed (dedup)", result.ResponseData);
            }
            else if (result.PaymentRequired && !string.IsNullOrEmpty(result.Bolt11))
            {
                // Case 2: Pay the Lightning invoice
                _logger.LogInformation(
                    "[ChapSmart] Paying bolt11: {Sats} sats, payoutId: {PayoutId}, invoice: {InvoiceId}",
                    result.AmountSats, result.PayoutId, invoice.Id);

                await UpdatePayoutStatus(payout, "paying_lightning", null, result.ResponseData);

                var lightningPaySuccess = await PayLightningInvoice(storeId, result.Bolt11);

                if (lightningPaySuccess)
                {
                    _logger.LogInformation(
                        "[ChapSmart] Lightning paid: {Sats} sats for invoice {InvoiceId}. Backend will send M-Pesa via webhook.",
                        result.AmountSats, invoice.Id);

                    await UpdatePayoutStatus(payout, "lightning_paid",
                        $"Paid {result.AmountSats} sats. PayoutId: {result.PayoutId}. Awaiting M-Pesa.", null);
                }
                else
                {
                    _logger.LogWarning(
                        "[ChapSmart] Lightning payment FAILED for invoice {InvoiceId}. Check Lightning wallet balance.",
                        invoice.Id);

                    await UpdatePayoutStatus(payout, "failed",
                        "Lightning payment failed — check wallet balance", null);
                }
            }
            else
            {
                // Error from backend
                _logger.LogWarning(
                    "[ChapSmart] Payout error for invoice {InvoiceId}: {Error}",
                    invoice.Id, result.Message);

                await UpdatePayoutStatus(payout, "failed", result.Message, result.ResponseData);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ChapSmart] Error handling invoice event for {InvoiceId}",
                invoiceEvent.Invoice?.Id);
        }
    }

    /// <summary>
    /// Update payout status in the database. Silently fails if DB is unavailable.
    /// </summary>
    private async Task UpdatePayoutStatus(Data.ChapSmartPayout payout, string status, string errorMessage, string responseData)
    {
        if (payout == null) return;
        try
        {
            await using var db = _dbFactory.CreateContext();
            payout.Status = status;
            if (errorMessage != null) payout.ErrorMessage = errorMessage;
            if (responseData != null) payout.ResponseData = responseData;
            if (status == "completed" || status == "failed")
                payout.CompletedAt = DateTimeOffset.UtcNow;
            db.Payouts.Update(payout);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ChapSmart] Could not update payout status in DB");
        }
    }

    /// <summary>
    /// Pay a bolt11 Lightning invoice using the merchant's BTCPay Lightning wallet.
    /// BTC moves from merchant → ChapSmart to cover the M-Pesa payout.
    /// </summary>
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
