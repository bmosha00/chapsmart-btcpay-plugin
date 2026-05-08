# ChapSmart — BTCPay Server Plugin

**Automatic Bitcoin to Mobile Money payouts for BTCPay Server.**

When a BTCPay invoice is paid (Lightning, on-chain, or altcoin via Exolix), ChapSmart automatically sends local currency to the recipient's mobile money wallet.

> ⚡ Bitcoin in → 📱 Mobile money out — in seconds.

## How It Works

```
Customer pays BTCPay invoice (Lightning / On-chain / Altcoin)
    ↓
BTCPay marks invoice as settled
    ↓
ChapSmart plugin catches the event
    ↓
Reads phoneNumber + amount from invoice metadata
    ↓
Calls your payout API to push funds to mobile money
    ↓
Recipient receives local currency on their phone
```

No manual intervention. No copy-pasting. Fully automated.

## Features

- **Automatic payouts** — Triggers mobile money payout the moment an invoice is settled
- **Manual mode** — Review payouts before triggering (optional)
- **Per-store settings** — Each BTCPay store has independent configuration
- **Payout dashboard** — View all payouts with status, filter by completed/failed/processing
- **Deduplication** — Atomic database transactions prevent double payouts
- **Rate limiting** — Built-in protection against payout spam
- **Retry logic** — Failed payouts are automatically retried
- **Audit trail** — Every payout is recorded with timestamps, amounts, and provider transaction IDs

## Requirements

- BTCPay Server **v2.0.1** or higher
- .NET 8.0 runtime (included in BTCPay Server)
- A payout API endpoint that accepts `{ phoneNumber, amount, recipientName, invoiceId }` and triggers the mobile money transfer

## Installation

### Option A: Upload DLL (Recommended)

