# Training Wizard, Reinforcement Learning and Portfolio Reinforcement Learning Guide

StockAnalyzer can create three kinds of files from your stored price data: a prediction model (Training Wizard), a trading agent for one ticker (Reinforcement Learning) and an asset-allocation policy for several tickers (Portfolio Reinforcement Learning). This guide tells you what each tool creates, how to use the file, and what every setting does. You only need to choose the ticker(s) and the period: every other setting keeps a default that works, so start with section 3.

## Table of contents

- 1. Overview: which tool creates which file and where it is used
- 2. Before you start
- 3. Quick start with the default settings
- 4. Training Wizard reference (changing the defaults)
- 5. Reinforcement Learning reference (changing the defaults)
- 6. Portfolio Reinforcement Learning reference (changing the defaults)
- 7. Files and folders
- 8. Limitations and disclaimer
- 9. Troubleshooting

---

## 1. Overview: which tool creates which file and where it is used

| Tool | Menu path | Creates | Stored in | Used in | Not usable in |
|---|---|---|---|---|---|
| Training Wizard | Tools > Training Wizard | A prediction model: an `.onnx` file with its `.onnx.metrics.json` (and scaler and analysis files where the trainer writes them) | Working files in `Data/TrainingArtifacts/TW`, registered copy in `Data/Models/Generations` | Settings > AI Predictions (the prediction shown on a chart) | Settings > RL Playback |
| Reinforcement Learning | Tools > Reinforcement Learning | A trading agent for one ticker (daily bars), registered automatically after a non-trial run | Working files in `Data/TrainingArtifacts/RL`, registered copy in `Data/Models/Generations/RL` | Settings > RL Playback (historical replay of the agent on the chart) | Settings > AI Predictions, Backtest, Screener |
| Portfolio Reinforcement Learning | Tools > Portfolio Reinforcement Learning | An asset-allocation policy and its evaluation report, stored as a verified run folder | `Data/TrainingArtifacts/RLPortfolio` | The same window (evaluation result, base-model transfer, comparison of two runs) | Charts, Settings > AI Predictions, Backtest, Screener |

The Reinforcement Learning window only creates and reviews agents. Replaying an agent on the chart is done in Settings > RL Playback.

---

## 2. Before you start

- **Python and training packages.** All three tools train with Python. Install the packages from Settings > Python and from Settings > AI Predictions > ONNX Environment & Dependencies (the **Automatic Setup** button).
- **Stored prices.** The tools read the stored daily prices of your tickers (`Data/Daily`). A ticker without stored prices cannot be used (Portfolio Reinforcement Learning lists it with the status "Local price or metadata file is missing").
- **Enough history.** The Training Wizard shows the number of bars the data must hold under the input fields ("Minimum bars required: N"). Reinforcement Learning reports "Insufficient data" when the period is too short. Portfolio Reinforcement Learning shows "Data check (bars in period / bars needed)" for every ticker.

---

## 3. Quick start with the default settings

Change only the ticker(s) and the period. Every other setting keeps its default.

### 3.1 Training Wizard: train a model and see its prediction

1. Open **Tools** > **Training Wizard**.
2. Under **Ticker lists**, keep **All tickers** ticked, or tick the list that holds your tickers.
3. **Start Date** and **End Date** are filled in from the stored data (the start leaves the bars the default settings need); change them only to use another period, or clear **Start Date** to use all stored data.
4. Press **Start** and wait until the status shows "Training completed.".
5. Press **Deploy to Models**.
6. Open a daily chart of any ticker. The prediction of the model is shown on the chart.
7. If the status says the model is registered but not active, open **Tools** > **Settings** > **AI Predictions**, tick the model and, if asked, press **Confirm activation**.

All other settings keep their defaults.

### 3.2 Reinforcement Learning: train an agent and replay it

1. Open **Tools** > **Reinforcement Learning**.
2. Enter the ticker in **Symbol**.
3. **Start Date** and **End Date** are filled in from the stored data of the symbol; change them only to use another period. If the window asks for more price history, move the start date earlier.
4. Press **Start** and wait until the status shows "Completed".
5. Close the window. Open **Tools** > **Settings** > **RL Playback**.
6. Choose the agent in the list and press **Show on chart**. Settings closes and the chart shows the replay.
7. Read the Q-Panel under the chart: it shows the value of Hold, Buy and Sell for the replayed day.

All other settings keep their defaults.

### 3.3 Portfolio Reinforcement Learning: train and review

1. Open **Tools** > **Portfolio Reinforcement Learning**.
2. Under **Ticker lists**, tick the lists whose tickers form the portfolio.
3. **End Date** is filled in from the stored data of the ticked lists and **Start Date** is suggested by the window; change either only to use another period.
4. Read the data check below the settings. Start is possible when it says "The data is sufficient for a run.".
5. Press **Start** and wait for the run to finish.
6. Read **Evaluation result (descriptive)**.

The files of this tool are not shown on a chart.

---

## 4. Training Wizard reference (changing the defaults)

The columns of every table are: the setting as it is labelled in the application, where it is, its default, the values it accepts and what it does. Texts in the last column are the application's own texts.

### 4.1 Settings

#### Basic settings

