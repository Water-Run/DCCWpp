# DCCW++ Windows 11 icon revision

Created with the built-in ImageGen tool. The user supplied the Windows 11 This PC icon as the style reference. The generated concept is `imagegen-concept-v2.png`; the editable vector interpretation is `DCCWPlus.svg`, with the rendered preview in `logo-preview.png` and package assets under `src/DccwPlus.App/Assets/`.

Prompt:

Create the redesigned DCCW++ Windows 11 display calibration application icon. Follow the reference's front-facing flat thin monitor, small corner radius, restrained dark gray outline, short silver stem and horizontal foot. Use precise vector-like geometry and a soft cyan-to-blue gradient screen. Put one modest RGB calibration cube with three clean gradient faces inside the screen. Make the silhouette legible at 32 pixels. Use a transparent background, no text, no document overlay, no chrome, no photographic reflections, and no Windows 7 Aero glass.

The SVG is independently drawn geometry without an embedded raster image. Regenerate PNG/ICO assets with `python scripts/render_assets.py`; this uses resvg when available, otherwise CairoSVG. Pillow is also required for packaging image canvases and ICO output.