1. Download the latest release from [Releases](https://github.com/bmosha00/chapsmart-btcpay-plugin/releases)
2. In BTCPay Server, go to **Server Settings → Plugins → Upload Plugin**
3. Upload the `.btcpay` plugin file
4. Restart BTCPay Server

### Option B: Manual Install

1. Build the plugin:
   ```bash
   git clone https://github.com/bmosha00/chapsmart-btcpay-plugin.git --recurse-submodules
   cd chapsmart-btcpay-plugin
   dotnet publish BTCPayServer.Plugins.ChapSmart -c Release
   ```

2. Copy the output to your BTCPay Server plugins directory:
   ```bash
   # Docker deployment
   docker cp BTCPayServer.Plugins.ChapSmart/bin/Release/net8.0/publish/. \
     generated_btcpayserver_1:/root/.btcpayserver/Plugins/BTCPayServer.Plugins.ChapSmart/
   
   docker restart generated_btcpayserver_1
   ```

## Configuration

After installation, navigate to your **Store → ChapSmart** in the sidebar.

| Setting | Description | Default |
|---------|-------------|---------|
| **Enable** | Turn on/off automatic payouts for this store | Off |
| **Auto Payout** | Trigger payout immediately on invoice settlement | On |
| **API URL** | Your payout API base URL | — |
| **API Key** | Authentication key for your payout API | — |
| **API Secret** | Authentication secret for your payout API | — |
| **Fee Percent** | Fee percentage applied to each payout | 2.2% |
| **Exchange Rate** | USD to local currency exchange rate | — |
| **Daily Limit** | Maximum payout amount per day (local currency) | 1,000,000 |

## Invoice Metadata

For ChapSmart to process a payout, the BTCPay invoice must include the following metadata fields:

```json
{
  "phoneNumber": "+255XXXXXXXXX",
  "amountTZS": 25000,
  "recipientName": "John Doe"
}
```

Invoices without these fields are ignored by the plugin.

You can set metadata when creating invoices via the [Greenfield API](https://docs.btcpayserver.org/API/Greenfield/v1/):

```bash
curl -X POST "https://your-btcpay.com/api/v1/stores/{storeId}/invoices" \
  -H "Authorization: token YOUR_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "amount": 0.00012,
    "currency": "BTC",
    "metadata": {
      "phoneNumber": "+255XXXXXXXXX",
      "amountTZS": 25000,
      "recipientName": "John Doe"
    }
  }'
```

## Architecture

ChapSmart is a **Phase A (Bridge) plugin** — it runs inside BTCPay Server and calls an external payout API for the actual mobile money transfer.

```
┌─────────────────────────────────┐
│       BTCPay Server             │
│                                 │
│  ┌───────────────────────────┐  │
│  │   ChapSmart Plugin        │  │
│  │                           │  │
│  │  • Invoice Event Handler  │  │
│  │  • Settings Repository    │  │
│  │  • Payout Database        │  │
│  │  • Dashboard UI           │  │
│  └──────────┬────────────────┘  │
│             │ HTTP POST         │
└─────────────┼───────────────────┘
              │
              ▼
┌─────────────────────────────────┐
│     Your Payout API             │
│                                 │
│  Receives: phoneNumber, amount  │
│  Triggers: Mobile money push    │
│  Returns: success/failure       │
└─────────────────────────────────┘
```

A future **Phase B (Native)** version will handle mobile money API calls directly within the plugin, eliminating the need for an external payout API.

## Development

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Git

### Build from Source

```bash
# Clone with BTCPay Server submodule
git clone https://github.com/bmosha00/chapsmart-btcpay-plugin.git --recurse-submodules
cd chapsmart-btcpay-plugin

# Checkout the BTCPay version matching your server
cd btcpayserver
git checkout v2.3.3
cd ..

# Build BTCPay Server (first time only, ~8 minutes)
dotnet build btcpayserver

# Build the plugin
dotnet build BTCPayServer.Plugins.ChapSmart
```

### Project Structure

```
BTCPayServer.Plugins.ChapSmart/
├── Plugin.cs                          # Entry point, registers services
├── PluginMigrationRunner.cs           # Runs database migrations on startup
├── Controllers/
│   └── UIChapSmartController.cs       # Settings + dashboard pages
├── Services/
│   ├── ChapSmartService.cs            # Payout API client
│   ├── ChapSmartInvoiceHandler.cs     # Invoice event listener
│   ├── ChapSmartSettingsRepository.cs # Per-store settings storage
│   └── ChapSmartDbContextFactory.cs   # Database context factory
├── Data/
│   ├── ChapSmartDbContext.cs           # EF Core database context
│   ├── ChapSmartPayout.cs             # Payout entity model
│   └── Migrations/                    # Database migrations
├── Views/
│   ├── _ViewImports.cshtml
│   ├── Shared/
│   │   └── ChapSmartNav.cshtml        # Store sidebar navigation
│   └── UIChapSmart/
│       ├── EditChapSmart.cshtml       # Settings page
│       ├── Dashboard.cshtml           # Payout dashboard
│       └── PayoutDetail.cshtml        # Individual payout detail
└── Resources/
    └── img/
```

## Payout API Contract

Your external payout API must implement the following endpoint:

### POST /api/v1/internal/payout

**Request:**
```json
{
  "phoneNumber": "+255XXXXXXXXX",
  "amountTZS": 25000,
  "recipientName": "John Doe",
  "invoiceId": "ABC123",
  "source": "btcpay-plugin"
}
```

**Success Response (200):**
```json
{
  "success": true,
  "message": "Payout initiated",
  "payoutId": "PAYOUT-abc-123"
}
```

**Already Processed Response (200):**
```json
{
  "success": true,
  "message": "Already processed",
  "alreadyProcessed": true
}
```

**Error Response (4xx/5xx):**
```json
{
  "success": false,
  "error": "Insufficient balance"
}
```

## Roadmap

- [x] Plugin scaffold and BTCPay integration
- [x] Invoice event handler with deduplication
- [x] Per-store settings with API credentials
- [x] Payout database with EF Core migrations
- [x] Store sidebar navigation
- [ ] Full settings page UI (BTCPay layout integration)
- [ ] Payout dashboard with stats and filtering
- [ ] End-to-end testing with real mobile money
- [ ] Phase B: Native mobile money API integration
- [ ] Multi-country support (Kenya, Uganda, Ghana)
- [ ] BTCPay Plugin Directory submission

## License

MIT

## Contributing

Contributions are welcome! Please open an issue first to discuss what you'd like to change.

## Acknowledgments

- [BTCPay Server](https://btcpayserver.org/) — Open source payment processor
- [BTCPay Plugin Template](https://github.com/btcpayserver/btcpayserver-plugin-template) — Starting point for this plugin
- Built for Africa's mobile money ecosystem 🌍
