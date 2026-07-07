# Telegram Claude Bot

A minimal Azure Functions (.NET 8, isolated worker) backend that receives Telegram
channel messages via webhook, sends them to Claude for classification, and replies
either with a factual answer or a joke.

## How it works

```
Telegram --webhook--> Azure Function (TelegramWebhook) --> Claude API (classify + reply)
                                                        --> Telegram Bot API (send reply)
```

- One Claude API call per message does both the classification and the reply generation
  (see the system prompt in `Services/ClaudeService.cs`).
- `Services/RateLimiterService.cs` provides basic abuse protection: a per-user/chat
  per-minute cap and a global daily cap, both configurable via app settings.
- The webhook always returns HTTP 200 quickly to Telegram and logs errors internally,
  so a Claude API hiccup doesn't cause Telegram to retry-storm your Function.

## Before you build

This was written to standard, current patterns for Azure Functions Worker SDK and
Telegram.Bot, but I couldn't compile/test it in my sandbox (no NuGet access there).
Two things worth a quick glance the first time you build:

1. **`Services/TelegramSenderService.cs`** — the send method name has changed across
   Telegram.Bot major versions (`SendTextMessageAsync` in older releases, `SendMessage`
   in newer ones). If it doesn't compile, your IDE's autocomplete on the client object
   will show you the correct current name — one-line fix.
2. **Package versions in the `.csproj`** — pinned to versions I'm confident exist, but
   check NuGet for anything newer you'd prefer before deploying.
3. **`JsonBotAPI.Options`** in `TelegramWebhookFunction.cs` — this is the library's own
   serializer settings, required to correctly map Telegram's snake_case JSON (`message`,
   `channel_post`, etc.) onto the C# `Update` type. Using the default `JsonSerializer`
   settings instead will silently deserialize every field to null except a lucky few.
   If `JsonBotAPI` isn't found in your installed version, check the library's own
   webhook docs for the current equivalent — the option name has moved around across
   versions.

## Setup steps

### 1. Local prerequisites
- .NET 8 SDK
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
- Azure CLI (for deployment)

### 2. Configure secrets locally
Copy the example settings file and fill in your real values:

```bash
cp local.settings.json.example local.settings.json
```

Edit `local.settings.json`:
- `TELEGRAM_BOT_TOKEN` — the token you already have from BotFather
- `ANTHROPIC_API_KEY` — your Anthropic API key
- Leave the rate-limit values as-is or tune them

`local.settings.json` is git-ignored — never commit real secrets.

### 3. Restore and build
```bash
dotnet restore
dotnet build
```

### 4. Run locally (optional, needs a tunnel)
Telegram needs a public HTTPS URL to deliver webhooks to, so local testing needs a
tunnel (e.g. `ngrok http 7071` while running `func start`). If you'd rather skip
local testing and go straight to Azure, jump to step 5.

### 5. Create the Azure Function App
```bash
az group create --name telegram-bot-rg --location eastus

az storage account create --name telegrambotstorage --location eastus \
  --resource-group telegram-bot-rg --sku Standard_LRS

az functionapp create --resource-group telegram-bot-rg \
  --consumption-plan-location eastus \
  --runtime dotnet-isolated --functions-version 4 \
  --name YOUR-UNIQUE-FUNCTION-APP-NAME \
  --storage-account telegrambotstorage
```

### 6. Set app settings on Azure (don't put secrets in source control)
```bash
az functionapp config appsettings set --name YOUR-UNIQUE-FUNCTION-APP-NAME \
  --resource-group telegram-bot-rg \
  --settings \
    TELEGRAM_BOT_TOKEN="your-token" \
    ANTHROPIC_API_KEY="your-key" \
    ANTHROPIC_MODEL="claude-haiku-4-5-20251001" \
    MAX_MESSAGES_PER_USER_PER_MINUTE="5" \
    MAX_MESSAGES_PER_DAY_TOTAL="500" \
    MAX_INPUT_MESSAGE_LENGTH="500"
```

### 7. Deploy
```bash
func azure functionapp publish YOUR-UNIQUE-FUNCTION-APP-NAME
```
Note the function URL it prints, plus your function key (find it in the Azure
Portal under the function > "Function Keys", or via `az functionapp keys list`).

### 8. Register the webhook with Telegram
```bash
curl "https://api.telegram.org/bot<YOUR_TELEGRAM_BOT_TOKEN>/setWebhook?url=https://YOUR-UNIQUE-FUNCTION-APP-NAME.azurewebsites.net/api/telegram/webhook?code=YOUR_FUNCTION_KEY"
```

### 9. Test it
Send a message in your channel — try one clearly factual ("What's the boiling point
of water?") and one clearly humorous ("tell me a joke about mondays") and confirm
the bot replies appropriately in each case.

## Cost controls in place / still worth doing

- ✅ Per-user/chat rate limit and daily total cap (in code, in-memory)
- ✅ Input length cap to stop huge pasted text from inflating token cost
- ⬜ Set a custom monthly spend limit in the [Anthropic Console](https://console.anthropic.com)
  Limits page as an outer backstop
- ⬜ Set an Azure budget alert on the resource group (Azure cost should be near $0
  at this scale, but free to set up)

## Notes on the channel vs. group distinction

If this is a Telegram **channel** (not a group), bots typically only see messages if
they're added as an **admin** of the channel — plain "member" bots often can't read
channel posts at all. If replies aren't arriving, check the bot's admin status first.
