from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "KeyPilot.App" / "Assets"
MASTER_SIZE = 1024


def build_master() -> Image.Image:
    image = Image.new("RGBA", (MASTER_SIZE, MASTER_SIZE), (0, 0, 0, 0))

    tile_mask = Image.new("L", image.size, 0)
    mask_draw = ImageDraw.Draw(tile_mask)
    mask_draw.rounded_rectangle((64, 64, 960, 960), radius=216, fill=255)

    gradient = Image.new("RGBA", image.size)
    pixels = gradient.load()
    top = (18, 35, 31)
    bottom = (5, 11, 9)
    for y in range(MASTER_SIZE):
        ratio = y / (MASTER_SIZE - 1)
        row = tuple(round(top[channel] * (1 - ratio) + bottom[channel] * ratio) for channel in range(3))
        for x in range(MASTER_SIZE):
            diagonal = min(1.0, (x + y) / (MASTER_SIZE * 1.45))
            shade = tuple(max(0, round(value * (1 - diagonal * 0.13))) for value in row)
            pixels[x, y] = (*shade, 255)
    image.paste(gradient, (0, 0), tile_mask)

    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle(
        (64, 64, 960, 960),
        radius=216,
        outline=(98, 227, 197, 255),
        width=32,
    )

    white = (244, 251, 249, 255)
    mint = (98, 227, 197, 255)
    draw.line((294, 282, 294, 742), fill=white, width=82)
    draw.line((310, 520, 520, 288), fill=white, width=82)
    draw.line((310, 520, 530, 746), fill=white, width=82)

    draw.line((586, 742, 586, 282), fill=mint, width=76)
    draw.line((586, 282, 712, 282), fill=mint, width=76)
    draw.arc((598, 282, 862, 542), start=270, end=90, fill=mint, width=76)
    draw.line((728, 542, 590, 542), fill=mint, width=76)

    draw.ellipse((804, 186, 860, 242), fill=(145, 166, 255, 255))
    return image


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    master = build_master()
    preview = master.resize((512, 512), Image.Resampling.LANCZOS)
    preview.save(ASSETS / "KeyPilot.Icon.png", optimize=True)
    master.save(
        ASSETS / "KeyPilot.ico",
        format="ICO",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
    )


if __name__ == "__main__":
    main()
