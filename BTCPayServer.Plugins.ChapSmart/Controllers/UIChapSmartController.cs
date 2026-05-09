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
        var s = await _settingsRepository.GetSettings(CurrentStore.Id) ?? new ChapSmartSettings();
        var msg = TempData["SuccessMessage"] as string;
        var err = TempData["ErrorMessage"] as string;

        return Content(SettingsHtml(s, msg, err), "text/html");
    }

    [HttpPost("stores/{storeId}/plugins/chapsmart")]
    public async Task<IActionResult> SaveSettings(string storeId,
        bool Enabled, bool AutoPayout, string ChapSmartApiUrl,
        string ChapSmartApiKey, string ChapSmartApiSecret,
        decimal FeePercent, decimal UsdToTzsRate, decimal DailyLimit)
    {
        var settings = new ChapSmartSettings
        {
            StoreId = storeId,
            Enabled = Enabled,
            AutoPayout = AutoPayout,
            ChapSmartApiUrl = ChapSmartApiUrl ?? "",
            ChapSmartApiKey = ChapSmartApiKey ?? "",
            ChapSmartApiSecret = ChapSmartApiSecret ?? "",
            FeePercent = FeePercent,
            UsdToTzsRate = UsdToTzsRate,
            DailyLimit = DailyLimit
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
            success ? "Connected to API successfully!" : "Connection failed. Check your credentials.";
        return RedirectToAction(nameof(EditChapSmart), new { storeId });
    }

    [HttpGet("stores/{storeId}/plugins/chapsmart/dashboard")]
    public async Task<IActionResult> Dashboard(string storeId, string status = null)
    {
        await using var db = _dbFactory.CreateContext();
        var query = db.Payouts.Where(p => p.StoreId == storeId);
        if (!string.IsNullOrEmpty(status)) query = query.Where(p => p.Status == status);
        var payouts = await query.OrderByDescending(p => p.CreatedAt).Take(50).ToListAsync();
        var total = await db.Payouts.CountAsync(p => p.StoreId == storeId);
        var completed = await db.Payouts.CountAsync(p => p.StoreId == storeId && p.Status == "completed");
        var failed = await db.Payouts.CountAsync(p => p.StoreId == storeId && p.Status == "failed");
        var volume = await db.Payouts.Where(p => p.StoreId == storeId && p.Status == "completed").SumAsync(p => p.AmountTZS);

        return Content(DashboardHtml(storeId, payouts, total, completed, failed, volume, status), "text/html");
    }

    private string SettingsHtml(ChapSmartSettings s, string success, string error)
    {
        var storeId = CurrentStore.Id;
        var chk = (bool v) => v ? "checked" : "";
        var esc = (string v) => System.Net.WebUtility.HtmlEncode(v ?? "");

        return $@"
<style>
  .cs-wrap {{ max-width: 700px; margin: 0 auto; padding: 30px 20px; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; color: #1a1a2e; }}
  .cs-title {{ font-size: 24px; font-weight: 700; margin-bottom: 4px; }}
  .cs-sub {{ color: #6b7280; font-size: 14px; margin-bottom: 24px; }}
  .cs-alert {{ padding: 12px 16px; border-radius: 8px; margin-bottom: 20px; font-size: 14px; }}
  .cs-alert-ok {{ background: #dcfce7; color: #166534; border: 1px solid #86efac; }}
  .cs-alert-err {{ background: #fee2e2; color: #991b1b; border: 1px solid #fca5a5; }}
  .cs-card {{ background: #fff; border: 1px solid #e5e7eb; border-radius: 12px; padding: 24px; margin-bottom: 20px; }}
  .cs-card h3 {{ font-size: 16px; font-weight: 600; margin: 0 0 16px 0; color: #374151; }}
  .cs-row {{ margin-bottom: 16px; }}
  .cs-label {{ display: block; font-size: 13px; font-weight: 600; color: #374151; margin-bottom: 4px; }}
  .cs-hint {{ font-size: 12px; color: #9ca3af; margin-top: 2px; }}
  .cs-input {{ width: 100%; padding: 8px 12px; border: 1px solid #d1d5db; border-radius: 6px; font-size: 14px; box-sizing: border-box; }}
  .cs-input:focus {{ outline: none; border-color: #f7931a; box-shadow: 0 0 0 2px rgba(247,147,26,0.2); }}
  .cs-toggle {{ display: flex; align-items: center; gap: 10px; margin-bottom: 12px; }}
  .cs-toggle input {{ width: 18px; height: 18px; accent-color: #f7931a; }}
  .cs-toggle label {{ font-size: 14px; font-weight: 500; cursor: pointer; }}
  .cs-btns {{ display: flex; gap: 10px; margin-top: 24px; flex-wrap: wrap; }}
  .cs-btn {{ padding: 10px 20px; border-radius: 6px; font-size: 14px; font-weight: 600; cursor: pointer; border: none; text-decoration: none; display: inline-block; }}
  .cs-btn-primary {{ background: #f7931a; color: #fff; }}
  .cs-btn-primary:hover {{ background: #e8850f; }}
  .cs-btn-secondary {{ background: #f3f4f6; color: #374151; border: 1px solid #d1d5db; }}
  .cs-btn-secondary:hover {{ background: #e5e7eb; }}
  .cs-btn-outline {{ background: transparent; color: #f7931a; border: 1px solid #f7931a; }}
  .cs-btn-outline:hover {{ background: #fff7ed; }}
  .cs-divider {{ border: none; border-top: 1px solid #e5e7eb; margin: 20px 0; }}
  .cs-ver {{ text-align: right; color: #9ca3af; font-size: 12px; margin-top: 16px; }}
</style>
<div class='cs-wrap'>
  <div class='cs-title'>&#x1F4B1; ChapSmart — Mobile Money Payout</div>
  <div class='cs-sub'>Automatically send local currency to mobile money when a BTCPay invoice is paid.</div>

  {(success != null ? $"<div class='cs-alert cs-alert-ok'>{esc(success)}</div>" : "")}
  {(error != null ? $"<div class='cs-alert cs-alert-err'>{esc(error)}</div>" : "")}

  <form method='post' action='/stores/{storeId}/plugins/chapsmart'>
    <div class='cs-card'>
      <h3>General</h3>
      <div class='cs-toggle'>
        <input type='checkbox' id='Enabled' name='Enabled' value='true' {chk(s.Enabled)} />
        <label for='Enabled'>Enable ChapSmart payouts</label>
      </div>
      <div class='cs-toggle'>
        <input type='checkbox' id='AutoPayout' name='AutoPayout' value='true' {chk(s.AutoPayout)} />
        <label for='AutoPayout'>Automatic payout on invoice settlement</label>
      </div>
    </div>

    <div class='cs-card'>
      <h3>API Connection</h3>
      <div class='cs-row'>
        <label class='cs-label'>API URL</label>
        <input class='cs-input' name='ChapSmartApiUrl' value='{esc(s.ChapSmartApiUrl)}' placeholder='https://your-api-url.com' />
        <div class='cs-hint'>Your ChapSmart payout API base URL</div>
      </div>
      <div class='cs-row'>
        <label class='cs-label'>API Key</label>
        <input class='cs-input' name='ChapSmartApiKey' value='{esc(s.ChapSmartApiKey)}' placeholder='Your API key' />
      </div>
      <div class='cs-row'>
        <label class='cs-label'>API Secret</label>
        <input class='cs-input' name='ChapSmartApiSecret' type='password' value='{esc(s.ChapSmartApiSecret)}' placeholder='Your API secret' />
      </div>
    </div>

    <div class='cs-card'>
      <h3>Payout Configuration</h3>
      <div class='cs-row'>
        <label class='cs-label'>Fee Percent (%)</label>
        <input class='cs-input' name='FeePercent' type='number' step='0.01' min='0' max='10' value='{s.FeePercent}' />
        <div class='cs-hint'>Fee charged on each payout (e.g., 2.2)</div>
      </div>
      <div class='cs-row'>
        <label class='cs-label'>USD to Local Currency Rate</label>
        <input class='cs-input' name='UsdToTzsRate' type='number' step='0.01' value='{s.UsdToTzsRate}' />
        <div class='cs-hint'>Exchange rate for conversion</div>
      </div>
      <div class='cs-row'>
        <label class='cs-label'>Daily Payout Limit</label>
        <input class='cs-input' name='DailyLimit' type='number' step='1000' value='{s.DailyLimit}' />
        <div class='cs-hint'>Maximum total payouts per day in local currency</div>
      </div>
    </div>

    <div class='cs-btns'>
      <button type='submit' class='cs-btn cs-btn-primary'>Save Settings</button>
      <button type='submit' formaction='/stores/{storeId}/plugins/chapsmart/test' class='cs-btn cs-btn-secondary'>Test Connection</button>
      <a href='/stores/{storeId}/plugins/chapsmart/dashboard' class='cs-btn cs-btn-outline'>Payout Dashboard</a>
    </div>
  </form>

  <div class='cs-ver'>ChapSmart Plugin v1.0.0</div>
</div>";
    }

    private string DashboardHtml(string storeId, List<ChapSmartPayout> payouts, int total, int completed, int failed, decimal volume, string filter)
    {
        var rows = "";
        foreach (var p in payouts)
        {
            var badge = p.Status switch
            {
                "completed" => "<span style='background:#dcfce7;color:#166534;padding:2px 8px;border-radius:4px;font-size:12px;'>Completed</span>",
                "failed" => "<span style='background:#fee2e2;color:#991b1b;padding:2px 8px;border-radius:4px;font-size:12px;'>Failed</span>",
                "processing" => "<span style='background:#fef9c3;color:#854d0e;padding:2px 8px;border-radius:4px;font-size:12px;'>Processing</span>",
                _ => $"<span style='background:#f3f4f6;color:#374151;padding:2px 8px;border-radius:4px;font-size:12px;'>{p.Status}</span>"
            };
            rows += $@"<tr>
                <td style='padding:10px 12px;border-bottom:1px solid #e5e7eb;'>{p.CreatedAt:yyyy-MM-dd HH:mm}</td>
                <td style='padding:10px 12px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(p.PhoneNumber)}</td>
                <td style='padding:10px 12px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(p.RecipientName ?? "")}</td>
                <td style='padding:10px 12px;border-bottom:1px solid #e5e7eb;font-weight:600;'>{p.AmountTZS:N0}</td>
                <td style='padding:10px 12px;border-bottom:1px solid #e5e7eb;'>{badge}</td>
            </tr>";
        }

        if (string.IsNullOrEmpty(rows))
            rows = "<tr><td colspan='5' style='padding:40px;text-align:center;color:#9ca3af;'>No payouts yet. Payouts appear here when invoices with mobile money metadata are settled.</td></tr>";

        var btnStyle = (string s, string f) => f == s
            ? "background:#f7931a;color:#fff;padding:6px 14px;border-radius:6px;font-size:13px;text-decoration:none;font-weight:600;"
            : "background:#f3f4f6;color:#374151;padding:6px 14px;border-radius:6px;font-size:13px;text-decoration:none;border:1px solid #d1d5db;";

        return $@"
<style>
  .cs-wrap {{ max-width: 900px; margin: 0 auto; padding: 30px 20px; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; color: #1a1a2e; }}
</style>
<div class='cs-wrap'>
  <div style='display:flex;justify-content:space-between;align-items:center;margin-bottom:24px;'>
    <div style='font-size:24px;font-weight:700;'>&#x1F4B1; Payout Dashboard</div>
    <a href='/stores/{storeId}/plugins/chapsmart' style='color:#f7931a;text-decoration:none;font-size:14px;'>&larr; Settings</a>
  </div>

  <div style='display:grid;grid-template-columns:repeat(4,1fr);gap:16px;margin-bottom:24px;'>
    <div style='background:#fff;border:1px solid #e5e7eb;border-radius:12px;padding:20px;text-align:center;'>
      <div style='font-size:12px;color:#6b7280;'>Total</div>
      <div style='font-size:28px;font-weight:700;'>{total}</div>
    </div>
    <div style='background:#fff;border:1px solid #e5e7eb;border-radius:12px;padding:20px;text-align:center;'>
      <div style='font-size:12px;color:#6b7280;'>Completed</div>
      <div style='font-size:28px;font-weight:700;color:#16a34a;'>{completed}</div>
    </div>
    <div style='background:#fff;border:1px solid #e5e7eb;border-radius:12px;padding:20px;text-align:center;'>
      <div style='font-size:12px;color:#6b7280;'>Failed</div>
      <div style='font-size:28px;font-weight:700;color:#dc2626;'>{failed}</div>
    </div>
    <div style='background:#fff;border:1px solid #e5e7eb;border-radius:12px;padding:20px;text-align:center;'>
      <div style='font-size:12px;color:#6b7280;'>Volume</div>
      <div style='font-size:28px;font-weight:700;'>{volume:N0}</div>
    </div>
  </div>

  <div style='margin-bottom:16px;display:flex;gap:8px;'>
    <a href='/stores/{storeId}/plugins/chapsmart/dashboard' style='{btnStyle(null, filter)}'>All</a>
    <a href='/stores/{storeId}/plugins/chapsmart/dashboard?status=completed' style='{btnStyle("completed", filter)}'>Completed</a>
    <a href='/stores/{storeId}/plugins/chapsmart/dashboard?status=failed' style='{btnStyle("failed", filter)}'>Failed</a>
    <a href='/stores/{storeId}/plugins/chapsmart/dashboard?status=processing' style='{btnStyle("processing", filter)}'>Processing</a>
  </div>

  <div style='background:#fff;border:1px solid #e5e7eb;border-radius:12px;overflow:hidden;'>
    <table style='width:100%;border-collapse:collapse;font-size:14px;'>
      <thead>
        <tr style='background:#f9fafb;'>
          <th style='padding:12px;text-align:left;font-weight:600;color:#374151;border-bottom:2px solid #e5e7eb;'>Date</th>
          <th style='padding:12px;text-align:left;font-weight:600;color:#374151;border-bottom:2px solid #e5e7eb;'>Phone</th>
          <th style='padding:12px;text-align:left;font-weight:600;color:#374151;border-bottom:2px solid #e5e7eb;'>Recipient</th>
          <th style='padding:12px;text-align:left;font-weight:600;color:#374151;border-bottom:2px solid #e5e7eb;'>Amount</th>
          <th style='padding:12px;text-align:left;font-weight:600;color:#374151;border-bottom:2px solid #e5e7eb;'>Status</th>
        </tr>
      </thead>
      <tbody>{rows}</tbody>
    </table>
  </div>

  <div style='text-align:right;color:#9ca3af;font-size:12px;margin-top:16px;'>ChapSmart Plugin v1.0.0</div>
</div>";
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
