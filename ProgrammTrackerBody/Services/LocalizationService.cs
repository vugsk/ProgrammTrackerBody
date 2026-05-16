using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

namespace ProgrammTrackerBody.Services;

public sealed class LocalizationService : INotifyPropertyChanged
{
    public const string LanguageRussian = "ru";
    public const string LanguageEnglish = "en";

    private static readonly Lazy<LocalizationService> InstanceHolder = new(() => new LocalizationService());

    private string _currentLanguage = LanguageRussian;

    private LocalizationService()
    {
    }

    public static LocalizationService Instance => InstanceHolder.Value;

    public string CurrentLanguage
    {
        get => _currentLanguage;
        private set
        {
            if (_currentLanguage == value)
            {
                return;
            }

            _currentLanguage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetLanguage(string languageCode)
    {
        var resolved = languageCode == LanguageEnglish ? LanguageEnglish : LanguageRussian;
        var dictUri = new Uri($"pack://application:,,,/Resources/Strings.{resolved}.xaml", UriKind.Absolute);

        var newDict = new ResourceDictionary { Source = dictUri };
        var appResources = Application.Current?.Resources;
        if (appResources is null)
        {
            CurrentLanguage = resolved;
            return;
        }

        var existingStrings = appResources.MergedDictionaries
            .FirstOrDefault(d => d.Source != null && d.Source.OriginalString.Contains("/Resources/Strings."));

        if (existingStrings is not null)
        {
            appResources.MergedDictionaries.Remove(existingStrings);
        }

        appResources.MergedDictionaries.Add(newDict);
        CurrentLanguage = resolved;

        var culture = new CultureInfo(resolved);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public string Get(string key)
    {
        var resource = Application.Current?.TryFindResource(key);
        return resource as string ?? key;
    }

    public string this[string key] => Get(key);
}