| Setting | Where | Default | Allowed values | What it does |
|---|---|---|---|---|
| Ticker lists | Training Wizard > Basic Settings > Ticker lists | All tickers ticked when the window opens | Any combination of the listed rows (all tickers, watchlists, portfolios, saved filters, categories, lists). Ticking a list unticks "All tickers". | Tickers in this run: N (all ticked rows together; ticking a list unticks "All tickers") |
| Start Date | Training Wizard > Basic Settings > Data Period | set automatically from the stored data (the date from which the bars the current settings require are available for every ticked ticker) | A date on or before the end date. Set the start date on or before the end date. | Inclusive lower bound of the price data used. The automatic start leaves the bars the current settings require, not the whole stored history; empty means no lower bound (all stored data). With more than 10,000 tickers ticked, both dates stay empty. A date you enter is kept. |
| End Date | Training Wizard > Basic Settings > Data Period | set automatically from the stored data (the latest stored day) | A date on or after the start date. Set the start date on or before the end date. | Inclusive upper bound of the price data used. Empty means no upper bound. A date you enter is kept. |
| Timeframe | Training Wizard > Basic Settings > Data Period | Daily | Daily, Weekly, Monthly | Bar-aggregation level of the source price data. Weekly and Monthly also need the finalized data definition file below. |
| Finalized data definition file | Training Wizard > Basic Settings > Data Period | none (empty) | Path of a JSON manifest file (the field is shown for Weekly and Monthly only). | A definition file is required for weekly/monthly data |
| Prediction type | Training Wizard > Basic Settings > Model | Classification | Classification, Regression | Regression offers the PyTorch framework only. |
| Framework | Training Wizard > Basic Settings > Model | PyTorch | PyTorch, LightGBM, TensorFlow (Regression: PyTorch only) | Not described in the application. |
| Architecture | Training Wizard > Basic Settings > Model | lstm (the first architecture of the framework) | PyTorch: lstm, cnn, attn_lstm. LightGBM: gbdt. TensorFlow: lstm, gru, cnn. | Not described in the application. |
| Input length (bars) | Training Wizard > Basic Settings > Input shape | 75 | Whole number, at least 1. | Number of past bars of one input window. The minimum number of bars the data must hold is shown below the fields (Minimum bars required: N). |
| Prediction length (bars) | Training Wizard > Basic Settings > Input shape | 5 | Whole number, at least 1. | Forward label horizon in bars. |

#### Advanced settings

| Setting | Where | Default | Allowed values | What it does |
|---|---|---|---|---|
| Training start mode | Training Wizard > Advanced Settings > Transfer Learning & Initialization | New training | PyTorch: New training, Continue from model, Resume training. LightGBM: New training, Continue from model. TensorFlow: New training only. | Not described in the application. |
| Training result file to continue from | Training Wizard > Advanced Settings > Transfer Learning & Initialization | none (empty) | Path of a training result file. Check the native checkpoint, parent ModelId and frozen module settings. | Not described in the application. |
| Source model ID | Training Wizard > Advanced Settings > Transfer Learning & Initialization | none (empty) | Identifier of a registered model generation. Check the native checkpoint, parent ModelId and frozen module settings. | Not described in the application. |
| Parts to keep fixed (optional) | Training Wizard > Advanced Settings > Transfer Learning & Initialization | none (empty) | Module paths to keep fixed; available for PyTorch with Continue from model only. | Not described in the application. |
| Validation splits | Training Wizard > Advanced Settings > Validation | 5 | Whole number, at least 2. | Walk-forward validation split count; it is also the number of rows of the per-fold results table. |
| Exclusion gap (bars) | Training Wizard > Advanced Settings > Validation | none (optional) | Whole number of bars; empty keeps the default. | Purge gap in bars between each fold's train and validation blocks. Empty leaves the split at its default (window + horizon - 1). |
| Final test period (days) | Training Wizard > Advanced Settings > Validation | none (optional) | Whole number of days; empty disables the holdout. | Fixed out-of-sample holdout length in trailing calendar days, excluded from training and walk-forward validation and scored once at the end. |
| Calibrate predicted probabilities | Training Wizard > Advanced Settings > Validation | off | On or off. Calibration requires a classification target and a source that is not prepared from a finalized weekly/monthly manifest. | Not described in the application. |
| Evaluate with CPCV | Training Wizard > Advanced Settings > Validation | off | On or off. CPCV evaluates a fresh single-model recipe: turn off the ensemble and use Fresh initialization. | The common calendar is cut into consecutive groups and every combination of test groups is scored by a fresh model trained on the remaining windows, with overlapping windows purged and an embargo of one horizon after each test span. Only each combination's own scores are reported: no backtest path is built, the count is not a number of paths, and the released model is not one of these fits. CPCV scores are a robustness diagnostic, not an estimate of deployed performance: the combinations share training windows and train on both sides of each test group (unlike the walk-forward folds, which train on the past only), so their mean and spread are not independent samples and must not be read next to the out-of-sample results. |
| Number of groups | Training Wizard > Advanced Settings > Validation | 6 | Whole number, at most 20 groups and at most 45 combinations in total. | N combinations will be evaluated |
| Test groups per combination | Training Wizard > Advanced Settings > Validation | 2 | Whole number, at least 1 and below the number of groups. | N combinations will be evaluated |
| Standardize with Z-score | Training Wizard > Advanced Settings > Preprocessing | off | On or off. | Not described in the application. |
| Limit outliers | Training Wizard > Advanced Settings > Preprocessing | off | On or off. Enable fixed z-score to use clipping. | Not described in the application. |
| Limit (σ) | Training Wizard > Advanced Settings > Preprocessing | 3.0 | Enter a finite number greater than zero for the clipping bound. | Not described in the application. |
| Lags (bars, comma-separated) | Training Wizard > Advanced Settings > Preprocessing | none (empty) | Distinct bar counts from 1 to 512, in the order entered (example: 1,5,20). | Not described in the application. |
| Ensemble method | Training Wizard > Advanced Settings > Ensemble & Pretraining | Single model | Single model, Bagging, Stacking | Every member is trained and evaluated independently and registered as its own model generation; the ensemble mixes their probabilities linearly. Bagging resamples each member's training windows in time-ordered blocks; stacking fits the mixture weights on out-of-fold predictions only, never on the final test fold. Training time grows with the number of members. Classification only; cannot be combined with calibration, pretraining, FineTune or Resume. |
| Pretrain the encoder (PyTorch) | Training Wizard > Advanced Settings > Ensemble & Pretraining | off | On or off. Pretraining requires PyTorch and Fresh or Resume initialization. | 30% of the bars of each training window are hidden and reconstructed before the supervised stage starts from those weights. Only the training windows are used; the output head is trained from scratch, so the model format is unchanged. |
| Number of models | Training Wizard > Advanced Settings > Ensemble & Pretraining | 5 | Bagging only: whole number from 2 to 8. | Number of models of a bagging ensemble. Every member is registered as its own model generation. |
| Models to combine | Training Wizard > Advanced Settings > Ensemble & Pretraining | none (empty) | Distinct bases written framework[:architecture] and separated by commas (example: pytorch:lstm, pytorch:cnn, lightgbm). Enter N to N distinct bases such as pytorch:lstm, pytorch:cnn or lightgbm. | The base models a stacking ensemble combines. |
| Weight fitting method | Training Wizard > Advanced Settings > Ensemble & Pretraining | Earlier splits (standard) | Earlier splits (standard), Dedicated split segment | Default: out-of-fold predictions of earlier folds, whose models saw less data than the registered members (the weights may not transfer exactly). Meta segment: the latest part of the final fold's training data is held out, the registered members are trained without it, and the weights are fitted on their own predictions there; the members therefore lose that part of their training data. The final test fold is never used to fit. |

