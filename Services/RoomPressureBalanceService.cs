using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace InspectionEditor.Services
{
    internal enum RoomPressureBalanceState
    {
        NotApplicable,
        Pending,
        Pass,
        Caution,
        Fail
    }

    internal readonly record struct RoomPressureBalanceResult(
        RoomPressureBalanceState State,
        decimal? RawPa = null,
        int? RoundedPa = null);

    internal static class RoomPressureBalanceService
    {
        private static readonly Regex NumericPaPattern = new(
            @"^\s*([+-]?(?:\d+(?:\.\d+)?|\.\d+))\s*(?:pa)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);


        internal static bool IsPressureBalancePrompt(string? prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return false;

            string text = prompt.Trim();
            bool identifiesRoom =
                text.Contains("bedroom", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("room to room", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("room-to-room", StringComparison.OrdinalIgnoreCase);

            return identifiesRoom &&
                   text.Contains("pressure", StringComparison.OrdinalIgnoreCase);
        }

        internal static RoomPressureBalanceResult Evaluate(string? prompt, string? rawValue)
        {
            if (!IsPressureBalancePrompt(prompt))
                return new RoomPressureBalanceResult(RoomPressureBalanceState.NotApplicable);

            if (!TryParsePa(rawValue, out decimal rawPa))
                return new RoomPressureBalanceResult(RoomPressureBalanceState.Pending);

            int roundedPa = decimal.ToInt32(decimal.Round(rawPa, 0, MidpointRounding.AwayFromZero));
            int magnitude = Math.Abs(roundedPa);
            RoomPressureBalanceState state = magnitude switch
            {
                <= 3 => RoomPressureBalanceState.Pass,
                <= 5 => RoomPressureBalanceState.Caution,
                _ => RoomPressureBalanceState.Fail
            };

            return new RoomPressureBalanceResult(state, rawPa, roundedPa);
        }

        private static bool TryParsePa(string? rawValue, out decimal pa)
        {
            pa = 0;
            if (string.IsNullOrWhiteSpace(rawValue))
                return false;

            Match match = NumericPaPattern.Match(rawValue);
            return match.Success &&
                   decimal.TryParse(
                       match.Groups[1].Value,
                       NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                       CultureInfo.InvariantCulture,
                       out pa);
        }
    }
}
