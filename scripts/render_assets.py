"""Render the editable SVG into Windows package assets. No generated bitmap is traced or embedded."""
from io import BytesIO
from pathlib import Path
import cairosvg
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "design/branding/DCCWPlus.svg"
DEST = ROOT / "src/DccwPlus.App/Assets"
DEST.mkdir(parents=True, exist_ok=True)

def render(width, height, fill=0.88):
    size = round(min(width, height) * fill)
    icon = Image.open(BytesIO(cairosvg.svg2png(url=str(SOURCE), output_width=size, output_height=size))).convert("RGBA")
    canvas = Image.new("RGBA", (width, height))
    canvas.alpha_composite(icon, ((width-size)//2, (height-size)//2))
    return canvas

for name, width, height in [("Square44x44Logo", 44, 44), ("Square150x150Logo", 150, 150),
                            ("Wide310x150Logo", 310, 150), ("SplashScreen", 620, 300), ("StoreLogo", 50, 50)]:
    for scale in (1, 2, 4):
        suffix = "" if scale == 1 else f".scale-{scale*100}"
        render(width*scale, height*scale).save(DEST / f"{name}{suffix}.png")
for size in (16, 24, 32, 48, 256):
    render(size, size, 1).save(DEST / f"Square44x44Logo.targetsize-{size}_altform-unplated.png")
render(256, 256, 1).save(DEST / "App.ico", sizes=[(16,16), (24,24), (32,32), (48,48), (64,64), (128,128), (256,256)])
render(1024, 1024, 1).save(ROOT / "design/branding/logo-preview.png")
print(f"Rendered Windows icon assets to {DEST}")