The Hyperparameters section lists the keys of the chosen framework and architecture; add a key with **+ Add** and type its value. The defaults are the trainers' own.

| Key | Where | Default | Allowed values | What it does |
|---|---|---|---|---|
| hidden | Training Wizard > Advanced Settings > Hyperparameters (PyTorch, TensorFlow) | 64 | Whole number, at least 1. | Hidden units of the network: its size. Larger can fit more, and overfits more easily. |
| layers | Training Wizard > Advanced Settings > Hyperparameters (PyTorch) | 2 | Whole number, at least 1. | Number of stacked recurrent layers. More layers learn more complex patterns but train slower. |
| heads | Training Wizard > Advanced Settings > Hyperparameters (PyTorch (attn_lstm only)) | 4 | Whole number from 1 to 16. | Attention heads (attn_lstm only). Must divide the hidden units, 1 to 16. |
| dense | Training Wizard > Advanced Settings > Hyperparameters (TensorFlow) | 32 | Whole number, at least 1. | Units of the dense layer before the output. |
| dropout | Training Wizard > Advanced Settings > Hyperparameters (PyTorch, TensorFlow) | 0.2 | Number from 0 up to, but not including, 1. | Share of units switched off while training (0 to 1). Higher means stronger regularisation. |
| epochs | Training Wizard > Advanced Settings > Hyperparameters (PyTorch, TensorFlow) | 15 | Whole number, at least 1. | Maximum passes over the training data. Early stopping may end sooner. |
| batch | Training Wizard > Advanced Settings > Hyperparameters (PyTorch, TensorFlow) | 256 | Whole number, at least 1. | Samples per training step. |
| lr | Training Wizard > Advanced Settings > Hyperparameters (PyTorch, TensorFlow) | 0.001 | Number greater than 0. | Learning rate: the size of each update step. Too high is unstable, too low is slow. |
| weight-decay | Training Wizard > Advanced Settings > Hyperparameters (PyTorch) | 0.0001 | Number, at least 0. | L2 penalty on the weights; keeps them small to limit overfitting. |
| patience | Training Wizard > Advanced Settings > Hyperparameters (PyTorch, TensorFlow) | 4 | Whole number, at least 1. | Epochs without validation improvement before training stops early. |
| n-estimators | Training Wizard > Advanced Settings > Hyperparameters (LightGBM) | 200 | Whole number, at least 1. | Number of boosted trees. |
| learning-rate | Training Wizard > Advanced Settings > Hyperparameters (LightGBM) | 0.05 | Number greater than 0. | Shrinkage of each tree. Smaller needs more trees but generalises better. |
| num-leaves | Training Wizard > Advanced Settings > Hyperparameters (LightGBM) | 31 | Whole number, at least 2. | Maximum leaves per tree: model complexity. |
| max-depth | Training Wizard > Advanced Settings > Hyperparameters (LightGBM) | -1 | Whole number, at least -1 (-1 means no limit). | Maximum tree depth. -1 means no limit. |
| min-child-samples | Training Wizard > Advanced Settings > Hyperparameters (LightGBM) | 20 | Whole number, at least 1. | Minimum samples in a leaf. Larger is more conservative. |
| subsample | Training Wizard > Advanced Settings > Hyperparameters (LightGBM) | 0.9 | Number greater than 0 and at most 1. | Share of rows used for each tree (0 to 1). |
| colsample-bytree | Training Wizard > Advanced Settings > Hyperparameters (LightGBM) | 0.9 | Number greater than 0 and at most 1. | Share of features used for each tree (0 to 1). |
| early-stopping | Training Wizard > Advanced Settings > Hyperparameters (LightGBM) | 30 | Whole number, at least 1. | Boosting rounds without validation improvement before training stops. |
| threshold | Training Wizard > Advanced Settings > Hyperparameters (PyTorch, TensorFlow, LightGBM) | 0.005 | Number, at least 0. | Return that separates Up / Down from Neutral when labelling (0.005 = 0.5 %). |
| seed | Training Wizard > Advanced Settings > Hyperparameters (PyTorch, TensorFlow, LightGBM) | 42 | Whole number, at least 0. | Random seed: the same seed and data give the same model. A different seed gives a different result (accuracy and attention contrast both vary). In an ensemble this is the run seed: bagging derives each member seed from it, stacking uses it for every member. |

#### Features tab

The Features tab composes the input channels of a model from price fields and indicators. Choose no channel to train the fixed OHLCV min-max model. Select at least one channel to train a composed_features model.

