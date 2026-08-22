using System.Globalization;
using LauncherGo.Abstractions.Services.I18n;
using LauncherGo.Ui.Services.I18n;
using Xunit;

namespace LauncherGo.Tests;

[Collection("Localization")]
public sealed class LocalizationServiceTests
{
    [Fact]
    public void UsesCultureSpecificSatelliteResource()
    {
        using var cultureScope = new CultureScope();
        var service = new LocalizationService();

        Assert.True(service.SetLanguage("zh-CN"));
        Assert.Equal("语言", service["LanguageButtonText"]);
        Assert.Equal("语言", service.Resolve("语言", "Language"));

        Assert.True(service.SetLanguage("en-US"));
        Assert.Equal("Language", service["LanguageButtonText"]);
        Assert.Equal("Language", service.Resolve("语言", "Language"));
    }

    [Fact]
    public void PublishesOneCommittedChangeAndRejectsInvalidCulture()
    {
        using var cultureScope = new CultureScope();
        var service = new LocalizationService();
        service.SetLanguage("en-US");
        var changes = new List<string>();
        service.LanguageChanged += (_, args) => changes.Add(args.NewCulture.Name);

        Assert.True(service.SetLanguage("zh-CN"));
        Assert.False(service.SetLanguage("not-a-culture"));
        Assert.True(service.SetLanguage("zh-CN"));

        Assert.Single(changes);
        Assert.Equal("zh-CN", changes[0]);
        Assert.Equal("zh-CN", service.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("ru-RU", "Язык")]
    [InlineData("de-DE", "Sprache")]
    [InlineData("fr-FR", "Langue")]
    [InlineData("es-ES", "Idioma")]
    [InlineData("pl-PL", "Język")]
    [InlineData("pt-BR", "Idioma")]
    public void SupportsAdditionalLauncherLanguages(string languageCode, string expectedLanguageLabel)
    {
        using var cultureScope = new CultureScope();
        var service = new LocalizationService();

        Assert.True(service.SetLanguage(languageCode));
        Assert.Equal(languageCode, service.CurrentCulture.Name);
        Assert.Equal(expectedLanguageLabel, service.Resolve("语言", "Language"));
        Assert.Equal(expectedLanguageLabel, service["LanguageButtonText"]);
    }

    [Fact]
    public void UsesTranslatedResourceForAdditionalCulture()
    {
        using var cultureScope = new CultureScope();
        var service = new LocalizationService();

        Assert.True(service.SetLanguage("fr-FR"));
        Assert.Equal("Informations sur le projet", service["AboutProjectInfoTitle"]);
    }

    [Fact]
    public void UsesNativeNamesInLanguageSelector()
    {
        Assert.Equal(
            ["中文（简体）", "English", "Русский", "Deutsch", "Français", "Español", "Polski", "Português (Brasil)"],
            SupportedLanguages.All.Select(language => language.NativeName));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _current = CultureInfo.CurrentCulture;
        private readonly CultureInfo _currentUi = CultureInfo.CurrentUICulture;
        private readonly CultureInfo _default = CultureInfo.DefaultThreadCurrentCulture ?? CultureInfo.InvariantCulture;
        private readonly CultureInfo _defaultUi = CultureInfo.DefaultThreadCurrentUICulture ?? CultureInfo.InvariantCulture;

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _current;
            CultureInfo.CurrentUICulture = _currentUi;
            CultureInfo.DefaultThreadCurrentCulture = _default;
            CultureInfo.DefaultThreadCurrentUICulture = _defaultUi;
        }
    }
}

[CollectionDefinition("Localization", DisableParallelization = true)]
public sealed class LocalizationCollectionDefinition
{
}
