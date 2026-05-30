namespace Booking;

/// <summary>Allowed values for passenger fields in booking setup UI (matches IRCTC labels).</summary>
public static class PassengerFieldOptions
{
    public static IReadOnlyList<string> Genders { get; } =
    [
        "Male",
        "Female",
        "Transgender"
    ];

    public static IReadOnlyList<string> Berths { get; } =
    [
        "No Preference",
        "Lower",
        "Middle",
        "Upper",
        "Side Lower",
        "Side Upper"
    ];

    public static IReadOnlyList<string> Countries { get; }

    static PassengerFieldOptions()
    {
        var countries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "India" };
        foreach (var name in CountryNames)
        {
            countries.Add(name);
        }

        var sorted = countries
            .Where(c => !c.Equals("India", StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
        sorted.Insert(0, "India");
        Countries = sorted;
    }

    public static string CoerceGender(string? value)
    {
        var normalized = PassengerValueNormalizer.NormalizeGender(value);
        return Genders.FirstOrDefault(g => g.Equals(normalized, StringComparison.OrdinalIgnoreCase)) ?? Genders[0];
    }

    public static string CoerceBerth(string? value)
    {
        var normalized = PassengerValueNormalizer.NormalizeBerth(value);
        return Berths.FirstOrDefault(b => b.Equals(normalized, StringComparison.OrdinalIgnoreCase)) ?? Berths[0];
    }

    public static string CoerceCountry(string? value)
    {
        var normalized = PassengerValueNormalizer.NormalizeCountry(value);
        return Countries.FirstOrDefault(c => c.Equals(normalized, StringComparison.OrdinalIgnoreCase)) ?? "India";
    }

    private static readonly string[] CountryNames =
    [
        "Afghanistan", "Aland Islands", "Albania", "Algeria", "American Samoa", "Andorra", "Angola",
        "Anguilla", "Antarctica", "Antigua and Barbuda", "Argentina", "Armenia", "Aruba", "Australia",
        "Austria", "Azerbaijan", "BES Islands", "Bahamas", "Bahrain", "Bangladesh", "Barbados", "Belarus",
        "Belgium", "Belize", "Benin", "Bermuda", "Bhutan", "Bolivia", "Bosnia-Herzegovina", "Botswana",
        "Bouvet Island", "Brazil", "British IOT", "British Virgin Islands", "Brunei Darussalam", "Bulgaria",
        "Burkina Faso", "Burundi", "CURACAO", "Cambodia", "Cameroon", "Canada", "Cape Verde",
        "Cayman Islands", "Central African Republic", "Chad", "Chile", "China", "Christmas Island",
        "Cocos Islands", "Colombia", "Comoros", "Cook Islands", "Costa Rica", "Croatia", "Cuba", "Cyprus",
        "Czech Republic", "Côte d'Ivoire", "DR Congo", "Denmark", "Djibouti", "Dominica",
        "Dominican Republic", "Ecuador", "Egypt", "El Salvador", "Equatorial Guinea", "Eritrea", "Estonia",
        "Ethiopia", "FS Micronesia", "Falkland Islands", "Faroe Islands", "Fiji", "Finland", "France",
        "French Guiana", "French Polynesia", "French Southern Lands", "Gabon", "Gambia", "Georgia",
        "Germany", "Ghana", "Gibraltar", "Great Britain", "Greece", "Greenland", "Grenada", "Guadeloupe",
        "Guam", "Guatemala", "Guernsey", "Guinea", "Guinea Bissau", "Guyana", "Haiti",
        "Heard & Mcdonald Islands", "Honduras", "Hong Kong", "Hungary", "Iceland", "Indonesia", "Iran",
        "Iraq", "Ireland", "Isle of Man", "Israel", "Italy", "Jamaica", "Japan", "Jersey", "Jordan",
        "Kazakhstan", "Kenya", "Kiribati", "Kuwait", "Kyrgyzstan", "Lao PDR", "Latvia", "Lebanon",
        "Lesotho", "Liberia", "Libya", "Liechtenstein", "Lithuania", "Luxembourg", "Macao", "Madagascar",
        "Malawi", "Malaysia", "Maldives", "Mali", "Malta", "Marshall Islands", "Martinique", "Mauritania",
        "Mauritius", "Mayotte", "Mexico", "Moldova", "Monaco", "Mongolia", "Montenegro", "Montserrat",
        "Morocco", "Mozambique", "Myanmar", "NRI", "Namibia", "Nauru", "Nepal", "Netherlands",
        "Netherlands Antilles", "New Caledonia", "New Zealand", "Nicaragua", "Niger", "Nigeria", "Niue",
        "Norfolk Island", "North Korea", "Northern Mariana Islands", "Norway", "Oman", "Pakistan", "Palau",
        "Palestine", "Panama", "Papua New Guinea", "Paraguay", "Peru", "Philippines", "Pitcairn", "Poland",
        "Portugal", "Puerto Rico", "Qatar", "Republic of Korea", "Republic of Macedonia",
        "Republic of the Congo", "Romania", "Russian Federation", "Rwanda", "Réunion", "Saint Barthelemy",
        "Saint Helena", "Saint Lucia", "Saint-Martin", "Samoa", "San Marino", "Sao Tome and Principe",
        "Saudi Arabia", "Senegal", "Serbia", "Seychelles", "Sierra Leone", "Singapore", "Sint Maarten",
        "Slovakia", "Slovenia", "Solomon Islands", "Somalia", "South Africa", "South Sudan", "Spain",
        "Sri Lanka", "St Kitts and Nevis", "St Pierre & Miquelon", "St Vincent & Grenadines", "Sudan",
        "Suriname", "Svalbard & Jan Mayen Iss", "Swaziland", "Sweden", "Switzerland", "Syria", "Taiwan",
        "Tajikistan", "Tanzania", "Thailand", "Timor-Leste", "Togo", "Tokelau", "Tonga",
        "Trinidad and Tobago", "Tunisia", "Turkey", "Turkmenistan", "Turks and Caicos Iss", "Tuvalu",
        "US Minor Outlying Iss", "Uganda", "Ukraine", "United Arab Emirates", "United Kingdom",
        "United States of America", "Uruguay", "Uzbekistan", "Vanuatu", "Vatican City", "Venezuela",
        "VietNam", "Virgin Islands, US", "Wallis and Futuna Iss", "Western Sahara", "Yemen", "Zambia",
        "Zimbabwe"
    ];
}