| Setting | Where | Default | Allowed values | What it does |
|---|---|---|---|---|
| Search indicators | Training Wizard > Features > Indicators | none (empty) | Free text. | Not described in the application. |
| Template name | Training Wizard > Features > Templates | none (empty) | Free text; used by Save template. | Not described in the application. |
| Normalization for newly added channels | Training Wizard > Features > Price / Indicators | None | None, WindowMinMax, WindowZScore. | None: value written through unchanged. WindowMinMax: min-max scaled to [0, 1] over the look-back window, this channel only. WindowZScore: population Z-score over the look-back window, this channel only. |
| Normalization for newly added channels (selected channel) | Training Wizard > Features > Selected | None | None, WindowMinMax, WindowZScore. | Normalization of the channel selected in the list of chosen channels (same values as above). |

#### Settings > Training Wizard (resource limits)

| Setting | Where | Default | Allowed values | What it does |
|---|---|---|---|---|
| Maximum channels | Settings > Training Wizard | off (no override) | On or off. | Optional limits for new composed-feature training jobs. An off item has no override: the child inherits its environment, and Python uses its own fallback for missing or invalid values. Resolved limits appear in training diagnostics after launch. Running jobs and AI prediction are unaffected. |
| Maximum channels (value) | Settings > Training Wizard | none | Whole number from 1 to 1024 (enabled limits only). | Caps the number of composed input channels (C). |
| Maximum MiB per input window | Settings > Training Wizard | off (no override) | On or off. | Optional limits for new composed-feature training jobs. An off item has no override: the child inherits its environment, and Python uses its own fallback for missing or invalid values. Resolved limits appear in training diagnostics after launch. Running jobs and AI prediction are unaffected. |
| Maximum MiB per input window (value) | Settings > Training Wizard | none | Whole number from 1 to 256 (enabled limits only). | Caps one float32 window: 4 x W x C bytes, where W is bars per window and C is channels. Measured in MiB; this is not total process memory (RSS). |
| Maximum training samples | Settings > Training Wizard | off (no override) | On or off. | Optional limits for new composed-feature training jobs. An off item has no override: the child inherits its environment, and Python uses its own fallback for missing or invalid values. Resolved limits appear in training diagnostics after launch. Running jobs and AI prediction are unaffected. |
| Maximum training samples (value) | Settings > Training Wizard | none | Whole number from 1 to 10000 (enabled limits only). | Caps each symbol's candidate windows and the cumulative retained train plus validation samples. Exceeding the cap fails training; samples are not automatically downsampled. |

### 4.2 Running a training

Press **Start** to start and **Cancel** to stop a running training. The progress bar, the current stage and the status message (including the reason a start was refused) appear in a strip above the buttons; the log is in the results section. When an input without a message of its own keeps **Start** disabled (for example no ticker is ticked, the prediction length is below 1 or the transfer fields are incomplete), the footer names it. A label appears in the footer while **Training start mode** is not "New training", while **Evaluate with CPCV** or **Pretrain the encoder (PyTorch)** is on, or while **Ensemble method** is not "Single model". When a training completes, the result can be deployed (section 4.3).

### 4.3 Using the result in Settings > AI Predictions

- **Deploy to Models** registers the model generation and tries to activate it. With the default settings the first model that fits the AI Predictions configuration becomes active. A model trained later on other data (another ticker or period) is registered but not active, because its evaluation data differs from the active model's: open Settings > AI Predictions, tick the model and press **Confirm activation** if asked. The status line says: "Model generation N is registered but not active. To use it, open Settings > AI Predictions, tick the model and, if asked, press Confirm activation. If it still does not become active, the message shown there names the reason."
- **Use as ensemble** makes the members of an ensemble result the active ensemble. Registered N ensemble member generations (none activated). Use "Use as ensemble" to make them the active ensemble.
- A model file can also be added in Settings > AI Predictions from a file; it is added to the list and not activated.

A model can be activated only when it agrees with the AI Predictions configuration:

| Item that must agree | Where it is set |
|---|---|
| Window size (input length) | Settings > AI Predictions |
| Feature mode | `appsettings.json`, section Prediction, `FeatureMode` |
| Composed channels | `appsettings.json`, section Prediction, `FeatureSpec` (a model trained with Features-tab channels needs this entry) |
| Class labels | `appsettings.json`, section Prediction, `ClassLabels` |

The chart time frame must also equal the model's time frame.

---

## 5. Reinforcement Learning reference (changing the defaults)

The agent reads 64 past daily bars and chooses Hold, Buy or Sell (or, with the position size and stop option, an entry budget and a stop distance). The window shows the training conditions under **Verification and execution conditions**.

### 5.1 Settings

