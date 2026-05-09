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

    [HttpGet("stores/{storeId}/plugins/chapsmart/logo.png")]
    [AllowAnonymous]
    public IActionResult Logo()
    {
        var assembly = typeof(Plugin).Assembly;
        var stream = assembly.GetManifestResourceStream("BTCPayServer.Plugins.ChapSmart.Resources.img.chapsmart-logo.png");
        if (stream == null) return NotFound();
        return File(stream, "image/png");
    }

    [HttpGet("stores/{storeId}/plugins/chapsmart")]
    public async Task<IActionResult> EditChapSmart()
    {
        var settings = await _settingsRepository.GetSettings(CurrentStore.Id) ?? new ChapSmartSettings();
        var vm = new ChapSmartSettingsViewModel
        {
            StoreId = CurrentStore.Id,
            Settings = settings
        };

        if (TempData["SuccessMessage"] is string success)
            ViewBag.SuccessMessage = success;
        if (TempData["ErrorMessage"] is string error)
            ViewBag.ErrorMessage = error;

        return View(vm);
    }

    [HttpPost("stores/{storeId}/plugins/chapsmart")]
    public async Task<IActionResult> SaveSettings(string storeId, ChapSmartSettingsViewModel model)
    {
        var settings = new ChapSmartSettings
        {
            StoreId = storeId,
            Enabled = model.Settings.Enabled,
            AutoPayout = model.Settings.AutoPayout,
            ChapSmartApiUrl = model.Settings.ChapSmartApiUrl ?? "",
            ChapSmartApiKey = model.Settings.ChapSmartApiKey ?? "",
            ChapSmartApiSecret = model.Settings.ChapSmartApiSecret ?? "",
            FeePercent = model.Settings.FeePercent,
            UsdToTzsRate = model.Settings.UsdToTzsRate,
            DailyLimit = model.Settings.DailyLimit
        };

        await _settingsRepository.SaveSettings(storeId, settings);
        TempData["SuccessMessage"] = "Settings saved successfully!";
        return RedirectToAction(nameof(EditChapSmart), new { storeId });
    }

    [HttpPost("stores/{storeId}/plugins/chapsmart/test")]
    public async Task<IActionResult> TestConnection(string storeId)
    {
        var settings = await _settingsRepository.GetSettings(storeId);
        if (settings == null || string.IsNullOrEmpty(settings.ChapSmartApiKey))
        {
            TempData["ErrorMessage"] = "Please save API credentials first.";
            return RedirectToAction(nameof(EditChapSmart), new { storeId });
        }

        var success = await _chapSmartService.TestConnection(settings);
        TempData[success ? "SuccessMessage" : "ErrorMessage"] =
            success ? "Connected to ChapSmart API successfully!" : "Connection failed. Check your credentials.";
        return RedirectToAction(nameof(EditChapSmart), new { storeId });
    }

    [HttpGet("stores/{storeId}/plugins/chapsmart/dashboard")]
    public async Task<IActionResult> Dashboard(string storeId, string status = null)
    {
        await using var db = _dbFactory.CreateContext();
        var query = db.Payouts.Where(p => p.StoreId == storeId);
        if (!string.IsNullOrEmpty(status))
            query = query.Where(p => p.Status == status);

        var payouts = await query.OrderByDescending(p => p.CreatedAt).Take(50).ToListAsync();
        var total = await db.Payouts.CountAsync(p => p.StoreId == storeId);
        var completed = await db.Payouts.CountAsync(p => p.StoreId == storeId && p.Status == "completed");
        var failed = await db.Payouts.CountAsync(p => p.StoreId == storeId && p.Status == "failed");
        var volume = await db.Payouts.Where(p => p.StoreId == storeId && p.Status == "completed")
            .SumAsync(p => (decimal?)p.AmountTZS) ?? 0;

        var vm = new ChapSmartDashboardViewModel
        {
            StoreId = storeId,
            Payouts = payouts,
            FilterStatus = status,
            Stats = new ChapSmartDashboardStats
            {
                TotalPayouts = total,
                CompletedPayouts = completed,
                FailedPayouts = failed,
                TotalAmountTZS = volume
            }
        };

        return View(vm);
    }

    [HttpGet("stores/{storeId}/plugins/chapsmart/payout/{payoutId}")]
    public async Task<IActionResult> PayoutDetail(string storeId, string payoutId)
    {
        await using var db = _dbFactory.CreateContext();
        var payout = await db.Payouts.FirstOrDefaultAsync(p => p.Id == payoutId && p.StoreId == storeId);
        if (payout == null) return NotFound();

        var vm = new ChapSmartPayoutDetailViewModel
        {
            StoreId = storeId,
            Payout = payout
        };

        return View(vm);
    }
}

public class ChapSmartSettingsViewModel
{
    public string StoreId { get; set; }
    public ChapSmartSettings Settings { get; set; } = new();
}

public class ChapSmartDashboardViewModel
{
    public string StoreId { get; set; }
    public List<ChapSmartPayout> Payouts { get; set; } = new();
    public ChapSmartDashboardStats Stats { get; set; } = new();
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
