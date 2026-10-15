using System.Globalization;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.UITests.Infrastructure;
using StockAnalyzer.UITests.PageObjects;
using Xunit.Abstractions;

namespace StockAnalyzer.UITests.Tests;

/// <summary>Real local training and a real qualified model, entirely in a disposable desktop-test data root.</summary>
[Collection("UI Tests")]
public sealed partial class RlStoredPriceDesktopTests(ITestOutputHelper output) : UITestBase(output)
{
    public const string QualifiedDirectoryVariable = "SA_RL_UI_QUALIFIED_DIRECTORY";
    public const string EvidenceDirectoryVariable = "SA_RL_UI_EVIDENCE_DIRECTORY";
    public const string ContinuousDirectoryVariable = "SA_RL_T16_ACTOR_DIRECTORY";
    private const string Symbol = "AAPL";
    private const int CompletionTimeoutMs = 180_000;
    /// <summary>A T16 run records its whole execution environment before it starts (3 min 14 s measured, plus a second verification); waits that include that start use this bound.</summary>
    private const int StrictRunTimeoutMs = 600_000;
    /// <summary>Whole-test bounds (xUnit <c>Timeout</c>): a test with one local run, with one strict T16 run, and with two strict T16 runs (or one run plus a resumed one).</summary>
    private const int LocalRunTestTimeoutMs = 600_000;
    private const int OneStrictRunTestTimeoutMs = 1_500_000;
    private const int TwoStrictRunsTestTimeoutMs = 2_400_000;
    private const int StableLayoutSamples = 3;
    private const int CheckpointScrollSteps = 3;

