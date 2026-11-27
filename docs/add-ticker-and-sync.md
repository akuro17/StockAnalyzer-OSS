# Adding Tickers and Syncing Market Data

This guide explains how to add stock tickers to StockAnalyzer using Tools > Add Ticker and download their historical price data and company information in Sync Progress.

## 1. Overview

Adding a new ticker in StockAnalyzer has two main steps:

1. Add the ticker (Add Ticker): Enter a stock symbol, or import a list of symbols from a file. You can also choose which Watchlists, Portfolios, and Tags to assign.
2. Download market data (Sync Progress): Once you confirm, the Sync Progress window opens automatically. It downloads daily historical price data, builds weekly and monthly views, and fetches company details. When finished, your charts and ticker list update automatically.

## 2. Prerequisites

Before adding and syncing tickers, check the following:

- Writable Data folder (not in Read-Only Mode):
  Adding tickers and saving market data requires write access to StockAnalyzer's Data folder. If StockAnalyzer is placed in a protected folder (such as `C:\Program Files`), a banner warns that the app is in read-only mode and Add Ticker is grayed out. Move StockAnalyzer to a writable folder or grant write permissions to Data, then restart the app.
- Active Internet Connection:
  Syncing downloads price history and company metadata online via Yahoo Finance (`yfinance`).
- Python Environment:
  - Windows (default setup): StockAnalyzer sets up an embedded Python environment automatically. On your first sync, it may ask to install Python and required packages, which can take a few minutes.
  - macOS / Linux (or custom Python): Python 3.13 (3.13.17 or later) with the packages in `StockAnalyzer.Python/requirements.txt` must be installed and set under `Python.PythonPath` in `appsettings.json`.
- Watchlists or Portfolios (optional):
  If you want to place new tickers into specific lists while adding them, create those Watchlists or Portfolios in the left-side Tickers panel beforehand. You can still add tickers without choosing a list; they are always saved to All Tickers.

## 3. Adding a Ticker (Tools > Add Ticker)

### Step-by-Step Instructions

1. Open the Add Ticker window:
   - From the top menu bar, click Tools > Add Ticker.
   - Tip: You can also right-click an item in the left-side Tickers panel (such as All Tickers, a Watchlist, or a Portfolio) and choose Add Ticker.

