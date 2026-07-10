using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Cruncharr.Core.Utils;

public class Languages
{
    public static readonly LanguageItem[] languages ={
        new(){ CrLocale = "ja-JP", Locale = "ja", Code = "jpn", Name = "Japanese" },
        new(){ CrLocale = "de-DE", Locale = "de", Code = "deu", Name = "German" },
        new(){ CrLocale = "en-US", Locale = "en", Code = "eng", Name = "English" },
        new(){ CrLocale = "en-IN", Locale = "en-IN", Code = "eng", Name = "English (India)" },
        new(){ CrLocale = "es-419", Locale = "es-419", Code = "spa-419", Name = "Spanish", Language = "Latin American Spanish" },
        new(){ CrLocale = "es-ES", Locale = "es-ES", Code = "spa-ES", Name = "Castilian", Language = "European Spanish" },
        new(){ CrLocale = "pt-BR", Locale = "pt-BR", Code = "por", Name = "Portuguese", Language = "Brazilian Portuguese" },
        new(){ CrLocale = "pt-PT", Locale = "pt-PT", Code = "por", Name = "Portuguese (Portugal)", Language = "Portugues (Portugal)" },
        new(){ CrLocale = "fr-FR", Locale = "fr", Code = "fra", Name = "French" },
        new(){ CrLocale = "it-IT", Locale = "it", Code = "ita", Name = "Italian" },
        new(){ CrLocale = "pl-PL", Locale = "pl-PL", Code = "pol", Name = "Polish" },
        new(){ CrLocale = "id-ID", Locale = "id-ID", Code = "ind", Name = "Indonesian", Language = "Bahasa Indonesia" },
        new(){ CrLocale = "ms-MY", Locale = "ms-MY", Code = "may", Name = "Malay (Malaysia)", Language = "Bahasa Melayu" },
        new(){ CrLocale = "ca-ES", Locale = "ca-ES", Code = "cat", Name = "Catalan" },
        new(){ CrLocale = "vi-VN", Locale = "vi-VN", Code = "vie", Name = "Vietnamese", Language = "Tiếng Việt" },
        new(){ CrLocale = "tr-TR", Locale = "tr", Code = "tur", Name = "Turkish" },
        new(){ CrLocale = "ru-RU", Locale = "ru", Code = "rus", Name = "Russian" },
        new(){ CrLocale = "ar-SA", Locale = "ar-SA", Code = "ara", Name = "Arabic" },
        new(){ CrLocale = "hi-IN", Locale = "hi", Code = "hin", Name = "Hindi" },
        new(){ CrLocale = "ta-IN", Locale = "ta-IN", Code = "tam", Name = "Tamil (India)", Language = "தமிழ்" },
        new(){ CrLocale = "te-IN", Locale = "te-IN", Code = "tel", Name = "Telugu (India)", Language = "తెలుగు" },
        new(){ CrLocale = "zh-CN", Locale = "zh-CN", Code = "zho", Name = "Chinese (Mainland China)" },
        new(){ CrLocale = "zh-HK", Locale = "zh-HK", Code = "zho-HK", Name = "Chinese (Hong Kong)" },
        new(){ CrLocale = "zh-TW", Locale = "zh-TW", Code = "chi", Name = "Chinese (Taiwan)" },
        new(){ CrLocale = "ko-KR", Locale = "ko", Code = "kor", Name = "Korean" },
        new(){ CrLocale = "th-TH", Locale = "th-TH", Code = "tha", Name = "Thai", Language = "ไทย" },
    };

    public static readonly LanguageItem DEFAULT_lang = new LanguageItem
    {
        CrLocale = "und",
        Locale = "un",
        Code = "und",
        Name = string.Empty,
        Language = string.Empty
    };

    public static List<string> SortListByLangList(List<string> langList)
    {
        var orderMap = languages.Select((value, index) => new { Value = value.CrLocale, Index = index })
            .ToDictionary(x => x.Value, x => x.Index);
        langList.Sort((x, y) =>
        {
            bool xExists = orderMap.ContainsKey(x);
            bool yExists = orderMap.ContainsKey(y);

            if (xExists && yExists)
                return orderMap[x].CompareTo(orderMap[y]);
            else if (xExists)
                return -1;
            else if (yExists)
                return 1;
            else
                return string.CompareOrdinal(x, y);
        });

        return langList;
    }

    public static LanguageItem FixAndFindCrLc(string cr_locale)
    {
        if (string.IsNullOrEmpty(cr_locale))
        {
            return DEFAULT_lang;
        }

        string str = FixLanguageTag(cr_locale);
        return FindLang(str);
    }

    public static string FixLanguageTag(string tag)
    {
        tag = tag ?? "und";

        var match = Regex.Match(tag, @"^(\w{2})-?(\w{2})$");
        if (match.Success)
        {
            string tagLang = $"{match.Groups[1].Value}-{match.Groups[2].Value.ToUpper()}";

            var langObj = FindLang(tagLang);
            if (langObj.CrLocale != "und")
            {
                return langObj.CrLocale;
            }

            return tagLang;
        }

        return tag;
    }

    public static LanguageItem FindLang(string crLocale)
    {
        LanguageItem? lang = languages.FirstOrDefault(l => l.CrLocale == crLocale);
        if (lang?.CrLocale != null)
        {
            return lang;
        }
        else
        {
            return DEFAULT_lang;
        }
    }

    public static LanguageItem Locale2language(string locale)
    {
        LanguageItem? filteredLocale = languages.FirstOrDefault(l => { return l.Locale == locale || l.CrLocale == locale; });
        if (filteredLocale != null)
        {
            return filteredLocale;
        }
        else
        {
            return DEFAULT_lang;
        }
    }

    public static List<T> SortSubtitles<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties)] T>(List<T> data, string sortKey = "locale")
    {
        var idx = new Dictionary<string, int>();
        var tags = new HashSet<string>(languages.Select(e => e.Locale));

        int order = 1;
        foreach (var l in tags)
        {
            idx[l] = order++;
        }

        return data.OrderBy(item =>
        {
            var property = typeof(T).GetProperty(sortKey, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null) throw new ArgumentException($"Property '{sortKey}' not found on type '{typeof(T).Name}'.");

            var value = property.GetValue(item) as string ?? string.Empty;
            int index = idx.ContainsKey(value) ? idx[value] : 50;
            return index;
        }).ToList();
    }
}
