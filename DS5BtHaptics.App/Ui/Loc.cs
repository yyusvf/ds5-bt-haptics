using System.Globalization;
using System.Windows.Markup;

namespace DS5BtHaptics.App.Ui;

internal enum AppLanguage
{
    System,
    German,
    English,
}

/// <summary>
/// German or English, picked once per flyout: texts sit next to each other in the code
/// (<c>Loc.T("Akku", "Battery")</c>, in XAML <c>{ui:T 'Akku', 'Battery'}</c>), so nothing can go missing
/// in one language. The log stays German.
/// </summary>
internal static class Loc
{
    public static bool English { get; private set; } = !IsGermanSystem;

    private static bool IsGermanSystem => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";

    public static void Apply(AppLanguage language) => English = language switch
    {
        AppLanguage.German => false,
        AppLanguage.English => true,
        _ => !IsGermanSystem,
    };

    public static string T(string german, string english) => English ? english : german;
}

/// <summary>XAML side of <see cref="Loc"/>: <c>Text="{ui:T 'Einstellungen', 'Settings'}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class T(string german, string english) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(german, english);
}
