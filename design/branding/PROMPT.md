# DCCW++ icon

Generated with the built-in ImageGen tool using the user-supplied dccw screenshot as a visual reference. The bitmap concept is preserved in `imagegen-concept.png`. `DCCWPlus.svg` is a separately authored, editable vector interpretation with simplified geometry for small Windows icons. It contains no embedded raster image.

## Prompt

Use case: logo-brand
Asset type: production app icon concept for DCCW++, a Windows 11 display color calibration app.
Input image: reference only, the screenshot's legacy dccw icon (monitor and colorful calibration cube). Ignore all surrounding screenshot UI and text.
Primary request: redesign that visual idea as a modern Windows 11 Fluent app icon. A compact softly rounded silver/pearl monitor with a deep blue display, a vivid RGB spectrum cube on the screen, and a small translucent frosted calibration card overlapping its lower-left corner with a smaller spectrum cube. Preserve the recognizable monitor + color cube + overlapping calibration card composition, but use clean contemporary geometry, softly beveled forms, restrained translucent materials and gentle studio highlights. Front three-quarter view, simple silhouette legible at small sizes, centered with generous clear margin.
Background: genuinely transparent alpha.
Text: no text or lettering; do not draw the application name.
Avoid: Windows logo, screenshot UI, drop-shadow background planes, excessive detail, photoreal desktop scene, watermarks, checkerboard baked into pixels.
One isolated icon, square canvas. This is the visual reference for a later editable SVG implementation.

## Assets

- Editable source: `DCCWPlus.svg`.
- Preview: `logo-preview.png`.
- Application/package assets: `src/DccwPlus.App/Assets/`.
- Regenerate package PNG/ICO files: `python scripts/render_assets.py` (Pillow and CairoSVG required only for asset authoring).
- Transparent backgrounds are preserved. The original screenshot itself is not bundled with the app.