| Setting | Where | Default | Allowed values | What it does |
|---|---|---|---|---|
| Symbol | Reinforcement Learning > Basic Settings > Training setup | The symbol of the chart that was open when the window opened | A ticker with stored daily prices. | Not described in the application. |
| Start Date | Reinforcement Learning > Basic Settings > Training setup | set automatically from the stored data of the symbol | A date before the end date. Start date must be earlier than end date. | The first day of the period: the stored history from this day to the end date holds the bars the training needs. A date you enter is kept. |
| End Date | Reinforcement Learning > Basic Settings > Training setup | set automatically from the stored data (the day after the latest stored day) | A date after the start date. The end date is not included. | The last stored day is the last day used. A date you enter is kept. |
| Initial equity | Reinforcement Learning > Basic Settings > Initial equity and costs | 1000000 (the Backtest default settings) | Initial equity above zero; commissions zero or more. (Enter valid costs: initial equity above zero, the other values zero or more; slippage and liquidation penalty below 1.) | Initial equity of every training episode. |
| Flat commission per fill | Reinforcement Learning > Basic Settings > Initial equity and costs | 0 (the Backtest default settings) | Initial equity above zero; commissions zero or more. (Enter valid costs: initial equity above zero, the other values zero or more; slippage and liquidation penalty below 1.) | Not described in the application. |
| Commission per unit | Reinforcement Learning > Basic Settings > Initial equity and costs | 0 (the Backtest default settings) | Initial equity above zero; commissions zero or more. (Enter valid costs: initial equity above zero, the other values zero or more; slippage and liquidation penalty below 1.) | Not described in the application. |
| Slippage ratio (0 inclusive, 1 exclusive) | Reinforcement Learning > Basic Settings > Initial equity and costs | 0 (the Backtest default settings) | From 0 (included) up to 1 (excluded). | Not described in the application. |
| Liquidation penalty ratio (0 inclusive, 1 exclusive) | Reinforcement Learning > Basic Settings > Initial equity and costs | 0.005 (the Backtest default settings) | From 0 (included) up to 1 (excluded). | Not described in the application. |
| Position size and stop learning | Reinforcement Learning > Advanced Settings > Run conditions | off | On or off. | Trains the position size and stop model instead of the trading direction model. |
| Trading direction model | Reinforcement Learning > Advanced Settings > Run conditions | none (choose one) | A saved trading direction model (shown with the position size and stop option only). | Not described in the application. |
| Use all complete validation and final-evaluation segments | Reinforcement Learning > Advanced Settings > Run conditions | off | On or off (position size and stop option only). | Evaluation scope: all complete validation and final-evaluation segments |
| Trial run (no model registration) | Reinforcement Learning > Advanced Settings > Run conditions | off | On or off. | Trial run: complete; model registration: none |
| Allow a short price history (use every complete segment) | Reinforcement Learning > Advanced Settings > Run conditions | off | On or off. | Direction model only. One complete training, validation and final-evaluation segment is enough instead of the usual per-partition segment count. The result is descriptive and is not statistically independent evidence. |
| Resume saved training | Reinforcement Learning > Advanced Settings > Training and resume | none (choose one after Inspect resume saves) | A verified resume save. | Resume training from the selected checkpoint. Click 'Inspect resume saves' to search for stored checkpoints. Select a checkpoint from the list to display resume and abandon actions. Note: Selected checkpoint has unverified integrity and cannot be resumed (release protection only). |
| Resume from selected save | Reinforcement Learning > Advanced Settings > Training and resume | off | On or off. | Check to unlock the 'Resume selected training' button. Ready: Click 'Resume selected training' to continue training from this checkpoint. |
| Release resume protection for selected run | Reinforcement Learning > Advanced Settings > Training and resume | off | On or off. | Check to unlock the 'Release resume protection' button. Ready: Click 'Release resume protection' to release protection for normal cleanup. |
| Saved models | Reinforcement Learning > Results and management > Models and trading replay | none (choose one) | A saved model. | Selects the saved model whose evaluation results are shown in this window. |
| Storage limit (GiB) | Reinforcement Learning > Results and management > Storage and retention | 10 | Number greater than 0 (GiB). | Not described in the application. |
| Inactive model retention (days) | Reinforcement Learning > Results and management > Storage and retention | none (the retention settings stay incomplete until it is set) | Whole number of days, at least 0. | Not described in the application. |
| Required free space (GiB) | Reinforcement Learning > Results and management > Storage and retention | 5 | Number, at least 0 (GiB). | Not described in the application. |
| Failed run retention (days) | Reinforcement Learning > Results and management > Storage and retention | 7 | Whole number of days, at least 0. | Not described in the application. |
| Inactive models to retain | Reinforcement Learning > Results and management > Storage and retention | none (the retention settings stay incomplete until it is set) | Whole number, at least 0. | Not described in the application. |
| Resume saves to retain | Reinforcement Learning > Results and management > Storage and retention | 2 | Whole number, at least 1. | Not described in the application. |
| Deletion of reviewed candidates | Reinforcement Learning > Results and management > Stored data and recovery | off | On or off. | Click 'Inspect stored data' to check for cleanup candidates. No candidates eligible for cleanup under current retention policy. |

### 5.2 Running a training

- **Start** starts the training; **Cancel operation** cancels the running operation; **Pause training** pauses a training and saves a resume point. **Start** stays disabled until the symbol, both dates and the costs are valid (and a trading direction model is chosen when the position size and stop option is on); next to the status the footer names the first input that is missing or invalid. A label appears next to the status while **Trial run (no model registration)**, **Allow a short price history (use every complete segment)** or **Position size and stop learning** is on.
- **Resume saved training:** **Inspect resume saves** lists the saved checkpoints. Select a verified one, tick **Resume from selected save** and press **Resume selected training**. **Release resume protection** releases the protection so that normal cleanup can remove the save.
- **Archives:** **Export Checkpoint**, **Import Checkpoint** and **Export Model** pack a checkpoint or a model into a `.zip` archive and read it back.
- **Storage and retention:** **Inspect stored data** lists what is stored and what would be deleted; the retention settings are saved when a training starts and they have changed; **Delete reviewed candidates** deletes the reviewed candidates after you tick **Deletion of reviewed candidates**. Nothing is deleted without that confirmation.
- **Initial equity and costs** start from the Settings > Backtest defaults. An edit applies to the training you start and is not saved as a default; to change the defaults, use Settings > Backtest.

### 5.3 Reviewing results

Select a saved model under **Saved models**. The window shows its identifier, its mean validation return and its mean final-evaluation return, the generation comparison ("Generation Comparison & Overfitting (Val vs OOS)") and the evaluation results with the comparison strategies. The comparison names the generation with the best validation return and shows its final-evaluation return only as a reference: the final evaluation is kept out of the choice on purpose. The pill strip at the top shows the measured mean return and the worst-episode MaxDD of the selected model's saved evaluation (final evaluation, or validation when there is none), with a line that gives the episode count, how many episodes ended above zero and the lowest episode return. Nothing is shown during training or before a model is selected; Sharpe and Profit Factor are not shown. **Activate selected model** marks the selected model as the active reinforcement learning model; replay in Settings > RL Playback does not depend on it.

