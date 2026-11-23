using System;

namespace StockAnalyzer.Core.MathUtils;

public static partial class DslQuadratureMath
{
    /// <summary>Maximum number of adaptive splits (anisotropic 2-way or isotropic 4-way) in <see cref="Quad2dGk{TFunc}"/>.</summary>
    public const int Max2dSubdivisions = 5;

    private const int Max2dActiveLeafPanels = 1 + (3 * Max2dSubdivisions); // 16
    private const int PointsPer2dPanel = 225;                              // 15 x 15
    private const int OuterBudgetPer2dPanel = PointsPer2dPanel / 16;       // 14
    private const double Quad2dAcceptTol = 1e-9;
    private const double Quad2dFinalTol = 1e-5;
    private const double AnisotropicRatioThreshold = 4.0;

    /// <summary>
    /// Adaptive 2D Gauss-Kronrod G7/K15 tensor-product integration of <c>f(x, y)</c> over
    /// <c>[ax, bx] x [ay, by]</c> with zero-extra-evaluation directional errors (<c>I_KK - I_GK</c>, <c>I_KK - I_KG</c>)
    /// driving anisotropic bisection vs. 4-way quadrant splitting (max 5 splits, max 16 active leaf panels).
    /// </summary>
    public static MathResult<double> Quad2dGk<TFunc>(
        TFunc f,
        double ax,
        double bx,
        double ay,
        double by,
        ref DslExecutionBudget budget)
        where TFunc : struct, IBinaryScalarFunction
    {
        if (!double.IsFinite(ax) || !double.IsFinite(bx) || !double.IsFinite(ay) || !double.IsFinite(by))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        if (ax == bx || ay == by)
        {
            return MathResult<double>.Ok(0.0);
        }

        double sign = 1.0;
        if (ax > bx)
        {
            (ax, bx) = (bx, ax);
            sign = -sign;
        }

        if (ay > by)
        {
            (ay, by) = (by, ay);
            sign = -sign;
        }

        Span<double> px0 = stackalloc double[Max2dActiveLeafPanels];
        Span<double> px1 = stackalloc double[Max2dActiveLeafPanels];
        Span<double> py0 = stackalloc double[Max2dActiveLeafPanels];
        Span<double> py1 = stackalloc double[Max2dActiveLeafPanels];
        Span<double> pRes = stackalloc double[Max2dActiveLeafPanels];
        Span<double> pErr = stackalloc double[Max2dActiveLeafPanels];
        Span<double> pErrX = stackalloc double[Max2dActiveLeafPanels];
        Span<double> pErrY = stackalloc double[Max2dActiveLeafPanels];

        px0[0] = ax;
        px1[0] = bx;
        py0[0] = ay;
        py1[0] = by;
        MathErrorCode rootErr = EvaluatePanel15x15(
            f, ax, bx, ay, by, ref budget,
            out pRes[0], out pErr[0], out pErrX[0], out pErrY[0]);
        if (rootErr != MathErrorCode.None)
        {
            return MathResult<double>.Fail(rootErr);
        }

        int activeCount = 1;
        double totalRes = pRes[0];
        double totalErr = pErr[0];

        for (int split = 0; split < Max2dSubdivisions; split++)
        {
            if (totalErr <= Quad2dAcceptTol * Math.Max(1.0, Math.Abs(totalRes)))
            {
                double early = sign * totalRes;
                return MathResult<double>.Ok(early == 0.0 ? 0.0 : early);
            }

            int worstIdx = 0;
            double maxPanelErr = pErr[0];
            for (int k = 1; k < activeCount; k++)
            {
                if (pErr[k] > maxPanelErr)
                {
                    maxPanelErr = pErr[k];
                    worstIdx = k;
                }
            }

            double x0 = px0[worstIdx];
            double x1 = px1[worstIdx];
            double y0 = py0[worstIdx];
            double y1 = py1[worstIdx];
            double ex = pErrX[worstIdx];
            double ey = pErrY[worstIdx];
            double widthX = x1 - x0;
            double widthY = y1 - y0;
            bool canSplitX = widthX > 1e-14 * Math.Max(1.0, Math.Abs(x0) + Math.Abs(x1));
            bool canSplitY = widthY > 1e-14 * Math.Max(1.0, Math.Abs(y0) + Math.Abs(y1));

            if (canSplitX && ex > AnisotropicRatioThreshold * Math.Max(ey, 1e-16))
            {
                if (activeCount + 1 > Max2dActiveLeafPanels)
                {
                    break;
                }

                double xm = 0.5 * (x0 + x1);
                MathErrorCode err0 = EvaluatePanel15x15(f, x0, xm, y0, y1, ref budget, out double r0, out double e0, out double ex0, out double ey0);
                if (err0 != MathErrorCode.None) return MathResult<double>.Fail(err0);
                MathErrorCode err1 = EvaluatePanel15x15(f, xm, x1, y0, y1, ref budget, out double r1, out double e1, out double ex1, out double ey1);
                if (err1 != MathErrorCode.None) return MathResult<double>.Fail(err1);

                px0[worstIdx] = x0; px1[worstIdx] = xm; py0[worstIdx] = y0; py1[worstIdx] = y1;
                pRes[worstIdx] = r0; pErr[worstIdx] = e0; pErrX[worstIdx] = ex0; pErrY[worstIdx] = ey0;

                int s1 = activeCount++;
                px0[s1] = xm; px1[s1] = x1; py0[s1] = y0; py1[s1] = y1;
                pRes[s1] = r1; pErr[s1] = e1; pErrX[s1] = ex1; pErrY[s1] = ey1;
            }
            else if (canSplitY && ey > AnisotropicRatioThreshold * Math.Max(ex, 1e-16))
            {
                if (activeCount + 1 > Max2dActiveLeafPanels)
                {
                    break;
                }

                double ym = 0.5 * (y0 + y1);
                MathErrorCode err0 = EvaluatePanel15x15(f, x0, x1, y0, ym, ref budget, out double r0, out double e0, out double ex0, out double ey0);
                if (err0 != MathErrorCode.None) return MathResult<double>.Fail(err0);
                MathErrorCode err1 = EvaluatePanel15x15(f, x0, x1, ym, y1, ref budget, out double r1, out double e1, out double ex1, out double ey1);
                if (err1 != MathErrorCode.None) return MathResult<double>.Fail(err1);

                px0[worstIdx] = x0; px1[worstIdx] = x1; py0[worstIdx] = y0; py1[worstIdx] = ym;
                pRes[worstIdx] = r0; pErr[worstIdx] = e0; pErrX[worstIdx] = ex0; pErrY[worstIdx] = ey0;

                int s1 = activeCount++;
                px0[s1] = x0; px1[s1] = x1; py0[s1] = ym; py1[s1] = y1;
                pRes[s1] = r1; pErr[s1] = e1; pErrX[s1] = ex1; pErrY[s1] = ey1;
            }
            else
            {
                if (activeCount + 3 > Max2dActiveLeafPanels)
                {
                    break;
                }

                double xm = 0.5 * (x0 + x1);
                double ym = 0.5 * (y0 + y1);

                MathErrorCode err0 = EvaluatePanel15x15(f, x0, xm, y0, ym, ref budget, out double r0, out double e0, out double ex0, out double ey0);
                if (err0 != MathErrorCode.None) return MathResult<double>.Fail(err0);
                MathErrorCode err1 = EvaluatePanel15x15(f, xm, x1, y0, ym, ref budget, out double r1, out double e1, out double ex1, out double ey1);
                if (err1 != MathErrorCode.None) return MathResult<double>.Fail(err1);
                MathErrorCode err2 = EvaluatePanel15x15(f, x0, xm, ym, y1, ref budget, out double r2, out double e2, out double ex2, out double ey2);
                if (err2 != MathErrorCode.None) return MathResult<double>.Fail(err2);
                MathErrorCode err3 = EvaluatePanel15x15(f, xm, x1, ym, y1, ref budget, out double r3, out double e3, out double ex3, out double ey3);
                if (err3 != MathErrorCode.None) return MathResult<double>.Fail(err3);

                px0[worstIdx] = x0; px1[worstIdx] = xm; py0[worstIdx] = y0; py1[worstIdx] = ym;
                pRes[worstIdx] = r0; pErr[worstIdx] = e0; pErrX[worstIdx] = ex0; pErrY[worstIdx] = ey0;

                int s1 = activeCount++;
                px0[s1] = xm; px1[s1] = x1; py0[s1] = y0; py1[s1] = ym;
                pRes[s1] = r1; pErr[s1] = e1; pErrX[s1] = ex1; pErrY[s1] = ey1;

                int s2 = activeCount++;
                px0[s2] = x0; px1[s2] = xm; py0[s2] = ym; py1[s2] = y1;
                pRes[s2] = r2; pErr[s2] = e2; pErrX[s2] = ex2; pErrY[s2] = ey2;

                int s3 = activeCount++;
                px0[s3] = xm; px1[s3] = x1; py0[s3] = ym; py1[s3] = y1;
                pRes[s3] = r3; pErr[s3] = e3; pErrX[s3] = ex3; pErrY[s3] = ey3;
            }

            totalRes = 0.0;
            totalErr = 0.0;
            for (int k = 0; k < activeCount; k++)
            {
                totalRes += pRes[k];
                totalErr += pErr[k];
            }
        }

        if (double.IsFinite(totalRes) && totalErr <= Quad2dFinalTol * Math.Max(1.0, Math.Abs(totalRes)))
        {
            double value = sign * totalRes;
            return MathResult<double>.Ok(value == 0.0 ? 0.0 : value);
        }

        return MathResult<double>.Fail(MathErrorCode.ConvergenceFailure);
    }

