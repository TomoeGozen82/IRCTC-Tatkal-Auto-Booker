namespace Booking;

internal static class PassengerValueNormalizer
{
    public static string NormalizeGender(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Male";
        }

        return value.Trim().ToUpperInvariant() switch
        {
            "M" or "MALE" => "Male",
            "F" or "FEMALE" => "Female",
            "T" or "TRANSGENDER" or "TG" => "Transgender",
            _ => value.Trim()
        };
    }

    public static string NormalizeBerth(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "No Preference";
        }

        return value.Trim().ToUpperInvariant() switch
        {
            "NP" or "NO PREFERENCE" or "NO-PREFERENCE" or "" => "No Preference",
            "LB" or "LOWER" => "Lower",
            "MB" or "MIDDLE" => "Middle",
            "UB" or "UPPER" => "Upper",
            "SL" or "SIDE LOWER" or "SIDE-LOWER" => "Side Lower",
            "SU" or "SIDE UPPER" or "SIDE-UPPER" => "Side Upper",
            _ => value.Trim()
        };
    }

    public static string NormalizeCountry(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "India" : value.Trim();

    /// <summary>IRCTC select value: M, F, T.</summary>
    public static string ToIrctcGenderCode(string? value) =>
        NormalizeGender(value).ToUpperInvariant() switch
        {
            "MALE" => "M",
            "FEMALE" => "F",
            "TRANSGENDER" => "T",
            _ when value?.Trim().Length == 1 => value.Trim().ToUpperInvariant(),
            _ => "M"
        };

    /// <summary>IRCTC select value: empty (no pref), LB, MB, UB, SL, SU.</summary>
    public static string ToIrctcBerthCode(string? value)
    {
        var normalized = NormalizeBerth(value);
        if (normalized == "No Preference")
        {
            return string.Empty;
        }

        return normalized switch
        {
            "Lower" => "LB",
            "Middle" => "MB",
            "Upper" => "UB",
            "Side Lower" => "SL",
            "Side Upper" => "SU",
            _ when value?.Trim().Length == 2 => value.Trim().ToUpperInvariant(),
            _ => value?.Trim().ToUpperInvariant() ?? string.Empty
        };
    }

    /// <summary>IRCTC nationality option value (ISO-style code, e.g. IN).</summary>
    public static string ToIrctcNationalityCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "IN";
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 2 && trimmed.All(char.IsLetter))
        {
            return trimmed.ToUpperInvariant();
        }

        return CountryNameToCode.TryGetValue(trimmed, out var code)
            ? code
            : trimmed.ToUpperInvariant() switch
            {
                "INDIA" => "IN",
                "UNITED STATES" or "UNITED STATES OF AMERICA" or "USA" => "US",
                "UNITED KINGDOM" or "GREAT BRITAIN" or "UK" => "GB",
                "VIETNAM" or "VIET NAM" => "VN",
                "UAE" or "UNITED ARAB EMIRATES" => "AE",
                _ => "IN"
            };
    }

    private static readonly Dictionary<string, string> CountryNameToCode =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["India"] = "IN",
            ["Afghanistan"] = "AF",
            ["Australia"] = "AU",
            ["Bangladesh"] = "BG",
            ["Canada"] = "CA",
            ["China"] = "CN",
            ["France"] = "FR",
            ["Germany"] = "DE",
            ["Japan"] = "JP",
            ["Nepal"] = "NP",
            ["Pakistan"] = "PK",
            ["Singapore"] = "SG",
            ["Sri Lanka"] = "LK",
            ["Thailand"] = "TH",
            ["United States"] = "US",
            ["United States of America"] = "US",
            ["United Kingdom"] = "GB",
            ["VietNam"] = "VN",
            ["Vietnam"] = "VN"
        };
}
