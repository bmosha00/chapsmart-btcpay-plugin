# ChapSmart Cashout — BTCPay Server Plugin

Convert your Bitcoin earnings to Tanzanian Shillings (TZS) and receive them directly on M-Pesa. Automatically.

## How It Works

1. A customer pays a BTCPay invoice on your server (Lightning or on-chain)
2. The plugin catches the invoice settlement
3. Plugin calls ChapSmart's cashout API with your Merchant ID and the TZS amount
4. ChapSmart returns a Lightning invoice — the plugin pays it from your wallet
5. ChapSmart receives the BTC and sends TZS to your registered M-Pesa number
6. You get an M-Pesa notification. Done.

The plugin's job ends at step 4. ChapSmart handles everything after that.

## Requirements

- BTCPay Server v2.0.1 or later
- Lightning Network enabled on your BTCPay store
- A ChapSmart Merchant ID (register at [chapsmart.com](https://chapsmart.com))

## Installation

### From Source

```bash
git clone --recurse-submodules https://github.com/bmosha00/chapsmart-btcpay-plugin.git
cd chapsmart-btcpay-plugin
dotnet publish BTCPayServer.Plugins.ChapSmart -c Release
```

Copy the DLL from `BTCPayServer.Plugins.ChapSmart/bin/Release/net8.0/publish/` to your BTCPay Server's plugin directory.

### Manual Deploy (Docker)

```bash
docker cp BTCPayServer.Plugins.ChapSmart.dll \
  btcpayserver:/root/.btcpayserver/Plugins/BTCPayServer.Plugins.ChapSmart/
docker restart btcpayserver
```

## Configuration

After installation, go to **Store → Plugins → ChapSmart** in your BTCPay dashboard.

| Setting | Description | Default |
|---------|-------------|---------|
| **Enable** | Turn cashout on/off for this store | Off |
| **Auto Cashout** | Automatically cash out every settled invoice with `amountTZS` metadata | On |
| **Merchant ID** | Your ChapSmart merchant ID (`mch_xxx`) | — |
| **Min Cashout** | Skip invoices below this TZS amount | 2,500 |
| **API URL** | ChapSmart backend URL (only change for testing) | `https://api.chapsmart.com` |

No API keys or secrets needed. The Merchant ID is your only credential.

## Invoice Metadata

For the plugin to trigger a cashout, the BTCPay invoice must include `amountTZS` in its metadata:

```json
{
  "amountTZS": 25000
}
```

Invoices without `amountTZS` in metadata are ignored by the plugin.

## API

The plugin calls two endpoints on the ChapSmart backend:

### Create Cashout
```
POST https://api.chapsmart.com/api/v1/cashout
Content-Type: application/json

{ "merchantId": "mch_xxx", "amountTZS": 25000 }
```

Returns: `{ bolt11, amountSats, cashoutId, expiresIn, merchantName }`

### Check Status
```
GET https://api.chapsmart.com/api/v1/cashout/status/{cashoutId}
```

Returns: `{ status, amountTZS, amountSats, merchantName }`

Both endpoints are public — no authentication headers required.

## Cashout Statuses

| Status | Meaning |
|--------|---------|
| `processing` | Plugin is calling the cashout API |
| `paying_lightning` | Plugin is paying the Lightning invoice |
| `lightning_paid` | BTC sent to ChapSmart. M-Pesa is being processed. |
| `failed` | Something went wrong. Check error message in dashboard. |

## Security

- ChapSmart **never** sends TZS until BTC is confirmed in ChapSmart's wallet
- The backend calculates the required BTC amount independently — invoice metadata cannot inflate the payout
- Lightning invoices enforce exact payment amounts
- The M-Pesa number is set at merchant registration, not in the request — no one can redirect your payout

## Project Structure

```
BTCPayServer.Plugins.ChapSmart/
├── Plugin.cs                          # Entry point, service registration
├── Controllers/
│   └── UIChapSmartController.cs       # Settings page, dashboard, detail view
├── Services/
│   ├── ChapSmartService.cs            # Cashout API client
│   ├── ChapSmartInvoiceHandler.cs     # Invoice event handler + Lightning payment
│   └── ChapSmartSettingsRepository.cs # Per-store settings via BTCPay store blob
├── Data/
│   ├── ChapSmartPayout.cs             # Payout entity
│   ├── ChapSmartDbContext.cs          # EF Core context
│   ├── ChapSmartDbContextFactory.cs   # DB context factory
│   └── Migrations/                    # EF Core migrations
├── Views/
│   ├── UIChapSmart/
│   │   ├── EditChapSmart.cshtml       # Settings page
│   │   ├── Dashboard.cshtml           # Cashout history
│   │   └── PayoutDetail.cshtml        # Individual cashout detail
│   ├── Shared/
│   │   └── ChapSmartNav.cshtml        # Sidebar navigation
│   └── _ViewImports.cshtml            # Razor imports
└── Resources/
    └── img/
        └── chapsmart-logo.png         # Plugin logo
```

## Merchant Registration

Contact ChapSmart to register as a merchant. You'll provide your M-Pesa number and receive a Merchant ID to enter in the plugin settings.

## Amount Limits

| Limit | Value |
|-------|-------|
| Minimum cashout | 2,500 TZS |
| Maximum cashout | 1,000,000 TZS |
| Fee | 2.2% (handled by backend, transparent to merchant) |

## License

MIT

## Links

- [ChapSmart](https://chapsmart.com)
- [BTCPay Server](https://btcpayserver.org)
- [BTCPay Plugin Development](https://docs.btcpayserver.org/Development/Plugins/)