    private static MathErrorCode EvaluatePanel15x15<TFunc>(
        TFunc f,
        double x0,
        double x1,
        double y0,
        double y1,
        ref DslExecutionBudget budget,
        out double res15,
        out double err15,
        out double errX,
        out double errY)
        where TFunc : struct, IBinaryScalarFunction
    {
        if (!budget.TryConsume(OuterBudgetPer2dPanel))
        {
            res15 = 0.0;
            err15 = 0.0;
            errX = 0.0;
            errY = 0.0;
            return MathErrorCode.BudgetExceeded;
        }

        Span<double> t = stackalloc double[15];
        Span<double> w15 = stackalloc double[15];
        Span<double> w7 = stackalloc double[15];
        for (int k = 0; k < 7; k++)
        {
            double node = Gk15Nodes[k];
            double wk15 = Gk15Weights[k];
            double wk7 = (k & 1) == 1 ? Gk7Weights[(k - 1) >> 1] : 0.0;
            t[k] = -node;
            t[14 - k] = node;
            w15[k] = wk15;
            w15[14 - k] = wk15;
            w7[k] = wk7;
            w7[14 - k] = wk7;
        }

        t[7] = 0.0;
        w15[7] = Gk15Weights[7];
        w7[7] = Gk7Weights[3];

        double cx = 0.5 * (x0 + x1);
        double hx = 0.5 * (x1 - x0);
        double cy = 0.5 * (y0 + y1);
        double hy = 0.5 * (y1 - y0);

        double sumKK = 0.0;
        double sumKG = 0.0;
        double sumGK = 0.0;
        double sumGG = 0.0;
        for (int ix = 0; ix < 15; ix++)
        {
            double x = cx + hx * t[ix];
            double wx15 = w15[ix];
            double wx7 = w7[ix];
            for (int iy = 0; iy < 15; iy++)
            {
                double y = cy + hy * t[iy];
                budget.LastError = MathErrorCode.None;
                if (!f.TryEvaluate(x, y, ref budget, out double z))
                {
                    res15 = 0.0;
                    err15 = 0.0;
                    errX = 0.0;
                    errY = 0.0;
                    return DslNumericalAnalysisMath.ResolveFunctorError(in budget);
                }

                if (!double.IsFinite(z))
                {
                    res15 = 0.0;
                    err15 = 0.0;
                    errX = 0.0;
                    errY = 0.0;
                    return MathErrorCode.NonFiniteResult;
                }

                double wy15 = w15[iy];
                double wy7 = w7[iy];
                sumKK += wx15 * wy15 * z;
                if (wy7 != 0.0)
                {
                    sumKG += wx15 * wy7 * z;
                }

                if (wx7 != 0.0)
                {
                    sumGK += wx7 * wy15 * z;
                    if (wy7 != 0.0)
                    {
                        sumGG += wx7 * wy7 * z;
                    }
                }
            }
        }

        double areaHalf = hx * hy;
        res15 = areaHalf * sumKK;
        double resKG = areaHalf * sumKG;
        double resGK = areaHalf * sumGK;
        double resGG = areaHalf * sumGG;
        if (!double.IsFinite(res15) || !double.IsFinite(resKG) || !double.IsFinite(resGK) || !double.IsFinite(resGG))
        {
            err15 = 0.0;
            errX = 0.0;
            errY = 0.0;
            return MathErrorCode.NonFiniteResult;
        }

        errX = Math.Abs(res15 - resGK);
        errY = Math.Abs(res15 - resKG);
        err15 = Math.Max(Math.Abs(res15 - resGG), Math.Max(errX, errY));
        return MathErrorCode.None;
    }
}
