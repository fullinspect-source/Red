using System;
using System.Collections.Generic;
using System.Linq;

namespace InspectionEditor.Services
{
    /// <summary>
    /// ANSI/RESNET/ICC 380 multipoint blower door test calculation.
    /// Uses unweighted log-linearized linear regression (Qenv = C * ΔP^n)
    /// to determine the pressure exponent n and flow coefficient C, then
    /// extrapolates to cfm50 = C * 50^n.
    ///
    /// Density corrections are omitted — acceptable for low-elevation markets
    /// (Houston, Phoenix, Southern Louisiana) where the correction is &lt;1-2%.
    /// </summary>
    public static class MultipointCalculator
    {
        public record Result(
            double Cfm50,
            double N,
            double C,
            bool IsValid,
            string? ErrorMessage);

        /// <param name="points">
        /// Sequence of (Pa, CFM) pairs as recorded by the blower door device.
        /// At least 2 valid points (Pa &gt; 0 and CFM &gt; 0) are required.
        /// RESNET 380 standard uses 5 points: 60, 48, 35, 23, 10 Pa.
        /// </param>
        public static Result Calculate(IEnumerable<(double Pa, double Cfm)> points)
        {
            var pts = points.Where(p => p.Pa > 0 && p.Cfm > 0).ToList();

            if (pts.Count < 2)
                return new Result(0, 0, 0, false,
                    $"Need at least 2 valid pressure/CFM pairs (got {pts.Count}).");

            // Log-linearise: ln(Q) = ln(C) + n*ln(P)
            double[] x = pts.Select(p => Math.Log(p.Pa)).ToArray();
            double[] y = pts.Select(p => Math.Log(p.Cfm)).ToArray();
            int count = pts.Count;

            double sumX  = x.Sum();
            double sumY  = y.Sum();
            double sumXY = x.Zip(y, (xi, yi) => xi * yi).Sum();
            double sumX2 = x.Select(xi => xi * xi).Sum();

            double denom = count * sumX2 - sumX * sumX;
            if (Math.Abs(denom) < 1e-10)
                return new Result(0, 0, 0, false,
                    "Degenerate data — pressure points are too close together.");

            double n         = (count * sumXY - sumX * sumY) / denom;
            double lnC       = (sumY - n * sumX) / count;
            double c         = Math.Exp(lnC);
            double cfm50     = c * Math.Pow(50.0, n);

            if (double.IsNaN(cfm50) || double.IsInfinity(cfm50) || cfm50 <= 0)
                return new Result(0, n, c, false,
                    "Regression produced an invalid result — check your CFM readings.");

            return new Result(cfm50, n, c, true, null);
        }
    }
}
