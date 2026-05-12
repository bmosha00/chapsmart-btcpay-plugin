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
            MerchantId = model.Settings.MerchantId?.Trim() ?? "",
            ApiUrl = string.IsNullOrWhiteSpace(model.Settings.ApiUrl)
                ? "https://backend.chapsmart.com"
                : model.Settings.ApiUrl.Trim(),
            Enabled = model.Settings.Enabled,
            AutoCashout = model.Settings.AutoCashout,
            MinCashout = model.Settings.MinCashout > 0 ? model.Settings.MinCashout : 2500m
        };

        await _settingsRepository.SaveSettings(storeId, settings);
        TempData["SuccessMessage"] = "Settings saved!";
        return RedirectToAction(nameof(EditChapSmart), new { storeId });
    }

    [HttpGet("stores/{storeId}/plugins/chapsmart/dashboard")]
    public async Task<IActionResult> Dashboard(string storeId, string status = null)
    {
        List<ChapSmartPayout> payouts = new();
        int total = 0, completed = 0, failed = 0;
        decimal volume = 0;

        try
        {
            await using var db = _dbFactory.CreateContext();
            var query = db.Payouts.Where(p => p.StoreId == storeId);
            if (!string.IsNullOrEmpty(status))
                query = query.Where(p => p.Status == status);

            payouts = await query.OrderByDescending(p => p.CreatedAt).Take(50).ToListAsync();
            total = await db.Payouts.CountAsync(p => p.StoreId == storeId);
            completed = await db.Payouts.CountAsync(p => p.StoreId == storeId && p.Status == "lightning_paid");
            failed = await db.Payouts.CountAsync(p => p.StoreId == storeId && p.Status == "failed");
            volume = await db.Payouts.Where(p => p.StoreId == storeId && p.Status == "lightning_paid")
                .SumAsync(p => (decimal?)p.AmountTZS) ?? 0;
        }
        catch (Exception)
        {
            // Table might not exist yet
        }

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
        try
        {
            await using var db = _dbFactory.CreateContext();
            var payout = await db.Payouts.FirstOrDefaultAsync(p => p.Id == payoutId && p.StoreId == storeId);
            if (payout == null) return NotFound();

            return View(new ChapSmartPayoutDetailViewModel { StoreId = storeId, Payout = payout });
        }
        catch
        {
            return NotFound();
        }
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