### 5.4 Replaying an agent on the chart

Settings > RL Playback lists the saved agents, newest first. Pick a trained single-ticker reinforcement learning agent and an evaluation segment, then show on the main chart how the agent would have traded that stretch of past data.

| Setting | Where | Default | Allowed values | What it does |
|---|---|---|---|---|
| Evaluation segment | Settings > RL Playback > Replay trading | The first final-evaluation segment, otherwise the first segment | A validation or final-evaluation segment of the selected agent. | The stretch of the agent's own past data that is replayed. |
| Comparison strategy | Settings > RL Playback > Replay trading | The agent's own strategy | The agent's own strategy or a comparison strategy: No trading, Buy and hold, Moving-average strategy, Random strategy; the position size and stop model also offers Fixed size and stop strategy. | Which strategy is shown for the chosen segment. |
| Decision position (bars) | Settings > RL Playback > Replay trading | 0 | Whole number from 0 to 127 (the decisions of one segment). | The decision position, in bars, within the segment at which the Q-Panel reads the agent. |
| Ticker | Settings > RL Playback > Any ticker and date | The agent's own ticker | A ticker that has stored daily data in the application; type to search. | The ticker whose stored daily bars are replayed instead of the agent's own segment. |
| Suggest tickers from | Settings > RL Playback > Any ticker and date | All tickers | All tickers, or one of your ticker lists (watchlists, portfolios, categories and list folders). | Only narrows the suggestions while you type; it never changes the ticker. |
| Anchor date | Settings > RL Playback > Any ticker and date | The latest valid date of the chosen ticker | A trading day between the first and the last valid date shown under the field. A day without a bar uses the closest earlier trading day and the page tells you which day was used. | The last day the agent sees before its first decision; the 63 days before it are shown too. |

Replay on another ticker or date: the card "Any ticker and date" replays the selected agent on the stored daily bars of any ticker you choose, from the anchor date and the decision position (the Comparison strategy and Decision position fields are shared with the segment replay above). The replay is an episode of 192 daily bars: the agent sees 64 bars up to the anchor date and then decides on the next 128 bars from a flat account with the costs it was trained with. For that reason the first 63 stored bars of a ticker and its newest 128 completed trading days cannot be anchor dates, and the page shows the first and the last valid date after you choose a ticker. Only completed days are used. The page opens with the agent's own ticker and the latest valid date, so one click on **Show on chart** works. Under the chart a note tells you whether the shown days overlap, lie before or lie after the agent's source period (the training, validation and final-evaluation parts together), whether the ticker is a different one, and which price items (for example Open or Volume) lie outside the range the agent was trained on. For the agent itself that check covers every day it has seen from the first decision to the shown one; for a comparison strategy it covers the shown 64 days. The note also tells you when the days shown contain a longer calendar gap or a bigger day-to-day price change than anything the agent was trained on (for example a stock split or missing days in the stored prices). The stored prices are not checked for splits or missing days, so such a note means "unlike the training data", not a diagnosis. Results on other tickers or other dates are not validated, and the agent still does not predict the future. Agents that choose a position size and a stop (continuous agents) cannot be replayed this way.

Press **Show on chart** to show the replay and **Remove replay from chart** to remove it from the chart. The chart switches to the daily time frame and shows the 64 bars the agent saw. The Q-Panel under the chart shows the model identifier and the policy, the unit of the values ("Action value (expected discounted cumulative initial-equity ratio); discount factor 0.99" with the discount factor of the contract), a legend ("Maximum action value <action> · <date>") and the three values: Hold, Buy, Sell.

### 5.5 What RL files cannot do

The replay uses only the agent's own saved training data (daily bars, past data). It does not predict the future and cannot be registered in Settings > AI Predictions. Agents work on one ticker's daily bars and are tied to the price scale of the data they were trained on. They are not used by Backtest or Screener.

---

## 6. Portfolio Reinforcement Learning reference (changing the defaults)

### 6.1 Settings

