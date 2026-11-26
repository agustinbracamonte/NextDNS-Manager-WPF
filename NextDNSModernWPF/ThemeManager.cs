using Microsoft.Win32;
using System;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace NextDNSModernWPF
{
    public enum ThemePreference
    {
        System,
        Light,
        Dark
    }

    public enum ThemeVariant
    {
        Light,
        Dark
    }

    /// <summary>
    /// Loads the app theme palette, persists the user's preference, and follows
    /// the Windows app theme when the preference is System.
    /// </summary>
    public static class ThemeManager
    {
        private const string SettingsPath = @"Software\NextDNSManager";
        private const string PreferenceValueName = "ThemePreference";
        private const string WindowsPersonalizationPath =
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private const string LightDictionaryUri = "/NextDNSModernWPF;component/Themes/Light.xaml";
        private const string DarkDictionaryUri = "/NextDNSModernWPF;component/Themes/Dark.xaml";

        private static Application? _application;
        private static ResourceDictionary? _activeDictionary;
        private static bool _listeningForSystemChanges;

        public static ThemePreference Preference { get; private set; } = ThemePreference.System;
        public static ThemeVariant EffectiveTheme { get; private set; } = ThemeVariant.Dark;

        /// <summary>Raised when the preference or effective palette changes.</summary>
        public static event EventHandler? ThemeChanged;

        /// <summary>
        /// Loads the saved preference and makes the palette available to windows.
        /// Call before creating the main window.
        /// </summary>
        public static void Initialize(Application application)
        {
            ArgumentNullException.ThrowIfNull(application);

            _application = application;
            Preference = ReadPreference();
            ApplyEffectiveTheme();

            if (!_listeningForSystemChanges)
            {
                SystemEvents.UserPreferenceChanged += OnWindowsPreferenceChanged;
                _listeningForSystemChanges = true;
            }
        }

        /// <summary>Changes the preference and stores it for the current user.</summary>
        public static void SetPreference(ThemePreference preference)
        {
            if (!Enum.IsDefined(preference))
                throw new ArgumentOutOfRangeException(nameof(preference));

            Application application = _application ??
                throw new InvalidOperationException("ThemeManager.Initialize must be called before setting the theme.");

            if (!application.Dispatcher.CheckAccess())
            {
                application.Dispatcher.Invoke(() => SetPreference(preference));
                return;
            }

            ThemePreference previousPreference = Preference;
            ThemeVariant previousTheme = EffectiveTheme;
            Preference = preference;
            SavePreference(preference);
            ApplyEffectiveTheme();

            if (previousPreference != Preference || previousTheme != EffectiveTheme)
                ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        private static void OnWindowsPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (Preference != ThemePreference.System)
                return;

            Application? application = _application;
            if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
                return;

            if (application.Dispatcher.CheckAccess())
                RefreshFromWindows();
            else
            {
                try { _ = application.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(RefreshFromWindows)); }
                catch (InvalidOperationException) { }
            }
        }

        private static void RefreshFromWindows()
        {
            if (Preference != ThemePreference.System)
                return;

            ThemeVariant previousTheme = EffectiveTheme;
            ApplyEffectiveTheme();
            if (previousTheme != EffectiveTheme)
                ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        private static void ApplyEffectiveTheme()
        {
            Application? application = _application;
            if (application == null)
                return;

            ThemeVariant target = Preference switch
            {
                ThemePreference.Light => ThemeVariant.Light,
                ThemePreference.Dark => ThemeVariant.Dark,
                _ => WindowsUsesLightTheme() ? ThemeVariant.Light : ThemeVariant.Dark
            };

            if (_activeDictionary != null && EffectiveTheme == target)
                return;

            string dictionaryUri = target == ThemeVariant.Light ? LightDictionaryUri : DarkDictionaryUri;
            var newDictionary = new ResourceDictionary
            {
                Source = new Uri(dictionaryUri, UriKind.Relative)
            };

            if (_activeDictionary != null)
                application.Resources.MergedDictionaries.Remove(_activeDictionary);

            application.Resources.MergedDictionaries.Add(newDictionary);
            _activeDictionary = newDictionary;
            EffectiveTheme = target;
        }

        private static ThemePreference ReadPreference()
        {
            try
            {
                object? value = Registry.CurrentUser.OpenSubKey(SettingsPath, writable: false)?
                    .GetValue(PreferenceValueName);
                if (value is string raw && Enum.TryParse(raw, ignoreCase: true, out ThemePreference preference) && Enum.IsDefined(preference))
                    return preference;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            {
                // Fall back to automatic mode if a per-user setting cannot be read.
            }

            return ThemePreference.System;
        }

        private static void SavePreference(ThemePreference preference)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsPath, writable: true);
                key.SetValue(PreferenceValueName, preference.ToString(), RegistryValueKind.String);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            {
                // Keep the in-memory preference active if persistence is unavailable.
            }
        }

        private static bool WindowsUsesLightTheme()
        {
            try
            {
                object? value = Registry.GetValue(WindowsPersonalizationPath, "AppsUseLightTheme", 0);
                return Convert.ToInt32(value) != 0;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException or FormatException or InvalidCastException or OverflowException)
            {
                // Preserve the current dark appearance when Windows has no readable setting.
                return false;
            }
        }
    }
}
