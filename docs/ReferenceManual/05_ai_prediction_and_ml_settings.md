# 05. AI Prediction & Machine Learning Settings

This document covers all user configuration files governing AI trend predictions, Python IPC integration, ensemble prediction models, training resource limits, and reinforcement learning storage retention.

---

## 1. AI Prediction Settings (`user_prediction_settings.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Config/user_prediction_settings.json` |
| **Path Resolver** | `PathDiscovery.ResolveConfigPath("user_prediction_settings.json")` |
| **Managing Class** | `StockAnalyzer.Avalonia.Services.PredictionSettingsManager` |

Stores user overrides for runtime inference window size.

```json
{
  "WindowSize": 60
}
```

- **`WindowSize`** (`integer`, default `60`): Number of sequential bars fed into the ONNX prediction input tensor.

---

## 2. Python Environment Settings (`user_python_settings.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Config/user_python_settings.json` |
| **Path Resolver** | `PathDiscovery.ResolveConfigPath("user_python_settings.json")` |
| **Managing Class** | `StockAnalyzer.Avalonia.Services.PythonSettingsManager` |

```json
{
  "ShowUpdateConfirmationDialog": true
}
```

- **`ShowUpdateConfirmationDialog`** (`boolean`, default `true`): When `false`, suppresses prompt dialogs when newer package versions are detected, skipping manual confirmations.

---

## 3. Ensemble Prediction Model (`user_ensemble_settings.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Config/user_ensemble_settings.json` |
| **Path Resolver** | `PathDiscovery.ResolveConfigPath("user_ensemble_settings.json")` |
| **Managing Class** | `StockAnalyzer.Core.Services.EnsembleSettingsManager` (`IEnsembleSettingsManager`) |
| **Model Class** | `StockAnalyzer.Core.Services.EnsembleSpec` |

Defines an ensemble of multiple trained ONNX model generations combining their class probability distributions into a weighted consensus prediction.

### Invariants & Validation Constraints
- **Member Count**: Between 1 and 10 members (`MaximumMembers = 10`).
- **Distinct Models**: `ModelId` strings must be unique within the members list.
- **Weights**: Each `Weight` must be finite and $\ge 0$.
- **Sum of Weights**: Weights must sum to $1.0$ within a floating-point tolerance of $1 \times 10^{-4}$ ($| \sum w_i - 1.0 | \le 0.0001$).
- **Compatibility**: All model generations in an ensemble must share identical target classification definitions (same timeframe, prediction horizon, label set, and neutral threshold).

### Reference Example
```json
{
  "Members": [
    {
      "ModelId": "gen_20261015_lstm_d1",
      "Weight": 0.6
    },
    {
      "ModelId": "gen_20261020_transformer_d1",
      "Weight": 0.4
    }
  ]
}
```

---

## 4. Training Wizard Resource Limits (`user_training_resource_settings.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Config/user_training_resource_settings.json` |
| **Path Resolver** | `PathDiscovery.ResolveConfigPath("user_training_resource_settings.json")` |
| **Managing Class** | `StockAnalyzer.Core.Services.TrainingResourceSettings` (`ITrainingResourceSettings`) |
| **Schema Version** | `1` (`schema_version = 1` required) |

Protects system memory and prevents OOM crashes during model training in the Training Wizard.

### Schema Specification

| Property | Type | Default | Description |
|---|---|---|---|
| `schema_version` | `integer` | `1` | Schema version identifier (strictly enforced; unexpected version aborts load). |
| `max_channels` | `integer` | `64` | Maximum combined input feature channels allowed for dataset generation. |
| `max_tensor_size_mib` | `integer` | `1024` | Maximum memory footprint in MiB allocated to training batch tensors. |
| `max_samples` | `integer` | `100000` | Maximum historical time-series samples included in training dataset. |

### Reference Example
```json
{
  "schema_version": 1,
  "max_channels": 64,
  "max_tensor_size_mib": 1024,
  "max_samples": 100000
}
```

---

## 5. Reinforcement Learning Storage Policy (`user_rl_artifact_settings.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Config/user_rl_artifact_settings.json` |
| **Path Resolver** | `PathDiscovery.ResolveConfigPath("user_rl_artifact_settings.json")` |
| **Managing Class** | `StockAnalyzer.Core.Services.ReinforcementLearning.RlArtifactSettings` |
| **Model Class** | `StockAnalyzer.Core.Services.ReinforcementLearning.RlArtifactPolicy` |
| **Schema Version** | `1` |

Defines disk quota and retention rules for checkpoints and policy weights generated during reinforcement learning training runs.

### Schema Specification

| Property | Type | Default | Description |
|---|---|---|---|
| `SchemaVersion` | `integer` | `1` | Format schema version. |
| `Policy.QuotaGiB` | `decimal` | `10.0` | Maximum disk space in GiB dedicated to RL artifacts under `Data/TrainingArtifacts/`. |
| `Policy.MinimumFreeGiB` | `decimal` | `5.0` | Minimum free disk space required on host drive before training starts. |
| `Policy.FailedRetentionDays` | `integer` | `7` | Retention period in days for aborted or crashed training run checkpoints. |
| `Policy.NonactiveRetentionDays` | `integer?` | `30` | Retention days for superseded model generations. |
| `Policy.NonactiveKeepCount` | `integer?` | `5` | Minimum count of historical generations retained regardless of age. |
| `Policy.CheckpointKeepCount` | `integer` | `3` | Maximum rolling checkpoints saved per active training session. |

### Reference Example
```json
{
  "SchemaVersion": 1,
  "Policy": {
    "QuotaGiB": 10.0,
    "MinimumFreeGiB": 5.0,
    "FailedRetentionDays": 7,
    "NonactiveRetentionDays": 30,
    "NonactiveKeepCount": 5,
    "CheckpointKeepCount": 3
  }
}
```
