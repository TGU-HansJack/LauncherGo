# LauncherGo Release Notes

Release notes are maintained as two Markdown files for every published version:

```text
docs/releases/v<version>.zh-CN.md
docs/releases/v<version>.en-US.md
```

The `publish-release.yml` workflow validates both files, combines them into one bilingual GitHub Release body, and appends a comparison link to the previous release. Keep the files user-facing and concise. Use the same section order in both languages:

1. `新增 / Added`
2. `修复 / Fixed`
3. `优化 / Improved`
4. `注意事项 / Notes`

Preview releases use the exact version passed to the workflow, such as `v2.5.5-preview`; add matching files when publishing a preview.