| Setting | Where | Default | Allowed values | What it does |
|---|---|---|---|---|
| Ticker lists | Portfolio Reinforcement Learning > Basic Settings > Ticker lists | none ticked | Any of the listed sources (all tickers, watchlists, portfolios, tags, saved filters, categories, lists). | The tickers of the ticked sources form the portfolio universe. |
| Start Date | Portfolio Reinforcement Learning > Basic Settings > Period | set automatically from the data check | A date before the end date. Enter a start date that is before the end date. | Start date set automatically: N bars are needed before N. Use Recalculate start date to set it again. |
| End Date | Portfolio Reinforcement Learning > Basic Settings > Period | the day after the latest stored day of the ticked lists (not later than today); the last confirmed day when no data is stored | A date after the start date and not later than today. | The end date is not included. A date you enter is kept. |
| Initial equity | Portfolio Reinforcement Learning > Basic Settings > Initial equity and costs | 1000000 (the Backtest default settings) | Initial equity above zero; the other costs zero or more. (Enter valid costs: initial equity above zero, the other values zero or more; slippage and liquidation penalty below 1.) | Not described in the application. |
| Flat commission per fill | Portfolio Reinforcement Learning > Basic Settings > Initial equity and costs | 0 (the Backtest default settings) | Initial equity above zero; the other costs zero or more. (Enter valid costs: initial equity above zero, the other values zero or more; slippage and liquidation penalty below 1.) | Not described in the application. |
| Commission per unit | Portfolio Reinforcement Learning > Basic Settings > Initial equity and costs | 0 (the Backtest default settings) | Initial equity above zero; the other costs zero or more. (Enter valid costs: initial equity above zero, the other values zero or more; slippage and liquidation penalty below 1.) | Not described in the application. |
| Slippage ratio (0 inclusive, 1 exclusive) | Portfolio Reinforcement Learning > Basic Settings > Initial equity and costs | 0 (the Backtest default settings) | Initial equity above zero; the other costs zero or more. (Enter valid costs: initial equity above zero, the other values zero or more; slippage and liquidation penalty below 1.) | Not described in the application. |
| Liquidation penalty ratio (0 inclusive, 1 exclusive) | Portfolio Reinforcement Learning > Basic Settings > Initial equity and costs | 0.005 (the Backtest default settings) | From 0 (included) up to 1 (excluded). (Enter valid costs: initial equity above zero, the other values zero or more; slippage and liquidation penalty below 1.) | Not described in the application. |
| Continue without the symbols that cannot be used | Portfolio Reinforcement Learning > Basic Settings > Data check (bars in period / bars needed) | off | On or off (shown only when a symbol cannot be used). | Some symbols cannot be used. Tick the box to continue without them. |
| Trial run (no model registration) | Portfolio Reinforcement Learning > Advanced Settings > Evaluation | off | On or off. | Not described in the application. |
| Short-history evaluation (one complete segment per partition is enough) | Portfolio Reinforcement Learning > Advanced Settings > Evaluation | off | On or off. | The standard evaluation uses the fixed minimum of N / N / N blocks (train / validation / test). The partition settings apply to the short-history evaluation. |
| Symbol holdout: hold out some symbols for testing (needs at least 4 usable symbols) | Portfolio Reinforcement Learning > Advanced Settings > Evaluation | on | On or off. | The symbol holdout needs at least 4 usable symbols, so this run evaluates the time holdout only. |
| Share of symbols held out (%) | Portfolio Reinforcement Learning > Advanced Settings > Evaluation | 25 | Whole number from 1 to 50 (values below 10 or above 40 are outside the standard range). | Share of the symbols (rounded up, at least two symbols) that is held out of training. |
| Largest weight of one symbol (%) | Portfolio Reinforcement Learning > Advanced Settings > Evaluation | 100 | Whole number from 1 to 100. | Largest share of the account one asset may take at a rebalance; 100 means no concentration cap. |
| Ratio (3:1:1 of the calendar) | Portfolio Reinforcement Learning > Advanced Settings > Partition of the short-history evaluation | selected | One of the two partition choices. | The calendar is split 3:1:1 into train, validation and test; needs at least N bars. |
| Block counts (counted back from the latest date) | Portfolio Reinforcement Learning > Advanced Settings > Partition of the short-history evaluation | not selected | One of the two partition choices. | Each partition takes the stated number of whole blocks, counted back from the latest date; needs at least N bars. |
| Minimum train bars | Portfolio Reinforcement Learning > Advanced Settings > Partition of the short-history evaluation | 192 | Whole number of bars from 192 to 15360 (values above 960 are outside the standard range). | Fewest bars the train partition holds, rounded up to whole blocks. Short-history evaluation only. |
| Train blocks | Portfolio Reinforcement Learning > Advanced Settings > Partition of the short-history evaluation | 1 | Whole number of blocks (one block is 192 bars). | The same setting as the minimum train bars, written in blocks. |
| Minimum validation bars | Portfolio Reinforcement Learning > Advanced Settings > Partition of the short-history evaluation | 192 | Whole number of bars from 192 to 15360 (values above 960 are outside the standard range). | Fewest bars the validation partition holds, rounded up to whole blocks. Short-history evaluation only. |
| Validation blocks | Portfolio Reinforcement Learning > Advanced Settings > Partition of the short-history evaluation | 1 | Whole number of blocks (one block is 192 bars). | The same setting as the minimum validation bars, written in blocks. |
| Minimum test bars | Portfolio Reinforcement Learning > Advanced Settings > Partition of the short-history evaluation | 192 | Whole number of bars from 192 to 15360 (values above 960 are outside the standard range). | Fewest bars the test partition holds, rounded up to whole blocks. Short-history evaluation only. |
| Test blocks | Portfolio Reinforcement Learning > Advanced Settings > Partition of the short-history evaluation | 1 | Whole number of blocks (one block is 192 bars). | The same setting as the minimum test bars, written in blocks. |
| Train from scratch | Portfolio Reinforcement Learning > Advanced Settings > Base model (transfer) | selected | One of the three run kinds. | A base model is an earlier verified run. Its data must end no later than the day the validation of this run starts, so the model has never seen the data it is judged on. |
| Fine-tune a stored base model | Portfolio Reinforcement Learning > Advanced Settings > Base model (transfer) | not selected | One of the three run kinds. | A base model is an earlier verified run. Its data must end no later than the day the validation of this run starts, so the model has never seen the data it is judged on. |
| Evaluate a stored base model without training | Portfolio Reinforcement Learning > Advanced Settings > Base model (transfer) | not selected | One of the three run kinds. | A base model is an earlier verified run. Its data must end no later than the day the validation of this run starts, so the model has never seen the data it is judged on. |
| Panel | Portfolio Reinforcement Learning > Advanced Settings > Base model (transfer) | Capture a new panel (default) | The default or a stored panel. | A run normally captures a new panel from the local data. Choose a stored panel to run on exactly the same data again, for example to compare a fine-tune with a run from scratch. |
| Base run | Portfolio Reinforcement Learning > Advanced Settings > Base model (transfer) | none (choose one) | A stored verified run with a complete model. | A base model is an earlier verified run. Its data must end no later than the day the validation of this run starts, so the model has never seen the data it is judged on. |
| Keep the shared encoder frozen (only the heads train) | Portfolio Reinforcement Learning > Advanced Settings > Base model (transfer) | on | On or off. | Keeps the shared encoder at the base weights; only the heads train. |
| Run A (for example the fine-tune) | Portfolio Reinforcement Learning > Results and management > Compare two runs | none | A stored run that has an evaluation report. | Two runs made on the same stored panel are paired segment by segment. The result is descriptive: it does not show that a stored model helps. |
| Run B (the reference, same panel) | Portfolio Reinforcement Learning > Results and management > Compare two runs | none | A stored run made on the same panel as run A. | Two runs made on the same stored panel are paired segment by segment. The result is descriptive: it does not show that a stored model helps. |

