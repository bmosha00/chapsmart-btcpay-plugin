using BTCPayServer.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.ChapSmart.Data;
using BTCPayServer.Plugins.ChapSmart.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.ChapSmart.Controllers;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
public class UIChapSmartController : Controller
{
    private readonly ChapSmartService _chapSmartService;
    private readonly ChapSmartSettingsRepository _settingsRepository;
    private readonly ChapSmartDbContextFactory _dbFactory;

    public UIChapSmartController(
        ChapSmartService chapSmartService,
        ChapSmartSettingsRepository settingsRepository,
        ChapSmartDbContextFactory dbFactory)
    {
        _chapSmartService = chapSmartService;
        _settingsRepository = settingsRepository;
        _dbFactory = dbFactory;
    }

    private StoreData CurrentStore => this.HttpContext.GetStoreData();

    [HttpGet("stores/{storeId}/plugins/chapsmart")]
    public async Task<IActionResult> EditChapSmart()
    {
        var settings = await _settingsRepository.GetSettings(CurrentStore.Id) ?? new ChapSmartSettings();
        return Content(@"
<html><head><title>ChapSmart Settings</title></head>
<body style='font-family: Arial; max-width: 600px; margin: 40px auto;'>
<h2>ChapSmart - M-Pesa Payout Settings</h2>
<p>Plugin is loaded and controller is reachable.</p>
<p>Store ID: " + CurrentStore.Id + @"</p>
<p>Enabled: " + settings.Enabled + @"</p>
<p>API URL: " + (settings.ChapSmartApiUrl ?? "not set") + @"</p>
<p><em>Full settings page coming soon.</em></p>
</body></html>", "text/html");
    }
}

public class ChapSmartSettingsViewModel
{
    public string StoreId { get; set; }
    public ChapSmartSettings Settings { get; set; }
}

public class ChapSmartDashboardViewModel
{
    public string StoreId { get; set; }
    public List<ChapSmartPayout> Payouts { get; set; }
    public ChapSmartDashboardStats Stats { get; set; }
    public string FilterStatus { get; set; }
}

public class ChapSmartDashboardStats
{
    public int TotalPayouts { get; set; }
    public int CompletedPayouts { get; set; }
    public int FailedPayouts { get; set; }
    public decimal TotalAmountTZS { get; set; }
}

public class ChapSmartPayoutDetailViewModel
{
    public string StoreId { get; set; }
    public ChapSmartPayout Payout { get; set; }
}
