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
/// Handles two cases:
///   Case 1: Invoice created by ChapSmart backend → backend handles payout via webhook
///   Case 2: Invoice created directly in BTCPay → plugin pays Lightning invoice to ChapSmart
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
            var recipientName = metadata.Value<string>("recipientName") ?? "Unknown";

            // If no phone number or amount, this invoice isn't a ChapSmart remittance
            if (string.IsNullOrEmpty(phoneNumber) || !amountTZS.HasValue || amountTZS.Value <= 0)
            {
                return;
            }

            _logger.LogInformation(
                "[ChapSmart] Invoice {InvoiceId} settled — processing payout: {Amount} TZS to {Phone}",
                invoice.Id, amountTZS.Value, phoneNumber);

            // Record the payout in our database
            await using var db = _dbFactory.CreateContext();
            var payout = new Data.ChapSmartPayout
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

            // Call ChapSmart backend
            var result = await _chapSmartService.TriggerMpesaPayout(
                settings, phoneNumber, amountTZS.Value, recipientName, invoice.Id);

            if (result.Success)
            {
                // Case 1: Backend handled it directly (webhook path or already processed)
                payout.Status = "completed";
                payout.CompletedAt = DateTimeOffset.UtcNow;
                payout.ResponseData = result.ResponseData;
                db.Payouts.Update(payout);
                await db.SaveChangesAsync();

                _logger.LogInformation(
                    "[ChapSmart] Case 1 — Payout completed: {Amount} TZS to {Phone} (Invoice: {InvoiceId})",
                    amountTZS.Value, phoneNumber, invoice.Id);
            }
            else if (result.PaymentRequired)
            {
                // Case 2: Backend needs us to pay a Lightning invoice
                _logger.LogInformation(
                    "[ChapSmart] Case 2 — Lightning payment required: {Sats} sats for invoice {InvoiceId}",
                    result.AmountSats, invoice.Id);

                payout.Status = "paying_lightning";
                payout.ResponseData = result.ResponseData;
                db.Payouts.Update(payout);
                await db.SaveChangesAsync();

                // Pay the bolt11 from the merchant's Lightning wallet
                var lightningPayResult = await PayLightningInvoice(storeId, result.Bolt11);

                if (lightningPayResult)
                {
                    payout.Status = "lightning_paid";
                    payout.ResponseData = $"Paid bolt11. PayoutId: {result.PayoutId}. Awaiting M-Pesa confirmation from backend.";
                    db.Payouts.Update(payout);
                    await db.SaveChangesAsync();

                    _logger.LogInformation(
                        "[ChapSmart] Case 2 — Lightning paid: {Sats} sats for invoice {InvoiceId}. Backend will process M-Pesa.",
                        result.AmountSats, invoice.Id);
                }
                else
                {
                    payout.Status = "failed";
                    payout.ErrorMessage = "Failed to pay Lightning invoice — check Lightning wallet balance";
                    payout.CompletedAt = DateTimeOffset.UtcNow;
                    db.Payouts.Update(payout);
                    await db.SaveChangesAsync();

                    _logger.LogWarning(
                        "[ChapSmart] Case 2 — Lightning payment FAILED for invoice {InvoiceId}. Check wallet balance.",
                        invoice.Id);
                }
            }
            else
            {
                // Error from backend
                payout.Status = "failed";
                payout.CompletedAt = DateTimeOffset.UtcNow;
                payout.ResponseData = result.ResponseData;
                payout.ErrorMessage = result.Message;
                db.Payouts.Update(payout);
                await db.SaveChangesAsync();

                _logger.LogWarning(
                    "[ChapSmart] Payout failed: {Error} (Invoice: {InvoiceId})",
                    result.Message, invoice.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ChapSmart] Error handling invoice event for {InvoiceId}",
                invoiceEvent.Invoice?.Id);
        }
    }

    /// <summary>
    /// Pay a bolt11 Lightning invoice using the merchant's BTCPay Lightning wallet.
    /// This sends BTC from the merchant → ChapSmart to cover the M-Pesa payout.
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

            // Get the store's Lightning payment method config
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

            // Create the Lightning client for this store
            var client = paymentMethod.CreateLightningClient(
                network, _lightningOptions.Value, _lightningClientFactory);

            _logger.LogInformation("[ChapSmart] Paying bolt11: {Bolt11}", bolt11[..Math.Min(40, bolt11.Length)] + "...");

            // Pay the invoice
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
