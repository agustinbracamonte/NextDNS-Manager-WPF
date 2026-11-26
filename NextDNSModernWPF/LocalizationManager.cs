#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;

namespace NextDNSModernWPF;

/// <summary>
/// Resolves the user's application language, persists their preference per Windows
/// user, and exposes translations to both code and WPF bindings.
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    private const string AutoLanguage = "auto";
    private const string DefaultLanguage = "es";
    private const string SettingsFileName = "language.json";
    private static readonly object Gate = new();
    private static readonly string[] LanguageCodes = ["es", "en", "fr", "pt", "de"];
    private static readonly IReadOnlyList<string> ReadOnlyLanguageCodes = Array.AsReadOnly(LanguageCodes);
    private static readonly IReadOnlyList<LanguageOption> ReadOnlyLanguageOptions = Array.AsReadOnly<LanguageOption>(
    [
        new LanguageOption(AutoLanguage, "Auto"),
        new LanguageOption("es", "Español"),
        new LanguageOption("en", "English"),
        new LanguageOption("fr", "Français"),
        new LanguageOption("pt", "Português"),
        new LanguageOption("de", "Deutsch")
    ]);
    private static readonly Dictionary<string, Dictionary<string, string>> TranslationTable = LoadTranslations();
    private static readonly Regex PlaceholderPattern = new(@"\{(?<index>\d+)(?<format>:[^{}]*)?\}", RegexOptions.CultureInvariant);
    private static readonly TranslationTemplateIndex TranslationTemplates = new(TranslationTable);
    private static string _selectedLanguage = AutoLanguage;
    private static string _currentLanguage = DefaultLanguage;
    private static string _settingsFilePath = GetDefaultSettingsFilePath();

    static LocalizationManager()
    {
        LoadPreferenceAndResolveLanguage(persistDefaultPreference: true);
    }

    private LocalizationManager()
    {
    }

    public static LocalizationManager Instance { get; } = new();

    /// <summary>The language currently used to resolve UI text.</summary>
    public static string CurrentLanguage
    {
        get
        {
            lock (Gate)
                return _currentLanguage;
        }
    }

    /// <summary>The saved preference: auto, es, en, fr, pt, or de.</summary>
    public string SelectedLanguage
    {
        get
        {
            lock (Gate)
                return _selectedLanguage;
        }
        set => SetLanguage(value);
    }

    /// <summary>Options suitable for a language selector, including automatic detection.</summary>
    public IReadOnlyList<LanguageOption> LanguageOptions => ReadOnlyLanguageOptions;

    /// <summary>The supported resolved language codes, excluding the automatic option.</summary>
    public static IReadOnlyList<string> SupportedLanguages => ReadOnlyLanguageCodes;

    /// <summary>Culture associated with the active language for WPF date and number controls.</summary>
    public CultureInfo ActiveCulture => GetCulture(CurrentLanguage);

    /// <summary>Culture information consumed by WPF's FrameworkElement.Language property.</summary>
    public XmlLanguage WindowLanguage => XmlLanguage.GetLanguage(ActiveCulture.IetfLanguageTag);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the selected or resolved application language changes.</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>Gets a translated string, falling back to Spanish and then the key.</summary>
    public static string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
            return string.Empty;

        lock (Gate)
        {
            if (TranslationTable.TryGetValue(_currentLanguage, out Dictionary<string, string>? current) &&
                current.TryGetValue(key, out string? translated))
            {
                return translated;
            }

            if (TranslationTable.TryGetValue(DefaultLanguage, out Dictionary<string, string>? spanish) &&
                spanish.TryGetValue(key, out string? fallback))
            {
                return fallback;
            }
        }

        return key;
    }

    /// <summary>Gets a translated string and formats it using the active language's culture.</summary>
    public static string T(string key, params object?[] arguments)
    {
        string format = Get(key);
        return arguments is { Length: > 0 }
            ? string.Format(GetCulture(CurrentLanguage), format, arguments)
            : format;
    }

    /// <summary>Re-renders an existing localized string after the active language changes.</summary>
    public static string TranslateExistingText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        if (TranslationTemplates.TryMatch(text, out string key, out object?[] arguments))
            return arguments.Length == 0 ? Get(key) : T(key, arguments);

        return text;
    }

    private sealed class TranslationTemplateIndex
    {
        private readonly Dictionary<string, TranslationTemplate> _exactTemplates = new(StringComparer.Ordinal);
        private readonly List<TranslationTemplate> _emptyPrefixTemplates = new();
        private readonly PrefixNode _root = new();

        public TranslationTemplateIndex(Dictionary<string, Dictionary<string, string>> translations)
        {
            int order = 0;
            foreach (Dictionary<string, string> entries in translations.Values)
            {
                foreach ((string key, string template) in entries)
                {
                    TranslationTemplate matcher = new(key, template, order++);
                    if (matcher.IsExact)
                        _exactTemplates.TryAdd(template, matcher);
                    else if (matcher.Prefix.Length == 0)
                        _emptyPrefixTemplates.Add(matcher);
                    else
                        AddToPrefixTree(matcher);
                }
            }
        }

        public bool TryMatch(string text, out string key, out object?[] arguments)
        {
            TranslationTemplate? exactMatch = _exactTemplates.GetValueOrDefault(text);

            List<TranslationTemplate> candidates = new(_emptyPrefixTemplates);
            PrefixNode node = _root;
            foreach (char character in text)
            {
                if (!node.Children.TryGetValue(character, out PrefixNode? child))
                    break;

                node = child;
                if (node.Templates is not null)
                    candidates.AddRange(node.Templates);
            }

            candidates.Sort(static (left, right) => left.Order.CompareTo(right.Order));
            if (exactMatch is null)
            {
                foreach (TranslationTemplate candidate in candidates)
                {
                    if (candidate.TryMatch(text, out arguments))
                    {
                        key = candidate.Key;
                        return true;
                    }
                }

                key = string.Empty;
                arguments = Array.Empty<object?>();
                return false;
            }

            foreach (TranslationTemplate candidate in candidates)
            {
                // An exact template later in the original catalog order wins over
                // any dynamic template after it, just as the previous linear scan did.
                if (candidate.Order >= exactMatch.Order)
                    break;

                if (candidate.TryMatch(text, out arguments))
                {
                    key = candidate.Key;
                    return true;
                }
            }

            key = exactMatch.Key;
            arguments = Array.Empty<object?>();
            return true;
        }

        private void AddToPrefixTree(TranslationTemplate template)
        {
            PrefixNode node = _root;
            foreach (char character in template.Prefix)
            {
                if (!node.Children.TryGetValue(character, out PrefixNode? child))
                {
                    child = new PrefixNode();
                    node.Children.Add(character, child);
                }
                node = child;
            }

            (node.Templates ??= new List<TranslationTemplate>()).Add(template);
        }

        private sealed class PrefixNode
        {
            public Dictionary<char, PrefixNode> Children { get; } = new();
            public List<TranslationTemplate>? Templates { get; set; }
        }
    }

    private sealed class TranslationTemplate
    {
        private readonly int _argumentCount;
        private readonly Lazy<Regex>? _matcher;

        public TranslationTemplate(string key, string template, int order)
        {
            Key = key;
            Order = order;

            MatchCollection placeholders = PlaceholderPattern.Matches(template);
            IsExact = placeholders.Count == 0;
            if (IsExact)
            {
                Prefix = string.Empty;
                return;
            }

            Prefix = template[..placeholders[0].Index];
            var pattern = new StringBuilder("^");
            int previousIndex = 0;
            int argumentCount = 0;
            foreach (Match placeholder in placeholders)
            {
                pattern.Append(Regex.Escape(template[previousIndex..placeholder.Index]));
                int index = int.Parse(placeholder.Groups["index"].Value, CultureInfo.InvariantCulture);
                argumentCount = Math.Max(argumentCount, index + 1);
                pattern.Append("(?<arg").Append(index).Append(">.*?)");
                previousIndex = placeholder.Index + placeholder.Length;
            }
            pattern.Append(Regex.Escape(template[previousIndex..])).Append("$");

            _argumentCount = argumentCount;
            string regexPattern = pattern.ToString();
            _matcher = new Lazy<Regex>(
                () => new Regex(regexPattern, RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)),
                isThreadSafe: true);
        }

        public string Key { get; }
        public int Order { get; }
        public bool IsExact { get; }
        public string Prefix { get; }

        public bool TryMatch(string text, out object?[] arguments)
        {
            if (IsExact)
            {
                arguments = Array.Empty<object?>();
                return string.Equals(Prefix, text, StringComparison.Ordinal);
            }

            Match match;
            try
            {
                match = _matcher!.Value.Match(text);
            }
            catch (RegexMatchTimeoutException)
            {
                arguments = Array.Empty<object?>();
                return false;
            }
            if (!match.Success)
            {
                arguments = Array.Empty<object?>();
                return false;
            }

            arguments = new object?[_argumentCount];
            for (int index = 0; index < _argumentCount; index++)
                arguments[index] = match.Groups["arg" + index].Value;

            return true;
        }
    }

    /// <summary>
    /// Initializes from the current user's saved preference. Call before creating
    /// windows so XAML bindings start with the resolved language.
    /// </summary>
    public static void Initialize()
    {
        LoadPreferenceAndResolveLanguage(persistDefaultPreference: true);
    }

    /// <summary>
    /// Selects and persists a supported language. Passing "auto" keeps the automatic
    /// preference saved as "auto" while resolving the current Windows UI language.
    /// </summary>
    public static bool SetLanguage(string? languageCode)
    {
        string? normalized = NormalizePreference(languageCode);
        if (normalized is null)
            return false;

        bool changed;
        lock (Gate)
        {
            string previousSelection = _selectedLanguage;
            string previousLanguage = _currentLanguage;
            _selectedLanguage = normalized;
            _currentLanguage = normalized == AutoLanguage ? DetectWindowsLanguage() : normalized;
            changed = previousSelection != _selectedLanguage || previousLanguage != _currentLanguage;
            PersistPreference();
        }

        if (changed)
            NotifyLanguageChanged();

        return true;
    }

    /// <summary>Refreshes automatic detection after Windows' preferred UI language changes.</summary>
    public static void RefreshSystemLanguage()
    {
        bool changed;
        lock (Gate)
        {
            if (_selectedLanguage != AutoLanguage)
                return;

            string resolved = DetectWindowsLanguage();
            changed = resolved != _currentLanguage;
            _currentLanguage = resolved;
        }

        if (changed)
            NotifyLanguageChanged();
    }

    private static void LoadPreferenceAndResolveLanguage(bool persistDefaultPreference)
    {
        bool preferenceNeedsNormalization = false;
        string selected = AutoLanguage;

        lock (Gate)
        {
            _settingsFilePath = GetDefaultSettingsFilePath();
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_settingsFilePath));
                    if (document.RootElement.TryGetProperty("language", out JsonElement languageElement) &&
                        languageElement.ValueKind == JsonValueKind.String)
                    {
                        string? normalized = NormalizePreference(languageElement.GetString());
                        selected = normalized ?? AutoLanguage;
                        preferenceNeedsNormalization = normalized is null ||
                            !string.Equals(languageElement.GetString(), normalized, StringComparison.Ordinal);
                    }
                    else
                        preferenceNeedsNormalization = true;
                }
                else
                {
                    preferenceNeedsNormalization = true;
                }
            }
            catch
            {
                preferenceNeedsNormalization = true;
            }

            _selectedLanguage = selected;
            _currentLanguage = selected == AutoLanguage ? DetectWindowsLanguage() : selected;

            if (persistDefaultPreference && preferenceNeedsNormalization)
                PersistPreference();
        }
    }

    private static void PersistPreference()
    {
        string? temporaryFile = null;
        try
        {
            string? directory = Path.GetDirectoryName(_settingsFilePath);
            if (string.IsNullOrEmpty(directory))
                return;

            Directory.CreateDirectory(directory);
            temporaryFile = _settingsFilePath + ".tmp";
            string json = JsonSerializer.Serialize(
                new LanguagePreference(_selectedLanguage),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            File.WriteAllText(temporaryFile, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryFile, _settingsFilePath, overwrite: true);
        }
        catch
        {
            if (temporaryFile is not null)
            {
                try { File.Delete(temporaryFile); } catch { }
            }
            // Language switching should still work for the current session if settings
            // storage is unavailable; the preference will be retried next launch.
        }
    }

    private static string DetectWindowsLanguage()
    {
        // CurrentUICulture reflects the Windows user's preferred display language.
        // InstalledUICulture is a fallback for processes whose UI culture is invariant.
        string? detected = ResolveSupportedCulture(CultureInfo.CurrentUICulture);
        detected ??= ResolveSupportedCulture(CultureInfo.InstalledUICulture);
        return detected ?? DefaultLanguage;
    }

    private static string? ResolveSupportedCulture(CultureInfo culture)
    {
        string code = culture.TwoLetterISOLanguageName;
        return LanguageCodes.FirstOrDefault(candidate => string.Equals(candidate, code, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizePreference(string? languageCode)
    {
        if (string.Equals(languageCode?.Trim(), AutoLanguage, StringComparison.OrdinalIgnoreCase))
            return AutoLanguage;

        return LanguageCodes.FirstOrDefault(candidate =>
            string.Equals(candidate, languageCode?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static CultureInfo GetCulture(string languageCode) => languageCode switch
    {
        "es" => CultureInfo.GetCultureInfo("es-AR"),
        "en" => CultureInfo.GetCultureInfo("en-US"),
        "fr" => CultureInfo.GetCultureInfo("fr-FR"),
        "pt" => CultureInfo.GetCultureInfo("pt-BR"),
        "de" => CultureInfo.GetCultureInfo("de-DE"),
        _ => CultureInfo.GetCultureInfo("es-AR")
    };

    private static string GetDefaultSettingsFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NextDNSManager",
        SettingsFileName);

    private static Dictionary<string, Dictionary<string, string>> LoadTranslations()
    {
        var translations = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            Assembly assembly = typeof(LocalizationManager).Assembly;
            string[] catalogNames = assembly.GetManifestResourceNames()
                .Where(name => name.Contains(".Resources.Localization.", StringComparison.OrdinalIgnoreCase) &&
                               name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (string resourceName in catalogNames)
            {
                try
                {
                    using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream is null)
                        continue;

                    using JsonDocument document = JsonDocument.Parse(stream);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        continue;

                    foreach (JsonProperty language in document.RootElement.EnumerateObject())
                    {
                        string? code = NormalizePreference(language.Name);
                        if (code is null || code == AutoLanguage || language.Value.ValueKind != JsonValueKind.Object)
                            continue;

                        if (!translations.TryGetValue(code, out Dictionary<string, string>? strings))
                        {
                            strings = new Dictionary<string, string>(StringComparer.Ordinal);
                            translations.Add(code, strings);
                        }

                        foreach (JsonProperty entry in language.Value.EnumerateObject())
                        {
                            if (entry.Value.ValueKind == JsonValueKind.String)
                                strings.TryAdd(entry.Name, entry.Value.GetString() ?? string.Empty);
                        }
                    }
                }
                catch
                {
                    // Continue loading other independent resource catalogs.
                }
            }
        }
        catch
        {
            // A malformed optional catalog must not prevent the manager from starting.
        }

        return translations;
    }

    private static void NotifyLanguageChanged()
    {
        void Notify()
        {
            foreach (LanguageOption option in ReadOnlyLanguageOptions)
                option.RefreshDisplayName();
            Instance.OnPropertyChanged(nameof(SelectedLanguage));
            Instance.OnPropertyChanged(nameof(ActiveCulture));
            Instance.OnPropertyChanged(nameof(WindowLanguage));
            Instance.OnPropertyChanged("Item[]");
            LanguageChanged?.Invoke(Instance, EventArgs.Empty);
        }

        Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Notify();
        else
            _ = dispatcher.BeginInvoke((Action)Notify, DispatcherPriority.DataBind);
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public string this[string key] => Get(key);

    private sealed record LanguagePreference(string Language);
}

public sealed class LanguageOption : INotifyPropertyChanged
{
    private readonly string _displayName;
    public LanguageOption(string code, string displayName)
    {
        Code = code;
        _displayName = displayName;
    }

    public string Code { get; }
    public string DisplayName => Code == "auto" ? LocalizationManager.Get("Main.Ui.Language.Auto") : _displayName;

    public event PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshDisplayName() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));

    public override string ToString() => DisplayName;
}
