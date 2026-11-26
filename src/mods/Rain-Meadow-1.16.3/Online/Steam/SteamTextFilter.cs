#if MEADOW_STEAM_GAMEPLAY
using SteamKit2.Internal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace RainMeadow;

internal sealed class SteamTextFilter
{
    private const string DictionaryUrl = "https://community.akamai.steamstatic.com/textfilter/gettextfilterdictionary";
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(25);
    private const RegexOptions PatternOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private readonly SteamLobbySession session;
    private readonly Dictionary<string, Task<FilterRules>> rulesByLanguage = new();
    private Task<(ETextFilterSetting Setting, CPlayer_TextFilterWords? Words)>? preferences;

    private sealed class FilterRules(Regex? mask, Regex? allow)
    {
        internal string Apply(string text)
        {
            try
            {
                return mask?.Replace(text, match => allow?.IsMatch(match.Value) == true
                    ? match.Value : new string('♥', match.Length)) ?? text;
            }
            catch (RegexMatchTimeoutException)
            {
                return text;
            }
        }
    }

    internal SteamTextFilter(SteamLobbySession session) => this.session = session;

    internal void Prepare()
    {
        if (RainMeadow.rainMeadowOptions?.ProfanityFilter.Value == true)
            GetRules();
    }

    internal string Apply(string text)
    {
        if (string.IsNullOrEmpty(text) || RainMeadow.rainMeadowOptions?.ProfanityFilter.Value != true)
            return text;

        var rules = GetRules();
        return rules.Status == TaskStatus.RanToCompletion ? rules.Result.Apply(text) : text;
    }

    private Task<FilterRules> GetRules()
    {
        string language = RWCustom.Custom.rainWorld?.inGameTranslator?.currentLanguage?.value?.ToLowerInvariant() switch
        {
            "chinese" or "chinesesimplified" or "simpchinese" => "schinese",
            "traditionalchinese" or "chinesetraditional" or "tradchinese" => "tchinese",
            "korean" => "koreana",
            "japanese" => "japanese",
            "french" => "french",
            "italian" => "italian",
            "german" => "german",
            "spanish" => "spanish",
            "portuguese" => "portuguese",
            "russian" => "russian",
            "thai" => "thai",
            _ => "english",
        };
        if (!rulesByLanguage.TryGetValue(language, out var rules))
        {
            rules = LoadRulesAsync(language);
            rulesByLanguage.Add(language, rules);
        }
        return rules;
    }

    private async Task<FilterRules> LoadRulesAsync(string language)
    {
        try
        {
            preferences ??= LoadPreferencesAsync();
            var (setting, words) = await preferences;
            if (setting == ETextFilterSetting.k_ETextFilterSettingDisabled)
                return new FilterRules(null, null);

            var languages = language == "english" ? new[] { "english" } : new[] { "english", language };
            var blocked = new List<Task<(string Language, string? Contents)>>();
            var allowed = new List<Task<(string Language, string? Contents)>>();
            foreach (string selectedLanguage in languages)
            {
                blocked.Add(LoadDictionaryAsync("banned", selectedLanguage));
                if (setting != ETextFilterSetting.k_ETextFilterSettingEnabledAllowProfanity)
                    blocked.Add(LoadDictionaryAsync("profanity", selectedLanguage));
                allowed.Add(LoadDictionaryAsync("clean_public", selectedLanguage));
            }
            var blockedDictionaries = await Task.WhenAll(blocked);
            var allowedDictionaries = await Task.WhenAll(allowed);
            return await Task.Run(() => new FilterRules(
                BuildPattern(blockedDictionaries, words?.text_filter_custom_banned_words, language, wholeMatch: false),
                BuildPattern(allowedDictionaries, words?.text_filter_custom_clean_words, language, wholeMatch: true)));
        }
        catch (Exception error)
        {
            RainMeadow.Warn("Steam text filtering could not be initialized: " + error.GetType().Name);
            return new FilterRules(null, null);
        }
    }

    private async Task<(ETextFilterSetting, CPlayer_TextFilterWords?)> LoadPreferencesAsync()
    {
        var setting = ETextFilterSetting.k_ETextFilterSettingEnabled;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            var response = await session.ReadTextFilterSettingAsync<CPlayer_GetCommunityPreferences_Request,
                CPlayer_GetCommunityPreferences_Response>("Player.GetCommunityPreferences#1",
                new CPlayer_GetCommunityPreferences_Request(), timeout.Token);
            if (response?.preferences is not CPlayer_CommunityPreferences settings) return (setting, null);
            setting = settings.text_filter_setting;
            if (settings.text_filter_words_revision == 0) return (setting, null);

            var words = await session.ReadTextFilterSettingAsync<CPlayer_GetTextFilterWords_Request,
                CPlayer_GetTextFilterWords_Response>("Player.GetTextFilterWords#1",
                new CPlayer_GetTextFilterWords_Request(), timeout.Token);
            return (setting, words?.words);
        }
        catch (Exception)
        {
            return (setting, null);
        }
    }

    private static async Task<(string Language, string? Contents)> LoadDictionaryAsync(string kind, string language)
    {
        using var request = UnityWebRequest.Get($"{DictionaryUrl}?type={kind}&language={language}&v=1&origin=https%3A%2F%2Fsteamcommunity.com");
        request.timeout = 8;
        request.redirectLimit = 0;
        var operation = request.SendWebRequest();
        while (!operation.isDone)
        {
            if (request.downloadedBytes > 256 * 1024)
            {
                request.Abort();
                return (language, null);
            }
            await Task.Delay(25);
        }

        if (request.result != UnityWebRequest.Result.Success || request.downloadedBytes > 256 * 1024)
            return (language, null);
        return (language, request.downloadHandler.text);
    }

    private static Regex? BuildPattern(IEnumerable<(string Language, string? Contents)> dictionaries,
        IEnumerable<string>? customWords, string language, bool wholeMatch)
    {
        var patterns = new HashSet<string>();
        foreach (var (dictionaryLanguage, dictionary) in dictionaries)
        {
            if (string.IsNullOrWhiteSpace(dictionary) || dictionary.TrimStart().StartsWith("<", StringComparison.Ordinal))
                continue;
            foreach (var entry in dictionary.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string word = entry.Trim().TrimStart('\ufeff');
                if (word.Length == 0) continue;
                try
                {
                    _ = new Regex(word, PatternOptions, MatchTimeout);
                    patterns.Add(WrapPattern(word, dictionaryLanguage, wholeMatch));
                }
                catch (ArgumentException)
                {
                    // Skip a dictionary expression this regex engine cannot interpret.
                }
            }
        }
        if (customWords != null)
        {
            foreach (var word in customWords.Where(word => !string.IsNullOrWhiteSpace(word)))
                patterns.Add(WrapPattern(Regex.Escape(word), language, wholeMatch));
        }
        return patterns.Count == 0 ? null : new Regex(string.Join("|", patterns), PatternOptions, MatchTimeout);
    }

    private static string WrapPattern(string word, string language, bool wholeMatch)
    {
        string pattern = "(?:" + word + ")";
        if (wholeMatch) return @"\A" + pattern + @"\z";
        if (language is "schinese" or "tchinese" or "japanese" or "koreana" or "thai") return pattern;
        return @"(?<![\p{L}\p{M}\p{N}_])" + pattern + @"(?![\p{L}\p{M}\p{N}_])";
    }
}
#endif
