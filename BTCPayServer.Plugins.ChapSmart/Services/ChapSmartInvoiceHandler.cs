using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Events;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ChapSmart.Services;

/// <summary>
/// Background service that subscribes to BTCPay's event bus
/// and triggers M-Pesa payout when an invoice is settled
/// </summary>
public class ChapSmartInvoiceHandler : IHostedService
{
    private readonly ILogger<ChapSmartInvoiceHandler> _logger;
    private readonly ChapSmartService _chapSmartService;
    private readonly ChapSmartSettingsRepository _settingsRepository;
    private readonly ChapSmartDbContextFactory _dbFactory;
    private readonly EventAggregator _eventAggregator;
    private IEventAggregatorSubscription _subscription;

    public ChapSmartInvoiceHandler(
        ILogger<ChapSmartInvoiceHandler> logger,
        ChapSmartService chapSmartService,
        ChapSmartSettingsRepository settingsRepository,
        ChapSmartDbContextFactory dbFactory,
        EventAggregator eventAggregator)
    {
        _logger = logger;
        _chapSmartService = chapSmartService;
        _settingsRepository = settingsRepository;
        _dbFactory = dbFactory;
        _eventAggregator = eventAggregator;
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
        // Only handle settled invoices (payment confirmed)
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
            if (metadata == null)
            {
                return;
            }

            var phoneNumber = metadata.Value<string>("phoneNumber");
            var amountTZS = metadata.Value<decimal?>("amountTZS");
            var recipientName = metadata.Value<string>("recipientName") ?? "Unknown";

            // If no phone number or amount, this invoice isn't a ChapSmart remittance
            if (string.IsNullOrEmpty(phoneNumber) || !amountTZS.HasValue || amountTZS.Value <= 0)
            {
                return;
            }

            _logger.LogInformation(
                "[ChapSmart] Invoice {InvoiceId} settled — processing M-Pesa payout: {Amount} TZS to {Phone}",
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

            // Trigger the M-Pesa payout via ChapSmart API
            var result = await _chapSmartService.TriggerMpesaPayout(
                settings, phoneNumber, amountTZS.Value, recipientName, invoice.Id);

            // Update payout status
            payout.Status = result.Success ? "completed" : "failed";
            payout.CompletedAt = DateTimeOffset.UtcNow;
            payout.ResponseData = result.ResponseData;
            payout.ErrorMessage = result.Success ? null : result.Message;
            db.Payouts.Update(payout);
            await db.SaveChangesAsync();

            if (result.Success)
            {
                _logger.LogInformation(
                    "[ChapSmart] ✅ Payout completed: {Amount} TZS to {Phone} (Invoice: {InvoiceId})",
                    amountTZS.Value, phoneNumber, invoice.Id);
            }
            else
            {
                _logger.LogWarning(
                    "[ChapSmart] ❌ Payout failed: {Error} (Invoice: {InvoiceId})",
                    result.Message, invoice.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ChapSmart] Error handling invoice event for {InvoiceId}",
                invoiceEvent.Invoice?.Id);
        }
    }
}