2. Enter a symbol under SYMBOL SEARCH (for a single ticker):
   - Click the box labeled "Search by symbol..." and type a ticker symbol (for example, `AAPL`, `MSFT`, `BRK-B`, or `7203`).
   - Wait briefly while StockAnalyzer checks your input:
     - If the symbol already exists in your master list: Matching entries appear below showing the symbol, "Stock Symbol", a MARKET badge, and a green Valid checkmark. Click an entry (or press the Down arrow key and then Enter) to select it.
     - If the symbol is new: A status row appears below showing the symbol with "New Ticker" and "+ Add New".

   Ticker input rules:
   - Automatic uppercase and formatting: Leading and trailing spaces are removed, letters are converted to uppercase, and dots (`.`) are converted to hyphens (`-`) when saved (for example, `brk.b` becomes `BRK-B`, and `7203.t` becomes `7203-T`).
   - Japanese 4-digit stock codes: Entering 4 digits (for example, `7203`) automatically adds `-T` when saved (`7203-T`).
   - Length limit: The "Search by symbol..." box accepts up to 10 characters (after trimming spaces). If the input is empty or longer than 10 characters, the Add button stays disabled.
   - Supported characters: Use standard letters (A-Z), numbers (0-9), hyphens (`-`), or dots (`.`). Continuous futures (`<ROOT>=F`, such as `CL=F`) and currency pairs (`<ROOT>=X`, such as `EURUSD=X`) are also supported. Avoid spaces, slashes (`/` or `\`), colons (`:`), `..`, leading hyphens, or full-width characters.

3. Import multiple tickers from a file (alternative to step 2):
   - To add several tickers at once (or symbols up to 16 characters), set any optional List and Tags in step 4 first, then click "Import From File (CSV/TXT)".
   - As noted next to the button ("Format: One ticker per line, or separated by comma, space, or tab."), prepare a plain UTF-8 `.txt` or `.csv` file with symbols separated by line breaks, commas, spaces, or tabs.
   - Clicking "Import From File (CSV/TXT)" closes the Add Ticker window and opens a file picker ("Bulk Import Tickers from File"). Choose your file to import all valid tickers and start syncing them. Duplicate or invalid entries in the file are skipped automatically.

4. Assign Lists and Tags (optional):
   - List: Click the dropdown button (which shows "Unspecified" by default) to open the list picker. Check one or more boxes under Watchlist or Portfolio (portfolios show a PF badge).
     - Note: If you leave List as "Unspecified", the ticker is still added to All Tickers, and if you currently had a Watchlist or Portfolio selected in the Tickers panel, it is also added to that active list.
   - Tags: Click the box labeled "Enter tags separated by commas (e.g. Growth, Tech)" and type one or more tags separated by commas. These tags are added without removing existing tags on the ticker.

5. Confirm and start syncing:
   - Click Add (or press Enter while the search box is focused) to save the ticker and open Sync Progress.
   - Click Cancel to close the Add Ticker window without making changes.

## 4. Using the Sync Progress Window

After you click Add (or import a file), StockAnalyzer opens the Sync Progress window to download market data for the added tickers.

### Summary Cards (Top)

Three counters at the top show the overall progress of the current batch:

- Completed: Number of tickers that have finished syncing.
- Syncing: Number of tickers currently being downloaded and processed.
- Error: Number of tickers that failed to sync.

### Advanced Sync Settings (Collapsible Panel)

Click the "Advanced Sync Settings" header to show or hide sync options:

| Setting | Default | Description |
|---|---|---|
| Sync Time-Series | Checked | Downloads daily historical price bars and generates weekly and monthly timeframes. |
| Sync Metadata | Checked | Downloads company and ticker details (such as exchange, website, and financial metrics). |
| Impute Missing Indicators | Unchecked | Estimates missing financial metadata indicators from available metrics. Enabled only when Sync Metadata is checked. |
| Auto-Start Sync | Checked | Starts syncing automatically when Sync Progress opens. If unchecked, tickers wait until you click Start Sync. |
| Re-fetch all historical data | Unchecked | Re-downloads all bars within the Lookback Period instead of fetching only recent missing days. |
| Full History | Unchecked | Downloads all available historical price data instead of limiting to the Lookback Period. |
| Delay Range (s) | 3.0 ~ 5.0 sec | Wait range (3.0 to 60.0 seconds) between consecutive tickers during multi-ticker sync to avoid rate limits. |
| Delete latest bars | 0 bars | Deletes the specified number of most recent bars (0 to 1000) before downloading fresh data (0 = keep all). Resets to 0 when you close the window. |
| Lookback Period (years) | 10 years | Number of years of price history to fetch (1 to 50) when Full History is unchecked. |

### Progress Table (Middle)

Each ticker in the sync queue appears as a row with these columns:

- Symbol: The normalized ticker symbol (for example, `AAPL` or `7203-T`).
- Period: The base data period being synced (`D1` for daily bars; weekly and monthly data are generated automatically from `D1`).
- Progress: A progress bar (0% to 100%) and label underneath (such as `Waiting`, `Syncing 15%`, or `Complete`).
- Status: The current state of that ticker:
  - `Waiting`: Waiting in queue to start.
  - `Syncing`: Actively downloading and processing data.
  - `Completed`: Data download and processing succeeded.
  - `Error`: Sync failed for this ticker.
  - `Canceled`: Sync was stopped before completion.
- Last Updated: The time (`HH:mm:ss`) of the last progress update (or `-` before starting).
- Action: Shows a Retry button (circular arrow icon) on rows whose Status is `Error`, `Canceled`, or `Waiting`. Click it to retry just that ticker.

### Logs

Sync Progress shows per-ticker progress bars, row statuses, and the summary message at the bottom, rather than a log panel inside the window. To view detailed sync logs or error messages, open Tools > Diagnostic Logs from the main menu bar.

### Bottom Status Bar and Buttons

- Status Message (center): Shows the overall batch status:
  - `Waiting in queue...`: Tickers are queued and not currently syncing.
  - `Syncing data...`: At least one ticker is currently syncing.
  - `Sync completed successfully.`: All tickers in the list finished without error.
  - `Sync failed with errors.`: Syncing has stopped and at least one ticker has an `Error` status.
- Start Sync: Starts or restarts syncing for any tickers that are not yet `Completed`. Disabled while a sync is actively running.
- Stop All: Cancels the active sync queue. The currently running ticker stops and changes to `Canceled`.
- Close: Stops any active sync, resets "Delete latest bars" to 0, and closes the Sync Progress window.

### How to Tell When Sync Is Complete

You can confirm the sync is finished when all of the following are true:

1. At the top, Syncing is `0`, Error is `0`, and Completed matches the total number of tickers.
2. Every row in the table shows a full progress bar (`Complete`) and `Completed` in the Status column.
3. The message at the bottom reads `Sync completed successfully.`.

Click Close to close the window. Your chart and Tickers panel automatically display the newly downloaded data.

## 5. Troubleshooting

### Add Ticker is grayed out or disabled
- Cause: StockAnalyzer is running in read-only mode because it cannot write to its Data folder (for example, if installed under `C:\Program Files`). Hovering over the disabled Add Ticker menu item shows the reason.
- Solution: Move StockAnalyzer to a writable folder (or grant write permission to the Data folder) and restart StockAnalyzer.

### The Add button stays disabled after typing a symbol
- Cause: The search box is empty, still validating, or contains more than 10 characters.
- Solution: Make sure the symbol is 1 to 10 characters long without extra spaces and wait briefly for validation to finish. For valid symbols longer than 10 characters (up to 16 characters), use "Import From File (CSV/TXT)".

### File import shows "No valid tickers found in the file."
- Cause: The selected file is empty, contains non-ASCII characters (such as full-width letters or numbers), uses unsupported separators, or has tokens longer than 16 characters.
- Solution: Save the file as UTF-8 `.txt` or `.csv` using standard ASCII characters, with one symbol per line or symbols separated by commas, spaces, or tabs.

### Sync opens but stays on Waiting
- Cause: "Auto-Start Sync" is unchecked under Advanced Sync Settings.
- Solution: Click Start Sync at the bottom right of Sync Progress. Check "Auto-Start Sync" if you want future syncs to start automatically.

### A ticker fails with Error or times out
- Incorrect or delisted symbol: Check the ticker spelling and market suffix (for example, `BRK-B` or `7203-T`). Because adding a ticker registers it in your list before syncing starts, if you entered the wrong symbol, right-click it in the Tickers panel and choose "Delete from Master List", then add the correct symbol.
- Rate limiting (especially during bulk imports): Yahoo Finance may reject requests if many tickers are queried too quickly. Expand Advanced Sync Settings, increase "Delay Range (s)", and click Start Sync (or the row's Retry button).
- Network or timeout error: Each ticker sync has a safety timeout (2 minutes by default). Check your internet connection and click the row's Retry button (or Start Sync).
- Python configuration error (macOS / Linux): Verify that `Python.PythonPath` in `appsettings.json` points to your Python 3.13 environment and that the packages in `StockAnalyzer.Python/requirements.txt` are installed.
- File in use: If another program has a data file open inside `Data/Daily`, `Data/Weekly`, `Data/Monthly`, or `Data/Metadata`, close that program and retry.

### Sync was stopped or closed before finishing
- What happens: Clicking Stop All or Close stops any remaining tickers (marking interrupted items as `Canceled`). Tickers that already reached `Completed` remain saved.
- How to resume:
  - If Sync Progress is still open, click Start Sync (or Retry on an individual row).
  - If you already closed Sync Progress, select the tickers in the Tickers panel, right-click, and choose "Sync Symbols" (or right-click a list or folder and choose "Sync All").

### Viewing detailed error logs
- Open Tools > Diagnostic Logs from the top menu bar.
- Make sure "Enable Diagnostic Logging" is checked, check "Errors Only" or type the ticker symbol into "Search:" to locate failure details, and use "Copy Details" or "Export" if needed.
