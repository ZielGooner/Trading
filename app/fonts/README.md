# Pretendard

AUREX embeds the unmodified Regular (400) and Bold (700) static TrueType
fonts from the official Pretendard v1.3.9 release. Regular is used for body
text, form fields, tables and chart labels; Bold is used for headings,
navigation, buttons and highlighted values.

- Author: Kil Hyung-jin.
- Release: https://github.com/orioncactus/pretendard/releases/tag/v1.3.9
- Archive: https://github.com/orioncactus/pretendard/releases/download/v1.3.9/Pretendard-1.3.9.zip
- Source paths: `public/static/alternative/Pretendard-Regular.ttf` and
  `public/static/alternative/Pretendard-Bold.ttf`.
- License: SIL Open Font License 1.1; the original copyright notice and full
  license are in [OFL.txt](OFL.txt).

SHA-256 of the unchanged font files:

```text
Pretendard-Regular.ttf  6D0AF5258997AEC7354A6E340FC2325BA321C410CA48B3AF858C8C3D6E92A324
Pretendard-Bold.ttf     C16B88C670D23E83FA1170C954CBC4822D3B8DAD3C3CDE15D798A94B43D97985
```

Fonts are loaded once into this process, for both GDI and GDI+ rendering.
They require no system font installation or runtime download. The build
also embeds `OFL.txt` and copies it to `Trading.font-license.txt` beside
the executable. Distribute that license file with the executable.
