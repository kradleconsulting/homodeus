# Telegram Claude Bot

[![Build](https://github.com/kradleconsulting/homodeus/actions/workflows/build.yml/badge.svg)](https://github.com/kradleconsulting/homodeus/actions/workflows/build.yml)

A minimal Azure Functions (.NET 10, isolated worker) backend that receives Telegram
channel messages via webhook, sends them to Claude for classification, and replies
either with a factual answer or a joke.

## How it works

```
Telegram --webhook--> Azure Function (TelegramWebhook) --> Claude API (classify + reply)
                                                        --> Telegram Bot API (send reply)
```

- One Claude API call per message does both the classification and the reply generation
  (see the system prompt in `Services/ClaudeService.cs`).
- `Services/ConversationHistoryService.cs` keeps a rolling per-chat conversation history
  for the current UTC calendar day, so follow-up messages ("what about tomorrow?") have
  context. See [Conversation memory](#conversation-memory) below for the details/caveats.
- `Services/RateLimiterService.cs` provides basic abuse protection: a per-user/chat
  per-minute cap and a global daily cap, both configurable via app settings.
- The webhook always returns HTTP 200 quickly to Telegram and logs errors internally,
  so a Claude API hiccup doesn't cause Telegram to retry-storm your Function.

## Before you build

`dotnet restore && dotnet build` succeeds as-is against .NET 10 SDK 10.0.302 and the
package versions currently pinned in the `.csproj` (0 warnings, 0 errors). If you bump
`Telegram.Bot` to a newer major version, two spots are worth a quick glance since this
library has moved things around across releases in the past:

1. **`Services/TelegramSenderService.cs`** — the send method name has changed across
   Telegram.Bot major versions (`SendTextMessageAsync` in older releases, `SendMessage`
   in newer ones, which is what's currently used). If it doesn't compile, your IDE's
   autocomplete on the client object will show you the correct current name — one-line
   fix.
2. **`JsonBotAPI.Options`** in `TelegramWebhookFunction.cs` — this is the library's own
   serializer settings, required to correctly map Telegram's snake_case JSON (`message`,
   `channel_post`, etc.) onto the C# `Update` type. Using the default `JsonSerializer`
   settings instead will silently deserialize every field to null except a lucky few.
   If `JsonBotAPI` isn't found in your installed version, check the library's own
   webhook docs for the current equivalent — the option name has moved around across
   versions.

## Setup steps

### 1. Local prerequisites
- .NET 10 SDK
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

az functionapp list-runtimes --os windows \
  --query "[?runtime=='dotnet-isolated']" --output table
# confirm the exact .NET 10 version string for your CLI version, then use it below

az functionapp create --resource-group telegram-bot-rg \
  --consumption-plan-location eastus \
  --os-type Windows \
  --runtime dotnet-isolated --runtime-version 10 --functions-version 4 \
  --name YOUR-UNIQUE-FUNCTION-APP-NAME \
  --storage-account telegrambotstorage
```

> **.NET 10 on Consumption plan:** the flags above pin the app to .NET 10 on
> Windows Consumption, which is what this project needs. `--runtime-version 10`
> is the expected format, but `az functionapp list-runtimes` (above) is the
> source of truth for the exact version string your Azure CLI version accepts.
>
> **Linux Consumption does not support .NET 10** — if you'd rather host on
> Linux, use a [Flex Consumption plan](https://learn.microsoft.com/azure/azure-functions/flex-consumption-plan)
> instead (`az functionapp create --flexconsumption-location ...`). Windows
> Consumption (as above) is the simplest path and is what the rest of these
> steps assume.
>
> This also requires `Microsoft.Azure.Functions.Worker` ≥ 2.50.0 and
> `Microsoft.Azure.Functions.Worker.Sdk` ≥ 2.0.5 — already satisfied by the
> versions pinned in `Homodeus.csproj`.

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
    MAX_INPUT_MESSAGE_LENGTH="500" \
    MAX_HISTORY_TURNS="8"
```

### 7. Deploy
```bash
func azure functionapp publish YOUR-UNIQUE-FUNCTION-APP-NAME
```
Note the function URL it prints, plus your function key (find it in the Azure
Portal under the function > "Function Keys", or via `az functionapp keys list`).

This manual step is only needed once, to stand the app up. After that, pushes to
`main` deploy automatically - see [Continuous deployment](#continuous-deployment) below.

### 8. Register the webhook with Telegram
```bash
curl "https://api.telegram.org/bot<YOUR_TELEGRAM_BOT_TOKEN>/setWebhook?url=https://YOUR-UNIQUE-FUNCTION-APP-NAME.azurewebsites.net/api/telegram/webhook?code=YOUR_FUNCTION_KEY"
```

### 9. Test it
Send a message in your channel — try one clearly factual ("What's the boiling point
of water?") and one clearly humorous ("tell me a joke about mondays") and confirm
the bot replies appropriately in each case.

## Continuous deployment

`.github/workflows/build.yml` has a `deploy` job that runs after `build` succeeds, only
on pushes to `main` (not on PRs) - so once step 5-6 above have stood the app up the first
time, further updates just need `git push`.

It authenticates to Azure via **OIDC federated credentials** (no client secret stored
anywhere, nothing to rotate/expire):

- An Azure AD app registration trusts GitHub's OIDC issuer for exactly
  `repo:<owner>/<repo>:ref:refs/heads/main` - only a workflow run on that branch, in that
  repo, can mint a usable token.
- Its service principal has the **Website Contributor** role scoped to just the one
  Function App resource (not the resource group) - it can redeploy code, not touch app
  settings/secrets or anything else in the subscription.

To set this up for your own fork/deployment:

```bash
APP_ID=$(az ad app create --display-name "github-actions-<function-app-name>-deploy" --query appId -o tsv)
az ad sp create --id "$APP_ID"

az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "github-main",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:YOUR-GH-ORG/YOUR-REPO:ref:refs/heads/main",
  "audiences": ["api://AzureADTokenExchange"]
}'

# Role assignment scoped to just the Function App. If `az role assignment create --scope`
# errors with "MissingSubscription" on your az CLI version, use `az rest` against the ARM
# roleAssignments API directly instead - that's a CLI-level bug, not a permissions issue.
az role assignment create --assignee "$APP_ID" --role "Website Contributor" \
  --scope "$(az functionapp show --name YOUR-FUNCTION-APP-NAME --resource-group YOUR-RG --query id -o tsv)"
```

Then add three **GitHub repo secrets** (Settings → Secrets and variables → Actions):
`AZURE_CLIENT_ID` (the app's `appId`), `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`. None of
these three are secret on their own without a valid token from the trusted repo/branch, but
storing them as secrets is the conventional/safe default.

The workflow also targets a GitHub **environment** named `production` (auto-created on
first run) - add required reviewers or wait timers there later if you want a manual
approval gate before deploys go out.

## Conversation memory

`Services/ConversationHistoryService.cs` keeps the last `MAX_HISTORY_TURNS` user/assistant
exchanges per chat (default 8) and sends them along with each new message, so Claude has
context for follow-ups within the same conversation.

- **Scoped to "today":** history is keyed to the current UTC calendar day. At UTC
  midnight, or for a chat's first message ever, a chat starts with no history - there's
  no explicit reset job, the day rollover check does it implicitly.
- **In-memory, not durable:** same trade-off as `RateLimiterService` - history lives in
  the Function instance's process memory. A cold start or scale-out (common on the
  Consumption plan) silently clears it mid-day. Fine for a hobby-scale, mostly-single-
  instance bot; if usage grows enough that this becomes noticeable, swap in Azure Table
  Storage keyed by `(chatId, date)` - the point read/write pattern (one row per chat per
  day) maps directly onto what's here now.
- **Privacy note:** message text is retained (in memory only, for the current day) to
  support this. If that's not acceptable for your use case, set `MAX_HISTORY_TURNS="0"`
  to disable history entirely - each message is then classified and answered standalone,
  as before this feature existed.

## Cost controls in place / still worth doing

- ✅ Per-user/chat rate limit and daily total cap (in code, in-memory)
- ✅ Input length cap to stop huge pasted text from inflating token cost
- ✅ Conversation history capped to `MAX_HISTORY_TURNS` per chat, so a long-running
  conversation's input tokens don't grow unbounded through the day
- ⬜ Set a custom monthly spend limit in the [Anthropic Console](https://console.anthropic.com)
  Limits page as an outer backstop
- ⬜ Set an Azure budget alert on the resource group (Azure cost should be near $0
  at this scale, but free to set up)

## Notes on the channel vs. group distinction

If this is a Telegram **channel** (not a group), bots typically only see messages if
they're added as an **admin** of the channel — plain "member" bots often can't read
channel posts at all. If replies aren't arriving, check the bot's admin status first.