    [Fact(Timeout = TwoStrictRunsTestTimeoutMs)]
    public void R09_RealPpoPauseReviewAndExplicitResumeThroughOwnedDesktop()
    {
        string evidence = Environment.GetEnvironmentVariable(EvidenceDirectoryVariable)!;
        using var config = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("SA_RL_R07_QUALIFICATION_CONFIG")!));
        string actor = config.RootElement.GetProperty("LaterActor").GetString()!;
        using var actorJob = JsonDocument.Parse(File.ReadAllText(Path.Combine(actor, RlFiles.Job)));
        LaunchApplication(root =>
        {
            string metadata = PathDiscovery.ResolveDataPath(null, MarketDataSettings.DefaultMetadataDataPath);
            File.Copy(Path.Combine(actor, RlFiles.MetadataParquet), Path.Combine(metadata, Symbol + ".meta.parquet"));
            using var registry = new ModelGenerationRegistry(); registry.ConfigureRlArtifacts(root, new RlArtifactSettings());
            registry.SaveRlArtifactPolicyAsync(new(MinimumFreeGiB: FixtureMinimumFreeGiB, NonactiveRetentionDays: 0, NonactiveKeepCount: 1)).GetAwaiter().GetResult();
            registry.RegisterRlAsync(config.RootElement.GetProperty("LaterDirection").GetString()!).GetAwaiter().GetResult();
        });
        Window window = new MainWindowPage(MainWindow!, this).OpenRlTrainingWindow();
        window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
        Element(window, RlUiConstants.ContinuousMode).AsCheckBox().IsChecked = true;
        Element(window, RlUiConstants.FrozenDirection).AsComboBox().Select(0);
        Text(window, RlUiConstants.Symbol, Symbol);
        Text(window, RlUiConstants.StartDate, actorJob.RootElement.GetProperty("start_date").GetString()!);
        Text(window, RlUiConstants.EndDate, actorJob.RootElement.GetProperty("end_date_exclusive").GetString()!);
        Element(window, RlUiConstants.Smoke).AsCheckBox().IsChecked = true;
        Invoke(window, RlUiConstants.Train);
        WaitUntil(() => Element(window, RlUiConstants.Pause).IsEnabled, StrictRunTimeoutMs, "Pause was unavailable during owned PPO training");
        Invoke(window, RlUiConstants.Pause); WaitState(window, "Paused");
        var header = WaitForElement(() => window.FindAllDescendants().FirstOrDefault(e => Localized("Rl.Checkpoint.Title").Contains(e.Properties.Name.ValueOrDefault)),
            errorMessage: "Checkpoint panel header missing");
        WaitUntil(() =>
        {
            if (!header.IsOffscreen && window.BoundingRectangle.Contains(header.BoundingRectangle)) return true;
            window.Focus(); var bounds = window.BoundingRectangle;
            Mouse.MoveTo((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
            Mouse.Scroll(-CheckpointScrollSteps); return false;
        }, errorMessage: "Checkpoint header could not be scrolled into view");
        string priorBounds = ""; int stable = 0;
        WaitUntil(() => { string bounds = header.BoundingRectangle.ToString(); stable = bounds == priorBounds ? stable + 1 : 0; priorBounds = bounds; return stable >= StableLayoutSamples; },
            errorMessage: "Checkpoint header layout did not stabilize");
        LogInfo("Checkpoint header patterns: expand=" + header.Patterns.ExpandCollapse.IsSupported
            + "; toggle=" + header.Patterns.Toggle.IsSupported + "; invoke=" + header.Patterns.Invoke.IsSupported);
        if (header.Patterns.ExpandCollapse.IsSupported) header.Patterns.ExpandCollapse.Pattern.Expand();
        else if (header.Patterns.Toggle.IsSupported) header.Patterns.Toggle.Pattern.Toggle();
        else if (header.Patterns.Invoke.IsSupported) header.Patterns.Invoke.Pattern.Invoke();
        else header.Click();
        SaveScreenshot(window, "sa_T16_R09_desktop_checkpoint_panel.png");
        File.WriteAllText(Path.Combine(evidence, "r09_checkpoint_automation.json"), JsonSerializer.Serialize(window.FindAllDescendants().Select(e => new
        { id = e.Properties.AutomationId.ValueOrDefault, name = e.Properties.Name.ValueOrDefault, type = e.ControlType.ToString(), rect = e.BoundingRectangle.ToString() })));
        WaitUntil(() => Element(window, RlUiConstants.CheckpointInspect).IsEnabled, CompletionTimeoutMs, "Checkpoint review still busy");
        Invoke(window, RlUiConstants.CheckpointInspect);
        // The list is hidden while the inspection reloads it, so it is awaited by presence and enabled state, not through the 5 s element lookup.
        WaitUntil(() => window.FindFirstDescendant(cf => cf.ByAutomationId(RlUiConstants.CheckpointModels)) is { IsEnabled: true }, CompletionTimeoutMs, "Checkpoint list did not finish loading");
        Element(window, RlUiConstants.CheckpointModels).AsComboBox().Select(0);
        Assert.False(Element(window, RlUiConstants.CheckpointResume).IsEnabled);
        Assert.False(Element(window, RlUiConstants.CheckpointAbandon).IsEnabled);
        SaveScreenshot(window, "sa_T16_R09_desktop_paused_review.png");
        Element(window, RlUiConstants.CheckpointResumeConfirm).AsCheckBox().IsChecked = true;
        WaitUntil(() => Element(window, RlUiConstants.CheckpointResume).IsEnabled, errorMessage: "Explicit verified resume not enabled");
        Invoke(window, RlUiConstants.CheckpointResume); WaitState(window, "Succeeded", StrictRunTimeoutMs);
        Assert.Equal("", ActiveModelName(window));
        SaveScreenshot(window, "sa_T16_R09_desktop_resumed.png");
        string runRoot = Path.Combine(IsolatedDataRoot, RlFiles.TrainingFolder, RlFiles.RlFolder);
        string run = Assert.Single(Directory.EnumerateDirectories(runRoot));
        using var pointer = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, RlCheckpointContract.Pointer)));
        Assert.False(pointer.RootElement.GetProperty("resumable").GetBoolean());
        Assert.True(File.Exists(Path.Combine(run, RlFiles.Model + RlFiles.EvaluationSuffix)));
        File.WriteAllText(Path.Combine(evidence, "r09_desktop_evidence.json"), JsonSerializer.Serialize(new
        { real_ppo = true, safe_boundary_pause = true, reviewed_owned_checkpoint = true, confirmation_required = true,
            explicit_resume_completed = true, smoke_not_activated = true, run_id = Path.GetFileName(run) }));
        window.Close();
    }

    [Fact(Timeout = LocalRunTestTimeoutMs)]
    public void R08_DefaultSettingsRequiredRetentionSmokeAndExplicitCleanupThroughDesktop()
    {
        string evidence=Environment.GetEnvironmentVariable(EvidenceDirectoryVariable) ?? throw new InvalidOperationException("Owned evidence output required.");
        using var config=JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("SA_RL_R07_QUALIFICATION_CONFIG")!));
        string actorDirectory=config.RootElement.GetProperty("LaterActor").GetString()!;
        string expiredRun="", expiredDirectory="";
        LaunchApplication(root=>
        {
            string metadata=PathDiscovery.ResolveDataPath(null,MarketDataSettings.DefaultMetadataDataPath);
            File.Copy(Path.Combine(actorDirectory,RlFiles.MetadataParquet),Path.Combine(metadata,Symbol+".meta.parquet"));
            using var registry=new ModelGenerationRegistry(); var settings=new RlArtifactSettings();
            registry.ConfigureRlArtifacts(root,settings);
            registry.SaveRlArtifactPolicyAsync(new(MinimumFreeGiB:FixtureMinimumFreeGiB,NonactiveRetentionDays:0,NonactiveKeepCount:0)).GetAwaiter().GetResult(); // admits the fixture run on the small work drive; the defaults are restored below
            Guid id=Guid.NewGuid(); expiredRun=id.ToString("N");
            using(var run=registry.BeginRlArtifactRunAsync(id,root).GetAwaiter().GetResult())
            {
                expiredDirectory=run!.Directory; File.WriteAllText(Path.Combine(run.Directory,"owned_failure.log"),"owned failure fixture");
                run.FinishAsync(RlArtifactRunState.Failed).GetAwaiter().GetResult();
            }
            string descriptor=Path.Combine(expiredDirectory,"owned_run.json");
            var node=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(descriptor))!;
            node["startedUtc"]=DateTimeOffset.UtcNow.AddDays(-9); node["finishedUtc"]=DateTimeOffset.UtcNow.AddDays(-8);
            File.WriteAllText(descriptor,node.ToJsonString());
            registry.SaveRlArtifactPolicyAsync(new()).GetAwaiter().GetResult();
        });
        Window window=new MainWindowPage(MainWindow!,this).OpenRlTrainingWindow();
        window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
        var panel=WaitForElement(()=>window.FindFirstDescendant(cf=>cf.ByAutomationId(RlUiConstants.StorageSettings))
            ?? window.FindAllDescendants().FirstOrDefault(e=>Localized("Rl.Storage.Title").Contains(e.Properties.Name.ValueOrDefault)),
            errorMessage:"RL artifact settings header unavailable");
        string previousHeader=""; int stableHeader=0;
        WaitUntil(()=> {
            string bounds=panel.BoundingRectangle.ToString();
            stableHeader=!panel.IsOffscreen && bounds==previousHeader ? stableHeader+1 : 0;
            previousHeader=bounds; return stableHeader>=StableLayoutSamples;
        },errorMessage:"Artifact settings header layout did not stabilize");
        LogInfo("Storage header: "+panel.ControlType+" "+panel.BoundingRectangle+" expand="+panel.Patterns.ExpandCollapse.IsSupported);
        if(panel.Patterns.ExpandCollapse.IsSupported) panel.Patterns.ExpandCollapse.Pattern.Expand(); else panel.Click();
        File.WriteAllText(Path.Combine(evidence,"r08_storage_automation.json"),JsonSerializer.Serialize(window.FindAllDescendants().Select(e=>new {
            id=e.Properties.AutomationId.ValueOrDefault,name=e.Properties.Name.ValueOrDefault,type=e.ControlType.ToString(),rect=e.BoundingRectangle.ToString() })));
        SaveScreenshot(window,"sa_T16_R08_desktop_settings.png");
        AutomationElement Editor(string id)
        {
            return WaitForElement(()=> {
                var control=window.FindFirstDescendant(cf=>cf.ByAutomationId(id)) ?? window.FindFirstDescendant(cf=>cf.ByAutomationId(id+RlUiConstants.NumericEditorSuffix)); // the numeric editor publishes its own id
                if(control is not null) return control.FindFirstDescendant(cf=>cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit)) ?? control;
                string key=id switch {
                    RlUiConstants.StorageQuota=>"Rl.Storage.Quota", RlUiConstants.StorageMinimumFree=>"Rl.Storage.MinimumFree",
                    RlUiConstants.StorageFailedDays=>"Rl.Storage.FailedDays", RlUiConstants.StorageNonactiveDays=>"Rl.Storage.NonactiveDays",
                    RlUiConstants.StorageKeepCount=>"Rl.Storage.KeepCount", _=>throw new ArgumentException("Unknown storage editor") };
                var children=window.FindAllDescendants();
                var label=children.FirstOrDefault(e=>e.ControlType==FlaUI.Core.Definitions.ControlType.Text && Localized(key).Contains(e.Name));
                if(label is null) return null;
                var bounds=label.BoundingRectangle;
                // NumericUpDown exposes its template editor rather than the outer automation id.
                return children.FirstOrDefault(e=>e.ControlType==FlaUI.Core.Definitions.ControlType.Edit
                    && e.BoundingRectangle.Left>=bounds.Right && e.BoundingRectangle.Top==bounds.Top);
            },errorMessage:"Missing labeled artifact editor "+id);
        }
        void Number(string id,string value) { var edit=Editor(id).AsTextBox(); edit.Text=value; edit.Focus(); Keyboard.Press(VirtualKeyShort.TAB); }
        Assert.Equal("10",Editor(RlUiConstants.StorageQuota).AsTextBox().Text);
        Assert.Equal("5",Editor(RlUiConstants.StorageMinimumFree).AsTextBox().Text);
        Assert.Equal("7",Editor(RlUiConstants.StorageFailedDays).AsTextBox().Text);
        Assert.Equal("",Editor(RlUiConstants.StorageNonactiveDays).AsTextBox().Text);
        Assert.Equal("",Editor(RlUiConstants.StorageKeepCount).AsTextBox().Text);
        Assert.True(Localized("Rl.Storage.Incomplete").Contains(Element(window,RlUiConstants.StorageSummary).Name));
        if(panel.Patterns.ExpandCollapse.IsSupported) panel.Patterns.ExpandCollapse.Pattern.Collapse(); else panel.Click();
        Text(window,RlUiConstants.Symbol,Symbol); Text(window,RlUiConstants.StartDate,"2002-01-01"); Text(window,RlUiConstants.EndDate,"2026-10-04");
        Element(window,RlUiConstants.Smoke).AsCheckBox().IsChecked=true;
        // The window still offers Start with an incomplete retention policy; the run is refused at admission (no run folder is created) and the window reports a failed operation.
        Invoke(window,RlUiConstants.Train); WaitState(window,"Failed",CompletionTimeoutMs);
        Assert.Single(Directory.EnumerateDirectories(Path.GetDirectoryName(expiredDirectory)!));
        if(panel.Patterns.ExpandCollapse.IsSupported) panel.Patterns.ExpandCollapse.Pattern.Expand(); else panel.Click();
        Number(RlUiConstants.StorageNonactiveDays,"0"); Number(RlUiConstants.StorageKeepCount,"1");
        Number(RlUiConstants.StorageMinimumFree,FixtureMinimumFreeGiB.ToString(CultureInfo.InvariantCulture)); // the default reserve was asserted above; the small work drive cannot keep it
        // No Save button any more: Train saves the retention values of this window first (RlTrainingDialogViewModel.TrainAsync).
        if(panel.Patterns.ExpandCollapse.IsSupported) panel.Patterns.ExpandCollapse.Pattern.Collapse(); else panel.Click();
        Invoke(window,RlUiConstants.Train); WaitState(window,"Succeeded",CompletionTimeoutMs);
        Assert.Equal("",ActiveModelName(window));
        var descriptors=Directory.EnumerateFiles(Path.GetDirectoryName(expiredDirectory)!,"owned_run.json",SearchOption.AllDirectories).ToArray();
        Assert.Equal(2,descriptors.Length);
        string completed=descriptors.Single(p=>Path.GetDirectoryName(p)!=expiredDirectory);
        using var finished=JsonDocument.Parse(File.ReadAllText(completed));
        Assert.Equal((int)RlArtifactRunState.Completed,finished.RootElement.GetProperty("state").GetInt32());
        Assert.Equal(10m,finished.RootElement.GetProperty("policy").GetProperty("quotaGiB").GetDecimal());
        if(panel.Patterns.ExpandCollapse.IsSupported) panel.Patterns.ExpandCollapse.Pattern.Expand(); else panel.Click();
        Invoke(window,RlUiConstants.StorageInspect);
        WaitUntil(()=>Element(window,RlUiConstants.StorageEntries).FindAllDescendants().Any(e=>e.Name.Contains(expiredRun)),CompletionTimeoutMs);
        Assert.True(Directory.Exists(expiredDirectory)); Assert.False(Element(window,RlUiConstants.StorageCleanup).IsEnabled);
        SaveScreenshot(window,"sa_T16_R08_desktop_review.png");
        Element(window,RlUiConstants.StorageConfirm).AsCheckBox().IsChecked=true;
        WaitUntil(()=>Element(window,RlUiConstants.StorageCleanup).IsEnabled);
        Invoke(window,RlUiConstants.StorageCleanup);
        WaitUntil(()=>!Directory.Exists(expiredDirectory),CompletionTimeoutMs,"Reviewed failed run was not explicitly removed");
        // Once nothing is left to delete the cleanup action is no longer shown; absent counts as not enabled.
        WaitUntil(()=>window.FindFirstDescendant(cf=>cf.ByAutomationId(RlUiConstants.StorageCleanup)) is not { IsEnabled: true });
        Assert.True(File.Exists(completed));
        File.WriteAllText(Path.Combine(evidence,"r08_desktop_evidence.json"),JsonSerializer.Serialize(new {
            defaults_quota_gib=10,defaults_minimum_free_gib=5,defaults_failed_days=7,nonactive_initially_unset=true,
            incomplete_policy_start_rejected=true,real_smoke_completed=true,run_policy_frozen=true,
            explicit_review_confirm_delete=true,completed_unregistered_smoke_retained=true,expiredRun,original_store_untouched=true }));
        SaveScreenshot(window,"sa_T16_R08_desktop_cleaned.png"); window.Close();
    }

    [Fact(Timeout = LocalRunTestTimeoutMs)]
    public void R07_ExplicitVerifiedBackupRecoveryThroughDesktop()
    {
        string evidence = Environment.GetEnvironmentVariable(EvidenceDirectoryVariable) ?? throw new InvalidOperationException("Explicit recovery evidence root required.");
        using var config = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("SA_RL_R07_QUALIFICATION_CONFIG")
            ?? throw new InvalidOperationException("Owned qualified recovery fixture configuration required.")));
        string earlierDirection = config.RootElement.GetProperty("EarlierDirection").GetString()!;
        string earlierActor = config.RootElement.GetProperty("EarlierActor").GetString()!;
        string laterDirection = config.RootElement.GetProperty("LaterDirection").GetString()!;
        string laterActor = config.RootElement.GetProperty("LaterActor").GetString()!;
        string restoredId = "", healthyId = "", registryRoot = "";
        byte[] backup = Array.Empty<byte>();
        LaunchApplication(root =>
        {
            string metadata = PathDiscovery.ResolveDataPath(null, MarketDataSettings.DefaultMetadataDataPath);
            File.Copy(Path.Combine(laterActor, RlFiles.MetadataParquet), Path.Combine(metadata, Symbol + ".meta.parquet"));
            registryRoot = PathDiscovery.ResolveModelGenerationsDirectory();
            using var registry = new ModelGenerationRegistry();
            registry.RegisterRlAsync(earlierDirection).GetAwaiter().GetResult();
            restoredId = registry.RegisterRlAsync(earlierActor).GetAwaiter().GetResult().ModelId;
            registry.ActivateRlAsync(restoredId).GetAwaiter().GetResult();
            registry.RegisterRlAsync(laterDirection).GetAwaiter().GetResult();
            healthyId = registry.RegisterRlAsync(laterActor).GetAwaiter().GetResult().ModelId;
            registry.ActivateRlAsync(healthyId).GetAwaiter().GetResult();
            string pointer = Path.Combine(registryRoot, RlFiles.RlFolder, RlContinuousContract.ActiveFile);
            backup = File.ReadAllBytes(pointer + ".bak");
            File.WriteAllText(pointer, "{"); Assert.Null(registry.RlContinuousActiveId); Assert.Null(registry.RlActiveId);
        });
        var page = new MainWindowPage(MainWindow!, this);
        Window window = page.OpenRlTrainingWindow();
        window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
        Element(window, RlUiConstants.ContinuousMode).AsCheckBox().IsChecked = true;
        Invoke(window, RlUiConstants.InspectRecovery);
        WaitUntil(() => Element(window, RlUiConstants.RecoveryPreview).Name.Contains(restoredId, StringComparison.Ordinal),
            CompletionTimeoutMs, "Verified backup candidate not displayed in the available recovery dialog");
        WaitState(window, "Unavailable");
        Assert.True(Element(window, RlUiConstants.RecoverPointer).IsEnabled);
        string pointerPath = Path.Combine(registryRoot, RlFiles.RlFolder, RlContinuousContract.ActiveFile);
        Assert.Equal("{", File.ReadAllText(pointerPath)); Assert.Equal(backup, File.ReadAllBytes(pointerPath + ".bak"));
        SaveScreenshot(window, "sa_T16_R07_desktop_verified_backup.png");
        Invoke(window, RlUiConstants.RecoverPointer); WaitState(window, "Ready", CompletionTimeoutMs);
        WaitUntil(() => Element(window, RlUiConstants.ActiveModel).Name == restoredId, CompletionTimeoutMs,
            "Explicit recovery did not publish the reviewed active identity");
        // The recovery action is no longer shown once the pointer is healthy; absent counts as not enabled.
        WaitUntil(() => window.FindFirstDescendant(cf => cf.ByAutomationId(RlUiConstants.RecoverPointer)) is not { IsEnabled: true }, errorMessage: "Healthy pointer still allows recovery");
        WaitUntil(() => Localized("Rl.RecoveryHealthy").Contains(Element(window, RlUiConstants.RecoveryPreview).Name),
            errorMessage: "Recovered pointer was not re-inspected as healthy");
        Assert.Equal(backup, File.ReadAllBytes(pointerPath + ".bak"));
        string? previousRoot = Environment.GetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable, IsolatedDataRoot);
            Assert.Equal(registryRoot, PathDiscovery.ResolveModelGenerationsDirectory());
            using var reopened = new ModelGenerationRegistry();
            Assert.Equal(restoredId, reopened.RlContinuousActiveId); Assert.Null(reopened.RlActiveId);
        }
        finally { Environment.SetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable, previousRoot); }
        File.WriteAllText(Path.Combine(evidence, "r07_desktop_evidence.json"), JsonSerializer.Serialize(new {
            restoredId, healthyId, explicit_candidate_review = true, no_automatic_restore = true, healthy_recovery_disabled = true,
            verified_backup_preserved = true, original_active_untouched = true }));
        SaveScreenshot(window, "sa_T16_R07_desktop_recovered.png");
        window.Close();
    }

    [Fact(Timeout = OneStrictRunTestTimeoutMs)]
    public void R06_AllBlockOptionSmokeReportAndLatestReplayThroughDesktop()
    {
        string directionDirectory = Environment.GetEnvironmentVariable(QualifiedDirectoryVariable)
            ?? throw new InvalidOperationException("Qualified earlier T15 required.");
        string actorDirectory = Environment.GetEnvironmentVariable(ContinuousDirectoryVariable)
            ?? throw new InvalidOperationException("Qualified R06 T16 required.");
        string evidenceDirectory = Environment.GetEnvironmentVariable(EvidenceDirectoryVariable)
            ?? throw new InvalidOperationException("Explicit desktop evidence output required.");
        var source = RlSourceSnapshot.Load(actorDirectory, RlTrainingValidation.FreezeLimits(continuous: true));
        Assert.True(source.ExpandedEvaluation); Assert.Equal(6, source.Catalog.Validation.Length); Assert.Equal(6, source.Catalog.Oos.Length);
        string actorId = "";
        LaunchApplication(root =>
        {
            string metadata = PathDiscovery.ResolveDataPath(null, MarketDataSettings.DefaultMetadataDataPath);
            File.Copy(Path.Combine(actorDirectory, RlFiles.MetadataParquet), Path.Combine(metadata, Symbol + ".meta.parquet"));
            using var registry = new ModelGenerationRegistry();
            SaveRetentionPolicy(registry, root);
            registry.RegisterRlAsync(directionDirectory).GetAwaiter().GetResult();
            actorId = registry.RegisterRlAsync(actorDirectory).GetAwaiter().GetResult().ModelId;
            Assert.Null(registry.RlActiveId); Assert.Null(registry.RlContinuousActiveId);
        });
        var page = new MainWindowPage(MainWindow!, this);
        Window window = page.OpenRlTrainingWindow();
        window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
        Element(window, RlUiConstants.ContinuousMode).AsCheckBox().IsChecked = true;
        var expandedOption = Element(window, RlUiConstants.AllCompleteBlocks).AsCheckBox();
        Assert.False(expandedOption.IsChecked == true); Assert.True(Localized("Rl.AllBlockOption").Contains(expandedOption.Name));
        expandedOption.IsChecked = true;
        SelectFirst(Element(window, RlUiConstants.FrozenDirection).AsComboBox());
        Text(window, RlUiConstants.Symbol, Symbol);
        Text(window, RlUiConstants.StartDate, "2002-01-01"); Text(window, RlUiConstants.EndDate, "2026-10-04");
        Element(window, RlUiConstants.Smoke).AsCheckBox().IsChecked = true;
        Invoke(window, RlUiConstants.Train); WaitState(window, "Succeeded", StrictRunTimeoutMs);
        Assert.Equal("", ActiveModelName(window));
        string ownedTraining = Path.Combine(IsolatedDataRoot, RlFiles.TrainingFolder, RlFiles.RlFolder);
        string ownedJob = Assert.Single(Directory.EnumerateFiles(ownedTraining, RlFiles.Job, SearchOption.AllDirectories));
        using var job = JsonDocument.Parse(File.ReadAllText(ownedJob));
        Assert.Equal(RlAllBlockEvaluationContract.SpecificationId, job.RootElement.GetProperty(RlAllBlockEvaluationContract.MetadataKey).GetString());
        Assert.Equal(6, job.RootElement.GetProperty("catalog").GetProperty("validation").GetArrayLength());
        Assert.Equal(0, job.RootElement.GetProperty("catalog").GetProperty("oos").GetArrayLength());
        File.Copy(ownedJob, Path.Combine(evidenceDirectory, "r06_desktop_smoke_job.json"), true);
        File.Copy(Path.Combine(Path.GetDirectoryName(ownedJob)!, RlFiles.Model + RlFiles.EvaluationSuffix),
            Path.Combine(evidenceDirectory, "r06_desktop_smoke_report.json"), true);
        SelectFirst(Element(window, RlUiConstants.Models).AsComboBox());
        WaitUntilEvaluationDetails(window, "Expanded generation catalog not loaded");
        var details = WaitForElement(() => FindEvaluationDetails(window));
        if (details.Patterns.ExpandCollapse.IsSupported) details.Patterns.ExpandCollapse.Pattern.Expand(); else details.Click();
        var reportElement = Element(window, RlUiConstants.EvaluationReport);
        Assert.True(Localized("Rl.AllBlockMode").Any(reportElement.Name.Contains));
        Assert.True(Localized("Rl.AllBlockDisclosure").Any(reportElement.Name.Contains));
        string previousLayout = ""; int stable = 0;
        WaitUntil(() =>
        {
            string layout = reportElement.BoundingRectangle.ToString() + Element(window, RlUiConstants.Activate).BoundingRectangle;
            stable = !reportElement.IsOffscreen && reportElement.BoundingRectangle.Height > 0 && layout == previousLayout ? stable + 1 : 0;
            previousLayout = layout; return stable >= StableLayoutSamples;
        }, errorMessage: "Expanded all-block report layout did not stabilize");
        File.WriteAllText(Path.Combine(evidenceDirectory, "r06_desktop_full_report.txt"), reportElement.Name);
        SaveScreenshot(window, "sa_T16_R06_desktop_report.png");
        if (details.Patterns.ExpandCollapse.IsSupported) details.Patterns.ExpandCollapse.Pattern.Collapse(); else details.Click();
        window.Close();
        ReplayFromSettings(page, source.Catalog.Oos[^1].Id);
        var panel = WaitForElement(() => MainWindow!.FindFirstDescendant(cf => cf.ByAutomationId(RlUiConstants.QPanel)));
        Assert.False(panel.IsOffscreen); Assert.Contains(actorId, panel.Name);
        string anchor = source.Bars[source.Catalog.Oos[^1].StartIndex + RlContract.WindowLength - 1].Timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Contains(anchor, panel.Name); Assert.Contains("0.99", panel.Name);
        SaveScreenshot(MainWindow!, "sa_T16_R06_desktop_latest_oos.png");
        page.ChangeTimeFrame("Weekly");
        WaitUntil(() => MainWindow!.FindFirstDescendant(cf => cf.ByAutomationId(RlUiConstants.QPanel)) is not { IsOffscreen: false },
            errorMessage: "Timeframe retained all-block replay presentation");
        File.WriteAllText(Path.Combine(evidenceDirectory, "r06_desktop_evidence.json"), JsonSerializer.Serialize(new {
            actorId, latest_oos = source.Catalog.Oos[^1].Id, anchor, validation_blocks = 6, oos_blocks = 6,
            explicit_option_default_off = true, smoke_option_consumed = true, report_visible = true, stale_presentation_cleared = true,
            original_active_untouched = true }));
    }

    [Fact(Timeout = LocalRunTestTimeoutMs)]
    public void R04_ConstraintReportAndSeparatePolicyControlsThroughDesktop()
    {
        string directionDirectory = Environment.GetEnvironmentVariable(QualifiedDirectoryVariable)
            ?? throw new InvalidOperationException("Qualified T15 required.");
        string actorDirectory = Environment.GetEnvironmentVariable(ContinuousDirectoryVariable)
            ?? throw new InvalidOperationException("Qualified R04 T16 required.");
        string actorId = "";
        LaunchApplication(root =>
        {
            string metadata = PathDiscovery.ResolveDataPath(null, MarketDataSettings.DefaultMetadataDataPath);
            File.Copy(Path.Combine(actorDirectory, RlFiles.MetadataParquet), Path.Combine(metadata, Symbol + ".meta.parquet"));
            using var registry = new ModelGenerationRegistry();
            registry.RegisterRlAsync(directionDirectory).GetAwaiter().GetResult();
            actorId = registry.RegisterRlAsync(actorDirectory).GetAwaiter().GetResult().ModelId;
            Assert.Null(registry.RlActiveId); Assert.Null(registry.RlContinuousActiveId);
        });
        var page = new MainWindowPage(MainWindow!, this);
        Window window = page.OpenRlTrainingWindow();
        window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
        // The model list shows the models of the chosen mode only: the T16 model is listed in the continuous mode.
        Element(window, RlUiConstants.ContinuousMode).AsCheckBox().IsChecked = true;
        SelectFirst(Element(window, RlUiConstants.Models).AsComboBox());
        WaitUntilEvaluationDetails(window, "R04 blocks not loaded");
        SaveScreenshot(window, "sa_T16_R04_desktop_before_report.png");
        var tree = window.FindAllDescendants().Select(e => new { AutomationId = e.Properties.AutomationId.ValueOrDefault,
            Name = e.Properties.Name.ValueOrDefault, ControlType = e.Properties.ControlType.ValueOrDefault.ToString(),
            IsOffscreen = e.Properties.IsOffscreen.ValueOrDefault }).ToArray();
        string? evidenceDirectory = Environment.GetEnvironmentVariable(EvidenceDirectoryVariable);
        if (evidenceDirectory is not null) File.WriteAllText(Path.Combine(evidenceDirectory, "r04_dialog_automation.json"), JsonSerializer.Serialize(tree));
        var details = WaitForElement(() => FindEvaluationDetails(window));
        if (details.Patterns.ExpandCollapse.IsSupported) details.Patterns.ExpandCollapse.Pattern.Expand(); else details.Click();
        var reportElement = Element(window, RlUiConstants.EvaluationReport);
        string report = reportElement.Name;
        // The report names the policies with their display names (the ids are not shown) and no longer prints the selection metric.
        Assert.True(Localized("Rl.Ui.Policy.Paired").Any(report.Contains)); Assert.True(Localized("Rl.Ui.Policy.Hold").Any(report.Contains));
        Assert.True(Localized("Rl.ConstraintStatus.NotApplicable").Any(report.Contains));
        Assert.True(Localized("Rl.ConstraintDisclosure").Any(report.Contains));
        string previousLayout = ""; int stableLayout = 0;
        WaitUntil(() =>
        {
            string layout = reportElement.BoundingRectangle.ToString() + Element(window, RlUiConstants.Activate).BoundingRectangle;
            stableLayout = !reportElement.IsOffscreen && reportElement.BoundingRectangle.Height > 0 && layout == previousLayout
                ? stableLayout + 1 : 0;
            previousLayout = layout;
            return stableLayout >= StableLayoutSamples;
        }, errorMessage: "Expanded report layout did not become visible and stable");
        SaveScreenshot(window, "sa_T16_R04_desktop_report.png");
        if (details.Patterns.ExpandCollapse.IsSupported) details.Patterns.ExpandCollapse.Pattern.Collapse();
        else details.Click();
        window.Close();
        ReplayFromSettings(page, policyNames: Localized("Rl.Ui.Policy.Paired"));
        WaitUntil(() => MainWindow!.FindFirstDescendant(cf => cf.ByAutomationId(RlUiConstants.PolicyControlReadout))?.Name is { } readout && Localized("Rl.Ui.Policy.Paired").Any(readout.Contains),
            CompletionTimeoutMs, "Fixed policy controls were not published");
        var proposal = WaitForElement(() => MainWindow!.FindFirstDescendant(cf => cf.ByAutomationId(RlUiConstants.ContinuousReadout)));
        var control = WaitForElement(() => MainWindow!.FindFirstDescendant(cf => cf.ByAutomationId(RlUiConstants.PolicyControlReadout)));
        Assert.False(proposal.IsOffscreen); Assert.False(control.IsOffscreen);
        Assert.Contains("30.00%", control.Name); Assert.Contains("2.00%", control.Name);
        Assert.Contains(actorId, WaitForElement(() => MainWindow!.FindFirstDescendant(cf => cf.ByAutomationId(RlUiConstants.QPanel))).Name);
        SaveScreenshot(MainWindow!, "sa_T16_R04_desktop_policy_controls.png");
        page.ChangeTimeFrame("Weekly");
        foreach (string id in new[] { RlUiConstants.ContinuousReadout, RlUiConstants.PolicyControlReadout })
            WaitUntil(() => MainWindow!.FindFirstDescendant(cf => cf.ByAutomationId(id)) is not { IsOffscreen: false }, errorMessage: "Stale R04 readout: " + id);
        WaitUntil(() => MainWindow!.FindAllDescendants().Where(e => Localized("Rl.EvaluationDetails").Contains(e.Properties.Name.ValueOrDefault))
            .All(e => e.IsOffscreen), errorMessage: "Stale R04 evaluation details");
    }

    [Fact(Timeout = LocalRunTestTimeoutMs)]
    public void LocalStoredPrices_TrainCancelActivateReplayAndInvalidateThroughDesktop()
    {
        string directory = Environment.GetEnvironmentVariable(QualifiedDirectoryVariable)
            ?? throw new InvalidOperationException("A completed real local-data qualification directory is required.");
        string modelId = "";
        string registryRoot = "";
        var source = RlSourceSnapshot.Load(directory, RlTrainingValidation.FreezeLimits());
        string expectedAnchor = source.Bars[source.Catalog.Validation[0].StartIndex + RlContract.WindowLength - 1]
            .Timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            LaunchApplication(root =>
            {
                string metadata = PathDiscovery.ResolveDataPath(null, MarketDataSettings.DefaultMetadataDataPath);
                File.Copy(Path.Combine(directory, RlFiles.MetadataParquet), Path.Combine(metadata, Symbol + ".meta.parquet"));
                using var registry = new ModelGenerationRegistry();
                SaveRetentionPolicy(registry, root);
                registryRoot = Path.Combine(PathDiscovery.ResolveModelGenerationsDirectory(), RlFiles.RlFolder);
                modelId = registry.RegisterRlAsync(directory).GetAwaiter().GetResult().ModelId;
                Assert.Null(registry.RlActiveId);
            });
            var page = new MainWindowPage(MainWindow!, this);
            Window window = page.OpenRlTrainingWindow();
            Assert.Equal(RlUiConstants.Window, window.AutomationId);
            WaitState(window, "Ready");
            Assert.Equal("", ActiveModelName(window));
            // The verification facts are shown as a label/value list (the first fact stands for it); the single disclosure sentence is no longer a screen element.
            var shownFacts = Element(window, RlUiConstants.Disclosure).FindAllDescendants().Select(e => e.Properties.Name.ValueOrDefault).ToArray();
            Assert.True(Localized("Rl.Ui.Fact.F01.Label").Any(shownFacts.Contains) && Localized("Rl.Ui.Fact.F01.Value").Any(shownFacts.Contains),
                "The verification facts list does not show its first fact: " + string.Join(" | ", shownFacts));
            Text(window, RlUiConstants.Symbol, Symbol);
            // The date pickers no longer accept free text and the window blocks an invalid input before any job exists: Start is disabled and the footer says why.
            Text(window, RlUiConstants.EndDate, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Text(window, RlUiConstants.StartDate, DateTime.UtcNow.AddYears(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            WaitUntil(() => !Element(window, RlUiConstants.Train).IsEnabled, errorMessage: "Start stayed enabled for a start date after the end date");
            Assert.True(Localized("Rl.Ui.Guide.InvalidDateOrder").Contains(Element(window, RlUiConstants.StartBlocker).Name));
            Text(window, RlUiConstants.StartDate, "2026-01-01");
            Invoke(window, RlUiConstants.Train);
            WaitState(window, "InsufficientData");
            Text(window, RlUiConstants.StartDate, "1990-01-01");
            Element(window, RlUiConstants.Smoke).AsCheckBox().IsChecked = true;
            Invoke(window, RlUiConstants.Train);
            WaitState(window, "Succeeded", CompletionTimeoutMs);
            Assert.Equal("", ActiveModelName(window));
            Assert.True(Localized("Rl.SmokeCompleted").Contains(Element(window, RlUiConstants.Message).Name));
            Assert.Single(Directory.EnumerateFiles(registryRoot, RlFiles.Manifest, SearchOption.AllDirectories));
            SaveScreenshot(window, "sa_T15_RL_desktop_smoke.png");

            Element(window, RlUiConstants.Smoke).AsCheckBox().IsChecked = false;
            Invoke(window, RlUiConstants.Train);
            WaitState(window, "Training", CompletionTimeoutMs);
            Invoke(window, RlUiConstants.Cancel);
            WaitState(window, "Cancelled", CompletionTimeoutMs);
            Assert.Equal("", ActiveModelName(window));
            Assert.Single(Directory.EnumerateFiles(registryRoot, RlFiles.Manifest, SearchOption.AllDirectories));

            Element(window, RlUiConstants.Models).AsComboBox().Select(0);
            // A T15 model has no constraint report, so the evaluation-details expander is T16-only; the selection is complete once Activate is offered.
            WaitUntil(() => Element(window, RlUiConstants.Activate).IsEnabled, CompletionTimeoutMs, "The selected model was not offered for activation");
            Invoke(window, RlUiConstants.Activate);
            WaitUntil(() => Element(window, RlUiConstants.ActiveModel).Name == modelId,
                errorMessage: "Manual activation did not publish the selected model");
            window.Close();
            // RL Playback starts from its default block; the first validation block is chosen explicitly so the anchor date below is the one that block ends its window on.
            ReplayFromSettings(page, source.Catalog.Validation[0].Id);
            var panel = WaitForElement(() => MainWindow!.FindFirstDescendant(cf => cf.ByAutomationId(RlUiConstants.QPanel)));
            Assert.False(panel.IsOffscreen);
            Assert.Contains(modelId, panel.Name);
            Assert.Contains(RlFiles.Target, panel.Name);
            Assert.True(panel.Name.Contains(expectedAnchor, StringComparison.Ordinal), $"Q panel does not show the anchor {expectedAnchor}: {panel.Name}");
            foreach (string key in new[] { "Rl.Hold", "Rl.Buy", "Rl.Sell", "Rl.Maximum" })
                Assert.True(Localized(key).Any(value => panel.Name.Contains(value, StringComparison.Ordinal)), $"Q panel does not expose {key}");
            Assert.Contains("0.99", panel.Name);
            SaveScreenshot(MainWindow!, "sa_T15_RL_desktop_qualified_Q.png");
            LogInfo($"Real local qualified model {modelId}: UI smoke succeeded without registration/activation; full job cancelled; manual activation, causal replay, three Q actions, units and visible panel verified.");
            page.ChangeTimeFrame("Weekly");
            WaitUntil(() => MainWindow!.FindFirstDescendant(cf => cf.ByAutomationId(RlUiConstants.QPanel)) is not { IsOffscreen: false },
                errorMessage: "Changing timeframe retained a stale Q presentation");
        }
        catch
        {
            CaptureScreenshotOnFailure(nameof(LocalStoredPrices_TrainCancelActivateReplayAndInvalidateThroughDesktop));
            throw;
        }
    }

    [Fact(Timeout = TwoStrictRunsTestTimeoutMs)]
    public void ContinuousStoredPrices_SelectFrozenDirectionTrainAndPublishReadout()
    {
        string directionDirectory = Environment.GetEnvironmentVariable(QualifiedDirectoryVariable)
            ?? throw new InvalidOperationException("Qualified T15 directory required.");
        string actorDirectory = Environment.GetEnvironmentVariable(ContinuousDirectoryVariable)
            ?? throw new InvalidOperationException("Qualified T16 actor directory required.");
        string actorId = "", directionId = "", registryRoot = "";
        LaunchApplication(root =>
        {
            string metadata = PathDiscovery.ResolveDataPath(null, MarketDataSettings.DefaultMetadataDataPath);
            File.Copy(Path.Combine(actorDirectory, RlFiles.MetadataParquet), Path.Combine(metadata, Symbol + ".meta.parquet"));
            using var registry = new ModelGenerationRegistry();
            SaveRetentionPolicy(registry, root);
            registryRoot = Path.Combine(PathDiscovery.ResolveModelGenerationsDirectory(), RlFiles.RlFolder);
            directionId = registry.RegisterRlAsync(directionDirectory).GetAwaiter().GetResult().ModelId;
            actorId = registry.RegisterRlAsync(actorDirectory).GetAwaiter().GetResult().ModelId;
            Assert.Null(registry.RlActiveId); Assert.Null(registry.RlContinuousActiveId);
        });
        var page = new MainWindowPage(MainWindow!, this);
        Window window = page.OpenRlTrainingWindow();
        // This is an owned test window; maximize to keep the added direction controls and replay controls visible.
        window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
        Element(window, RlUiConstants.ContinuousMode).AsCheckBox().IsChecked = true;
        Text(window, RlUiConstants.Symbol, Symbol); Text(window, RlUiConstants.StartDate, "1990-01-01");
        Text(window, RlUiConstants.EndDate, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        // Without a frozen direction model the window blocks the start itself (Start disabled, the footer names the reason).
        WaitUntil(() => !Element(window, RlUiConstants.Train).IsEnabled, errorMessage: "Start stayed enabled without a frozen direction model");
        Assert.True(Localized("Rl.Ui.Start.DirectionModelMissing").Contains(Element(window, RlUiConstants.StartBlocker).Name));
        SelectFirst(Element(window, RlUiConstants.FrozenDirection).AsComboBox());
        // The old overlapping input now fails before learning; it is never silently shifted.
        Invoke(window, RlUiConstants.Train); WaitState(window, "Rejected");
        Assert.True(Localized("Rl.Temporal.Rejection.FutureInformation").Any(template =>
            Element(window, RlUiConstants.Message).Name.StartsWith(template.Split('{')[0], StringComparison.Ordinal)));
        var actorSource = RlSourceSnapshot.Load(actorDirectory, RlTrainingValidation.FreezeLimits(continuous: true));
        Text(window, RlUiConstants.StartDate, actorSource.Manifest.PeriodStartInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Text(window, RlUiConstants.EndDate, actorSource.Manifest.PeriodEndExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        WaitUntil(() => Localized("Rl.Temporal.ExactDerived").Any(text => Element(window, RlUiConstants.DirectionTemporal).Name.Contains(text, StringComparison.Ordinal)),
            CompletionTimeoutMs, "Frozen direction temporal evidence was not presented");
        Element(window, RlUiConstants.Smoke).AsCheckBox().IsChecked = true;
        Invoke(window, RlUiConstants.Train); WaitState(window, "Succeeded", StrictRunTimeoutMs);
        Assert.Equal("", ActiveModelName(window));
        Assert.Equal(2, Directory.EnumerateFiles(registryRoot, RlFiles.Manifest, SearchOption.AllDirectories).Count());
        // Closed/virtualized ComboBox items need not expose UIA SelectedItem. The following full
        // train must reach Training without selecting again, proving preservation through refresh.
        Element(window, RlUiConstants.Smoke).AsCheckBox().IsChecked = false;
        Invoke(window, RlUiConstants.Train); WaitState(window, "Training", StrictRunTimeoutMs);
        Invoke(window, RlUiConstants.Cancel); WaitState(window, "Cancelled", CompletionTimeoutMs);
        Assert.Equal("", ActiveModelName(window));
        Assert.Equal(2, Directory.EnumerateFiles(registryRoot, RlFiles.Manifest, SearchOption.AllDirectories).Count());
        SelectFirst(Element(window, RlUiConstants.Models).AsComboBox());
        WaitUntilEvaluationDetails(window, "Continuous model block support was not loaded");
        Invoke(window, RlUiConstants.Activate);
        WaitUntil(() => Element(window, RlUiConstants.ActiveModel).Name == actorId, errorMessage: "Continuous manual activation failed");
        window.Close();
        ReplayFromSettings(page);
        var panel = WaitForElement(() => MainWindow!.FindFirstDescendant(cf => cf.ByAutomationId(RlUiConstants.QPanel)));
        Assert.Contains(actorId, panel.Name); Assert.Contains(RlContinuousContract.Target, panel.Name);
        LogInfo("Published continuous Q panel: " + panel.Name);
        AutomationElement readout;
        try
        {
            readout = WaitForElement(() => MainWindow!.FindFirstDescendant(cf => cf.ByAutomationId(RlUiConstants.ContinuousReadout)));
        }
        catch
        {
            SaveScreenshot(MainWindow!, "sa_T16_RL_desktop_readout_failure.png");
            LogInfo("Readout candidates: " + string.Join(" | ", MainWindow!.FindAllDescendants()
                .Where(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("Rl_", StringComparison.Ordinal))
                .Select(e => e.Properties.AutomationId.ValueOrDefault + ": " + e.Properties.Name.ValueOrDefault)));
            throw;
        }
        Assert.False(readout.IsOffscreen); Assert.Contains(directionId, readout.Name);
        Assert.True(Localized("Rl.ContinuousReadout").Any(template => readout.Name.Contains(template.Split('{')[0], StringComparison.Ordinal)));
        Assert.True(Localized("Rl.ContinuousDisclosure").Any(text => readout.Name.Contains(text, StringComparison.Ordinal)));
        Assert.True(Localized("Rl.Temporal.ExactDerived").Any(text => readout.Name.Contains(text, StringComparison.Ordinal)));
        SaveScreenshot(MainWindow!, "sa_T16_RL_desktop_continuous_readout.png");
        page.ChangeTimeFrame("Weekly");
        WaitUntil(() => MainWindow!.FindFirstDescendant(cf => cf.ByAutomationId(RlUiConstants.QPanel)) is not { IsOffscreen: false },
            errorMessage: "Timeframe change retained stale continuous Q presentation");
        WaitUntil(() => MainWindow!.FindFirstDescendant(cf => cf.ByAutomationId(RlUiConstants.ContinuousReadout)) is not { IsOffscreen: false },
            errorMessage: "Timeframe change retained stale continuous readout");
    }

    /// <summary>The disposable data root lives on the small work drive, which cannot keep the default free-space reserve; a run is admitted with this reserve instead.</summary>
    private const decimal FixtureMinimumFreeGiB = 1m;
    /// <summary>Training needs an explicit retention policy (the defaults leave the inactive-model retention unset on purpose); the fixture saves the one R09 uses.</summary>
    private static void SaveRetentionPolicy(ModelGenerationRegistry registry, string root)
    {
        registry.ConfigureRlArtifacts(root, new RlArtifactSettings());
        registry.SaveRlArtifactPolicyAsync(new(MinimumFreeGiB: FixtureMinimumFreeGiB, NonactiveRetentionDays: 0, NonactiveKeepCount: 1)).GetAwaiter().GetResult();
    }
    /// <summary>The active-model line is shown only while a model is active; "no active model" is the absence of that element.</summary>
    private static string ActiveModelName(Window window) => ShownName(window, RlUiConstants.ActiveModel);
    /// <summary>The name of an element that the window shows only while it has content (active model, message); "" while it is not shown.</summary>
    private static string ShownName(Window window, string id) => window.FindFirstDescendant(cf => cf.ByAutomationId(id))?.Name ?? "";
    private AutomationElement Element(Window window, string id) => WaitForElement(() => window.FindFirstDescendant(cf => cf.ByAutomationId(id)), errorMessage: $"Missing RL element {id}");
    private void Text(Window window, string id, string value)
    {
        var editor = FieldElement(window, id).AsTextBox();
        editor.Text = value; editor.Focus(); Keyboard.Press(VirtualKeyShort.TAB);
    }
    /// <summary>
    /// The Symbol field (AutoCompleteBox) and the two date fields (CalendarDatePicker) are composite controls: UI Automation exposes only their inner
    /// editor, not their automation id, so those fields are found through their label.
    /// </summary>
    private AutomationElement FieldElement(Window window, string id) => WaitForElement(() => window.FindFirstDescendant(cf => cf.ByAutomationId(id))
        ?? (CompositeFieldLabels.TryGetValue(id, out string? labelKey) ? FindEditorBelowLabel(window, FlaUI.Core.Definitions.ControlType.Edit, Localized(labelKey)) : null),
        errorMessage: "Missing RL element " + id);
    private static readonly Dictionary<string, string> CompositeFieldLabels = new()
    {
        [RlUiConstants.Symbol] = "Rl.Symbol", [RlUiConstants.StartDate] = "Rl.StartDate", [RlUiConstants.EndDate] = "Rl.EndDate",
    };
    private void Invoke(Window window, string id) => Element(window, id).Patterns.Invoke.Pattern.Invoke();
    /// <summary>Selects the first option and closes the list through UI Automation: an Escape key would close the whole window when the list is already closed.</summary>
    private static void SelectFirst(ComboBox combo)
    {
        combo.Select(0);
        if (combo.ExpandCollapseState == FlaUI.Core.Definitions.ExpandCollapseState.Expanded) combo.Collapse();
    }
    /// <summary>The playback lists name an option "partition/label · id"; FlaUI's <c>Select(string)</c> needs the exact name and silently selects nothing otherwise, so the option is found by the id (or, for the policies, the display name) it contains.</summary>
    private static void SelectContaining(ComboBox combo, params string[] anyOf)
    {
        int index = Array.FindIndex(combo.Items, item => anyOf.Any(text => item.Name.Contains(text, StringComparison.Ordinal)));
        Assert.True(index >= 0, "No list option contains " + string.Join(" / ", anyOf) + ": " + string.Join(" | ", combo.Items.Select(item => item.Name)));
        combo.Select(index);
    }
    /// <summary>The RL window shows the evaluation details only once the blocks of the selected generation are loaded.</summary>
    private void WaitUntilEvaluationDetails(Window window, string errorMessage)
        => WaitUntil(() => FindEvaluationDetails(window) is not null, CompletionTimeoutMs, errorMessage);
    /// <summary>The expander header publishes no automation id, so it is found by its id or, failing that, by its localized text.</summary>
    private static AutomationElement? FindEvaluationDetails(Window window) => window.FindFirstDescendant(cf => cf.ByAutomationId(RlUiConstants.EvaluationDetails))
        ?? window.FindAllDescendants().FirstOrDefault(e => Localized("Rl.EvaluationDetails").Contains(e.Properties.Name.ValueOrDefault));
    /// <summary>Replays the newest agent through Settings > RL Playback; a successful "Show on chart" closes Settings.</summary>
    private void ReplayFromSettings(MainWindowPage page, string? blockId = null, string[]? policyNames = null)
    {
        var settings = page.OpenRlPlaybackSettings(Localized("Settings_RlPlayback"));
        WaitUntil(() => Element(settings, RlUiConstants.PlaybackShow).IsEnabled, CompletionTimeoutMs, "RL Playback was not ready to show");
        if (blockId is not null) { SelectContaining(Element(settings, RlUiConstants.PlaybackBlocks).AsComboBox(), blockId); Keyboard.Press(VirtualKeyShort.ESCAPE); }
        if (policyNames is not null) { SelectContaining(Element(settings, RlUiConstants.PlaybackPolicies).AsComboBox(), policyNames); Keyboard.Press(VirtualKeyShort.ESCAPE); }
        Invoke(settings, RlUiConstants.PlaybackShow);
        WaitUntil(() => MainWindow!.ModalWindows.Length == 0, CompletionTimeoutMs, "Replay did not finish: Settings stayed open");
    }
    private void WaitState(Window window, string state, int timeout = CompletionTimeoutMs)
    {
        var expected = Localized("Rl.State." + state);
        WaitUntil(() => expected.Contains(Element(window, RlUiConstants.Status).Name), timeout,
            $"RL did not reach {state}; inspect its status/message and training artifacts");
        LogInfo($"RL state: {state}; {ShownName(window, RlUiConstants.Message)}");
    }
    private static string[] Localized(string key) => new[] { "en", "ja" }.Select(language =>
    {
        string name = string.Format(CultureInfo.InvariantCulture, LocalizationManager.LocaleResourceNameFormat, language);
        using var stream = typeof(LocalizationManager).Assembly.GetManifestResourceStream(name)!;
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.GetProperty(key).GetString()!;
    }).ToArray();
    private static void SaveScreenshot(Window window, string name)
    {
        string destination = Environment.GetEnvironmentVariable(EvidenceDirectoryVariable) ?? Path.GetTempPath();
        var capture = FlaUI.Core.Capturing.Capture.Element(window);
        capture.ToFile(Path.Combine(destination, name));
    }
}
