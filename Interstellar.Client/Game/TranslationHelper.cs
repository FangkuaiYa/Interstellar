using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Interstellar.Voice;

public static class TranslationHelper
{
    private const string BlankText = "[BLANK]";
    private const int DefaultLanguage = 0;
    private const string DefaultLocale = "en";

    private static Dictionary<string, string>? _stringData;

    private static readonly Dictionary<int, string> LanguageMap = new()
    {
        [0] = "en",
        [4] = "ko",
        [9] = "de",
        [11] = "ja",
        [13] = "zh-Hans",
        [14] = "zh-Hant",
        [18] = "ar"
    };

    public static void Load()
    {
        var lang = (int)AmongUs.Data.DataManager.Settings.Language.CurrentLanguage;
        var locale = LanguageMap.TryGetValue(lang, out var mapped) ? mapped : DefaultLocale;

        if (!LoadLocale(locale) && locale != DefaultLocale)
            LoadLocale(DefaultLocale);
    }

    private static bool LoadLocale(string locale)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"Interstellar.Resources.Locales.{locale}.xml";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            InterstellarPlugin.Logger?.LogWarning($"[VC] Translation: {locale}.xml not found.");
            return false;
        }

        var bytes = new byte[stream.Length];
        stream.Read(bytes, 0, (int)stream.Length);
        var xml = Encoding.UTF8.GetString(bytes);

        _stringData = new Dictionary<string, string>();

        var doc = new XmlDocument();
        doc.LoadXml(xml);

        var stringNodes = doc.SelectNodes("//string[@name]");
        if (stringNodes == null) return false;

        foreach (XmlNode node in stringNodes)
        {
            if (node.Attributes?["name"]?.Value is string name && node.InnerText != BlankText)
                _stringData[name] = node.InnerText;
        }

        InterstellarPlugin.Logger?.LogInfo($"[VC] Translation: loaded {_stringData.Count} keys from {locale}.xml.");
        return true;
    }

    public static string Get(string key, string? defaultText = null)
    {
        defaultText ??= key;
        if (_stringData == null) return defaultText;

        var keyClean = Regex.Replace(key, "<.*?>", "");
        keyClean = Regex.Replace(keyClean, @"^-\s*", "");
        keyClean = keyClean.Trim();

        if (_stringData.TryGetValue(keyClean, out var translated))
            return translated;

        return defaultText;
    }

    /// <summary>
    /// Gets the translated string, formatting it with the given arguments.
    /// </summary>
    public static string Get(string key, string defaultText, params object[] args)
        => string.Format(Get(key, defaultText), args);
}
