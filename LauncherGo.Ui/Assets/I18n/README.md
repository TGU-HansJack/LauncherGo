# LauncherGo UI translations

UI strings live in the RESX files in this directory:

- `Resources.resx`: invariant/English fallback
- `Resources.en-us.resx`: English (United States)
- `Resources.zh-hans.resx`: Simplified Chinese

Use stable PascalCase keys grouped by feature (`Common*`, `Config*`,
`Instance*`, and so on). Keep format placeholders (`{0}`, `{1:0.##}`) in
every language file. A missing translation falls back to the invariant
English resource and finally to the key, so a partial translation never
produces an empty control.

The application accepts `en-US` and `zh-CN` for the launcher language. The
localization service maps `zh-CN` to the `zh-Hans` resource assembly. Adding a
new language only requires adding another `Resources.<culture>.resx` file and
including the culture in the appearance language list.

Language changes are committed by `ILocalizationService.SetLanguage` and are
published through `LanguageChanged` after the culture and resource lookup are
ready. Views coalesce that notification at render priority, so changing the
selector repeatedly does not render an intermediate or blank language.

New UI code should use a resource key (`localization["CommonSaveButtonText"]`)
instead of embedding two strings. `Resolve` exists as a compatibility bridge
for older views and automatically uses a matching resource key when one is
already present.
