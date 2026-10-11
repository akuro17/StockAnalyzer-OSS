using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs
{
    /// <summary>
    /// One line of the model list: a group header (not selectable) or a registered generation. The same list shows both so the models that can be
    /// mixed into one ensemble stand together under one header.
    /// </summary>
    public sealed class GenerationRow
    {
        private GenerationRow(ModelGenerationSummary? summary, string headerText, string? groupKey)
        {
            Summary = summary;
            HeaderText = headerText;
            GroupKey = groupKey;
        }

        public static GenerationRow Header(string text) => new(null, text, null);
        public static GenerationRow Item(ModelGenerationSummary summary, string? groupKey) => new(summary, string.Empty, groupKey);

        public ModelGenerationSummary? Summary { get; }
        public string HeaderText { get; }

        /// <summary>The ensemble compatibility key of the generation (null = cannot be an ensemble member, or not known yet).</summary>
        public string? GroupKey { get; }

        public bool IsHeader => Summary is null;
        public bool IsSelectable => Summary is not null;
        public bool IsItem => Summary is not null;
        public bool IsActive => Summary?.IsActive == true;
        public string ModelFile => Summary?.ModelFile ?? string.Empty;
        public string TargetType => Summary?.TargetType ?? string.Empty;
        public string Timeframe => Summary?.Timeframe ?? string.Empty;
        public int Horizon => Summary?.Horizon ?? 0;
        public string MetricName => Summary?.MetricName ?? string.Empty;
        public double Score => Summary?.Score ?? 0;

        /// <summary>Everything the row shows, one fact per line, for the hover tip (the row itself is cut off when a file name is long); null for a header.</summary>
        public string? Tooltip => Summary is null ? null : string.Join("\n", new[]
        {
            Line("Settings_ModelGenerations_Tip_File", Summary.ModelFile),
            Line("Settings_ModelGenerations_Tip_Target", Summary.TargetType),
            Line("Settings_ModelGenerations_Tip_Timeframe", Summary.Timeframe),
            Line("Settings_ModelGenerations_Tip_Horizon", Summary.Horizon),
            Line("Settings_ModelGenerations_Tip_Score", Summary.MetricName, Summary.Score),
            LocalizationManager.Instance[Summary.IsActive ? "Settings_ModelGenerations_Tip_Active" : "Settings_ModelGenerations_Tip_NotActive"],
        });

        private static string Line(string key, params object[] arguments) =>
            string.Format(System.Globalization.CultureInfo.CurrentCulture, LocalizationManager.Instance[key], arguments);
    }

    public partial class AIPredictionsSettingsViewModel
    {
        private long _groupingRevision;
        private bool _rebuildingRows;

        /// <summary>The model list as the view shows it: group headers and generations.</summary>
        public ObservableCollection<GenerationRow> GenerationRows { get; } = new();

        [ObservableProperty]
        private GenerationRow? _selectedGenerationRow;

        /// <summary>Completes when the grouping of the current list is published (the flat list shows until then).</summary>
        public Task GenerationGroupingTask { get; private set; } = Task.CompletedTask;

        /// <summary>Why the selected model cannot be added to the ensemble now (null = it can, or nothing is selected).</summary>
        public string? AddEnsembleMemberHint { get; private set; }

        /// <summary>
        /// The check box of a row: the same as selecting the row and pressing the activation button (activate it, or deactivate it when it is the active
        /// one), so every rule, warning and confirmation of the button applies. The box always shows the registry state, so the rows are built again
        /// afterwards, also when the request was refused or still waits for a confirmation.
        /// </summary>
        [RelayCommand]
        private async Task ToggleGenerationRowActivationAsync(GenerationRow? row)
        {
            if (row?.Summary is not { } summary) return;
            try
            {
                SelectedGeneration = summary;
                await ToggleGenerationActivationAsync();
            }
            finally { RebuildGenerationRows(); }
        }

        partial void OnSelectedGenerationRowChanged(GenerationRow? value)
        {
            if (_rebuildingRows) return;
            var summary = value?.Summary;
            if (!Equals(summary, SelectedGeneration)) SelectedGeneration = summary;
        }

        /// <summary>The list selection follows <see cref="SelectedGeneration"/> (set by the commands and by tests).</summary>
        private void SyncRowSelection()
        {
            var row = SelectedGeneration is null ? null : GenerationRows.FirstOrDefault(r => Equals(r.Summary, SelectedGeneration));
            if (!ReferenceEquals(row, SelectedGenerationRow))
            {
                _rebuildingRows = true;
                try { SelectedGenerationRow = row; }
                finally { _rebuildingRows = false; }
            }

            UpdateAddEnsembleMemberAvailability();
        }

        /// <summary>The flat list right away, then the same list with group headers once the generations' ensemble contracts are read.</summary>
        private void RebuildGenerationRows()
        {
            var summaries = Generations.ToArray();
            ReplaceRows(summaries.Select(s => GenerationRow.Item(s, null)));
            var revision = ++_groupingRevision;
            var registry = _modelRegistry;
            if (registry is null || summaries.Length == 0) { GenerationGroupingTask = Task.CompletedTask; return; }

            (ModelGenerationSummary Summary, EnsembleCompatibility? Compatibility)[] Describe() => summaries.Select(s =>
                (Summary: s, Compatibility: registry.GetManifest(s.ModelId) is { } manifest ? EnsembleSettingsManager.Describe(manifest, registry) : null)).ToArray();
            void Publish((ModelGenerationSummary Summary, EnsembleCompatibility? Compatibility)[] described)
            {
                if (_isDisposed || revision != _groupingRevision) return;
                ReplaceRows(BuildGroupedRows(described));
            }

            // With a UI dispatcher the contracts are read on a worker thread (each is an ONNX metadata read, cached per generation) and published on
            // the UI thread; without one (design time, unit tests) everything runs in the caller, so no thread ever touches the rows concurrently.
            if (_analysisDispatcher is null)
            {
                Publish(Describe());
                GenerationGroupingTask = Task.CompletedTask;
                return;
            }

            GenerationGroupingTask = Task.Run(() =>
            {
                var described = Describe();
                _analysisDispatcher.Post(() => Publish(described));
            });
        }

        private void ReplaceRows(IEnumerable<GenerationRow> rows)
        {
            var selected = SelectedGeneration;
            _rebuildingRows = true;
            try
            {
                GenerationRows.Clear();
                foreach (var row in rows) GenerationRows.Add(row);
                SelectedGenerationRow = selected is null ? null : GenerationRows.FirstOrDefault(r => Equals(r.Summary, selected));
            }
            finally { _rebuildingRows = false; }

            UpdateAddEnsembleMemberAvailability();
        }

        private IEnumerable<GenerationRow> BuildGroupedRows(
            IReadOnlyList<(ModelGenerationSummary Summary, EnsembleCompatibility? Compatibility)> described)
        {
            var groups = described.Where(d => d.Compatibility is not null)
                .GroupBy(d => d.Compatibility!.Key)
                .OrderBy(g => g.First().Compatibility!.Timeframe, StringComparer.Ordinal)
                .ThenBy(g => g.First().Compatibility!.Horizon)
                .ThenBy(g => g.Key, StringComparer.Ordinal);
            foreach (var group in groups)
            {
                yield return GenerationRow.Header(GroupHeader(group.First().Compatibility!, group.Count()));
                foreach (var member in group.OrderBy(d => d.Summary.ModelFile, StringComparer.Ordinal).ThenBy(d => d.Summary.ModelId, StringComparer.Ordinal))
                    yield return GenerationRow.Item(member.Summary, group.Key);
            }

            var others = described.Where(d => d.Compatibility is null)
                .OrderBy(d => d.Summary.ModelFile, StringComparer.Ordinal).ThenBy(d => d.Summary.ModelId, StringComparer.Ordinal).ToArray();
            if (others.Length == 0) yield break;
            yield return GenerationRow.Header(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                LocalizationManager.Instance["Settings_ModelGenerations_Group_NotCapable"], others.Length));
            foreach (var other in others) yield return GenerationRow.Item(other.Summary, null);
        }

        private static string GroupHeader(EnsembleCompatibility group, int count)
        {
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            string Text(string key, object argument) => string.Format(culture, LocalizationManager.Instance[key], argument);
            var parts = new List<string>
            {
                group.Timeframe,
                Text("Settings_ModelGenerations_Group_Horizon", group.Horizon),
                group.Labels.Replace(",", "/"),
            };
            if (!string.IsNullOrWhiteSpace(group.NeutralThreshold)) parts.Add(Text("Settings_ModelGenerations_Group_Threshold", group.NeutralThreshold));
            if (!string.IsNullOrWhiteSpace(group.PriceAdjustment)) parts.Add(Text("Settings_ModelGenerations_Group_Prices", group.PriceAdjustment));
            return string.Join(" · ", parts) + " " + Text("Settings_ModelGenerations_Group_Count", count);
        }

        private bool CanAddEnsembleMember() => AddEnsembleMemberHint is null && SelectedGeneration is not null
            && EnsembleMembers.Count < EnsembleSpec.MaximumMembers && EnsembleMembers.All(row => row.ModelId != SelectedGeneration.ModelId);

        /// <summary>
        /// Adding is allowed within one group only: the selected model must share the group of the members already listed. The hint tells why not;
        /// groups are known after <see cref="GenerationGroupingTask"/>, before that only the plain rules apply.
        /// </summary>
        private void UpdateAddEnsembleMemberAvailability()
        {
            string? hint = null;
            var selected = SelectedGenerationRow;
            if (selected is { IsItem: true } && GenerationRows.Any(r => r.IsHeader))
            {
                if (selected.GroupKey is null)
                    hint = LocalizationManager.Instance["Settings_Ensemble_Hint_NotCapable"];
                else
                {
                    var memberKeys = EnsembleMembers
                        .Select(m => GenerationRows.FirstOrDefault(r => r.Summary?.ModelId == m.ModelId)?.GroupKey)
                        .Where(key => key is not null).Distinct().ToArray();
                    if (memberKeys.Length > 0 && !memberKeys.Contains(selected.GroupKey))
                        hint = LocalizationManager.Instance["Settings_Ensemble_Hint_OtherGroup"];
                }
            }

            if (hint != AddEnsembleMemberHint)
            {
                AddEnsembleMemberHint = hint;
                OnPropertyChanged(nameof(AddEnsembleMemberHint));
            }

            AddEnsembleMemberCommand.NotifyCanExecuteChanged();
        }
    }
}
