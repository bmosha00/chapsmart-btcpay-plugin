using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BTCPayServer.Plugins.ChapSmart.Data;

public class ChapSmartPayout
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public string Id { get; set; }

    [Required]
    public string StoreId { get; set; }

    [Required]
    public string InvoiceId { get; set; }

    [Required]
    public string PhoneNumber { get; set; }

    public string RecipientName { get; set; }

    public decimal AmountTZS { get; set; }

    public decimal AmountBTC { get; set; }

    /// <summary>
    /// Status: processing, completed, failed, retrying
    /// </summary>
    [Required]
    public string Status { get; set; } = "processing";

    public string PaymentProviderTransId { get; set; }

    public string ErrorMessage { get; set; }

    public string ResponseData { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public int RetryCount { get; set; } = 0;
}