### 6.2 Running a training

The data check shows, for every ticker, the bars in the period and the bars needed per segment. At least 2 usable tickers are needed; with the symbol holdout at least 4 ("Symbol holdout: hold out some symbols for testing (needs at least 4 usable symbols)"). The role of a ticker in the check: An evaluated-only symbol is traded by the policy in validation and test, but the policy is never trained on it. Press **Start** when the check is sufficient. A run can start from scratch, fine-tune a stored base model or evaluate a stored base model without training. While **Start** is disabled, the footer next to the status names the reason (for example a missing list, an invalid period, costs or percentage, or a base run that is not chosen). A label appears next to the status while **Trial run (no model registration)**, **Short-history evaluation (one complete segment per partition is enough)**, **Fine-tune a stored base model** or **Evaluate a stored base model without training** is selected, or while **Symbol holdout: hold out some symbols for testing (needs at least 4 usable symbols)** is cleared.

### 6.3 Reviewing results and transfer evaluation

**Evaluation result (descriptive)** shows, for every training or holdout cell, the number of segments and the mean return, and the comparison with the baselines (Cash, Equal-weight buy and hold, Equal-weight, rebalanced daily). Stored prices are not verified as point-in-time data. **Compare two runs** pairs two runs made on the same stored panel: Two runs made on the same stored panel are paired segment by segment. The result is descriptive: it does not show that a stored model helps.

### 6.4 What Portfolio RL files cannot do

Nothing a portfolio run creates is registered or activated, and nothing is shown on a chart. Its model is not compatible with the Settings > AI Predictions price models, and its output is evaluated inside the window only.

---

## 7. Files and folders

All folders are below the `Data` folder.

| File | Produced by | Folder | Safe to delete |
|---|---|---|---|
| `<model>.onnx` | Training Wizard | `TrainingArtifacts/TW` (working copy), `Models/Generations` (registered copy) | Registered copy: remove it with the remove action in Settings > AI Predictions. Working copy: Do not delete manually |
| `<model>.onnx.metrics.json`, `<model>.onnx.scaler.json`, `<model>.onnx.analysis.json` | Training Wizard (the scaler and analysis files where the trainer writes them) | Next to the model | Do not delete manually |
| Experiment records | Training Wizard | `Experiments/<run id>` | Do not delete manually |
| `policy.onnx`, `policy.onnx.metrics.json`, `policy.pt`, `parity.json`, `dependencies.json` | Reinforcement Learning (model, evaluation report, native policy, output check, dependency record) | `TrainingArtifacts/RL/...` and `Models/Generations/RL/...` | Use Storage and retention in the Reinforcement Learning window |
| `job.json`, `source.json`, `bars.json`, `actions.json`, `source.parquet`, `metadata.parquet`, `catalog.json`, `baseline_actions.json` | Reinforcement Learning (saved job and limits, source description, price bars, corporate actions, copies of the price and metadata files, episode catalog, comparison-strategy actions) | `TrainingArtifacts/RL/...` and `Models/Generations/RL/...` | Use Storage and retention in the Reinforcement Learning window |
| `manifest.json`, `active.json` | Reinforcement Learning (generation manifest, pointer to the active model) | `Models/Generations/RL` | Use Storage and retention in the Reinforcement Learning window |
| Resume saves | Reinforcement Learning | `TrainingArtifacts/RL/...` | Release resume protection, then Storage and retention |
| `.zip` archives | Reinforcement Learning export | The folder you choose | Yes: it is your copy |
| `portfolio_manifest.json` and the model and evaluation files it lists | Portfolio Reinforcement Learning | `TrainingArtifacts/RLPortfolio/<run id>` | Do not delete manually |
| `panel.json` and the `symbols` folder | Portfolio Reinforcement Learning (stored panel) | `TrainingArtifacts/RLPortfolio/panels/<panel id>` | Do not delete manually |

---

## 8. Limitations and disclaimer

- The results are descriptive evidence about past data. They are not investment advice and do not establish that a strategy works.
- A replay is historical. It uses the agent's own saved data and does not predict the future. Stored prices are not verified as point-in-time data.
- Nothing here places live orders.
- Statistical superiority: not established. Descriptive only: short-history segments are not statistically independent evidence, and investment efficacy is not established.

---

## 9. Troubleshooting

| Symptom | Cause | Action |
|---|---|---|
| The Training Wizard says the model is registered but not active | Another model is active and this one was evaluated on different data, is not better, or has another window size | Open Settings > AI Predictions, tick the model and press **Confirm activation** if asked. The message shown there names the reason. |
| The prediction is not shown on the chart | The model is not active, or the chart time frame differs from the model's time frame | Activate the model in Settings > AI Predictions and open a chart with the model's time frame (daily by default). |
| Training stops with insufficient history | The period holds fewer bars than the window, horizon and splits need | Move the start date earlier or reduce the input length; the Training Wizard shows the bars required. Reinforcement Learning says: "Not enough price history for this symbol and period to build the required independent episodes. Use an earlier start date, or enable the trial run." |
| Replay is unavailable for an agent | The agent's stored data cannot be read or the agent is not usable | Press **Refresh** on the RL Playback page and read the message; inspect the agent in the Reinforcement Learning window. |
| Python packages are missing | The training environment is not installed | Use **Automatic Setup** in Settings > AI Predictions > ONNX Environment & Dependencies, or **Configure Manually**. |
