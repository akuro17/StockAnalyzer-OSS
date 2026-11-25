using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters
{
    /// <summary>
    /// Parameters of the Elliott Wave Impulse 1-5 flag (plan ChartPatterns56 v2, run V2-7, integration spec 5.5; engine <c>ElliottWaveEngine.RunImpulse</c>).
    /// NEVER rename or move: the class name is the JSON type discriminator of the workspace.
    /// </summary>
    public class CoreElliottImpulseParameter : CoreElliottPatternParameterBase
    {
        private decimal _overlapTolerance = PatternFlagParameterLimits.ElliottOverlapToleranceDefault;
        private bool _allowTruncation;

        public CoreElliottImpulseParameter()
            : base(PatternFlagParameterLimits.ElliottImpulseMinSpanDefault, PatternFlagParameterLimits.ElliottImpulseMaxSpanDefault)
        {
        }

        [DisplayName("Overlap Tolerance")]
        [Description("Allowed Wave 4 overlap into Wave 1 territory as a fraction of Wave 1 length (0 = strict p4 > p1). Unit: ratio.")]
        [Category("Rules")]
        [CoreParameterRange(PatternFlagParameterLimits.ElliottOverlapToleranceMin, PatternFlagParameterLimits.ElliottOverlapToleranceMax)]
        public decimal OverlapTolerance
        {
            get => _overlapTolerance;
            set => SetProperty(ref _overlapTolerance, value);
        }

        [DisplayName("Allow Truncation")]
        [Description("When on, Wave 5 is allowed to end without exceeding Wave 3 (fifth-wave failure).")]
        [Category("Rules")]
        public bool AllowTruncation
        {
            get => _allowTruncation;
            set => SetProperty(ref _allowTruncation, value);
        }

        public override void Validate()
        {
            base.Validate();
            ThrowIfOutOfRange(OverlapTolerance, PatternFlagParameterLimits.ElliottOverlapToleranceMin, PatternFlagParameterLimits.ElliottOverlapToleranceMax, nameof(OverlapTolerance));
        }
    }

    /// <summary>
    /// Parameters of the Elliott Wave Corrective ABC flag (plan ChartPatterns56 v2, run V2-7, integration spec 5.5; engine <c>ElliottWaveEngine.RunCorrective</c>).
    /// NEVER rename or move: the class name is the JSON type discriminator of the workspace.
    /// </summary>
    public class CoreElliottCorrectiveParameter : CoreElliottPatternParameterBase
    {
        private bool _requireWaveCExceedWaveA = true;

        public CoreElliottCorrectiveParameter()
            : base(PatternFlagParameterLimits.ElliottCorrectiveMinSpanDefault, PatternFlagParameterLimits.ElliottCorrectiveMaxSpanDefault)
        {
        }

        [DisplayName("Require Wave C > Wave A")]
        [Description("When on (zigzag mode), Wave C must exceed Wave A's endpoint. Turn off to allow flat/truncated ABC corrections.")]
        [Category("Rules")]
        public bool RequireWaveCExceedWaveA
        {
            get => _requireWaveCExceedWaveA;
            set => SetProperty(ref _requireWaveCExceedWaveA, value);
        }
    }
}
