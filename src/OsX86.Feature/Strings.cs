using System.Globalization;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace OsX86.Feature;

/// <summary>
/// The localized texts of the Feature (ADR 0104 of Kazo): <c>Resources/Strings.resx</c> (English,
/// neutral) and <c>Resources/Strings.fr.resx</c>, in the language of the OS the command runs in
/// (<c>IAppExecutionContext.Culture</c>, decision 289 A). The rest of the command still writes English.
/// </summary>
internal sealed class Strings
{
    private Strings()
    {
    }

    /// <summary>The texts in <paramref name="culture"/>; a key missing in French falls back to English.</summary>
    public static IStringLocalizer<Strings> For(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return new KazoStringLocalizer<Strings>(() => culture);
    }
}
