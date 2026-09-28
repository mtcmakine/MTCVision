"""
Synthetic training lines that look like what the camera photographs.

There is no labelled corpus of laser-marked Turkish parts, and collecting one would take longer
than the line has. Rendering is the alternative: unlimited labelled data, and — more usefully —
control over exactly the degradations that break recognition here. The augmentation ranges are not
guesses; they were measured off the live camera, where a panel crop reads roughly 110 mean
brightness with a standard deviation near 21 and visible sensor noise at the gain a dim scene needs.
"""

import random
import string

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

from charset import CHARSET, CONFUSABLE_PAIRS

TARGET_HEIGHT = 48

# Fonts that actually carry the Turkish letters. A font missing them renders a blank box and would
# teach the model that the mark is invisible.
CANDIDATE_FONTS = [
    "C:/Windows/Fonts/arial.ttf",
    "C:/Windows/Fonts/arialbd.ttf",
    "C:/Windows/Fonts/calibri.ttf",
    "C:/Windows/Fonts/calibrib.ttf",
    "C:/Windows/Fonts/segoeui.ttf",
    "C:/Windows/Fonts/segoeuib.ttf",
    "C:/Windows/Fonts/tahoma.ttf",
    "C:/Windows/Fonts/tahomabd.ttf",
    "C:/Windows/Fonts/verdana.ttf",
    "C:/Windows/Fonts/verdanab.ttf",
    "C:/Windows/Fonts/times.ttf",
    "C:/Windows/Fonts/timesbd.ttf",
    "C:/Windows/Fonts/consola.ttf",
    "C:/Windows/Fonts/consolab.ttf",
    "C:/Windows/Fonts/trebuc.ttf",
    "C:/Windows/Fonts/georgia.ttf",
]

# Words off the machine in front of the camera, plus everyday Turkish carrying the letters the
# stock model drops. The point is not vocabulary — it is that the hard letters appear constantly.
PANEL_WORDS = [
    "Sıkma", "Boşaltma", "Durulama", "Yıkama", "Yorgan", "Perde", "Mix", "Narin", "İpek",
    "Yünlüler", "Elde", "Ek", "programlar", "Sıcaklık", "Devir", "Ön", "Yıkama", "Hızlı",
    "Pamuklu", "Sentetik", "Bebek", "Gömlek", "Kirli", "Çamaşır", "Kurutma", "Ayarlar",
    "Başlat", "Duraklat", "İptal", "Kilit", "Açık", "Kapalı", "Süre", "Bitiş", "Gecikme",
    "Ağır", "Günlük", "Karışık", "Şaşırtma", "Ilık", "Soğuk", "Sıcak", "Işık", "Güç",
    "Değişim", "Öğütücü", "Çöğür", "Işıklı", "Sağlık", "Çevrim", "Şifre", "Ütü", "Sızdırmaz",
]

TURKISH_WORDS = [
    "İstanbul", "Ankara", "İzmir", "Şanlıurfa", "Çanakkale", "Muğla", "Iğdır", "Bingöl",
    "üretim", "kalite", "kontrol", "makine", "otomasyon", "sıcaklık", "basınç", "ölçüm",
    "değer", "sınır", "ağırlık", "genişlik", "yükseklik", "çıkış", "giriş", "açıklama",
    "şarj", "güvenlik", "bağlantı", "işlem", "birim", "toplam", "günlük", "yıllık",
]


def _random_code(rng: random.Random) -> str:
    """Part and date codes: the other thing that gets laser-marked."""
    letters = "".join(rng.choice(string.ascii_uppercase) for _ in range(rng.randint(2, 4)))
    digits = "".join(rng.choice(string.digits) for _ in range(rng.randint(2, 6)))
    separator = rng.choice(["-", "/", ".", " ", ""])
    return f"{letters}{separator}{digits}"


def _confusable_drill(rng: random.Random) -> str:
    """
    Short strings built from the letters the stock model gets wrong, mixed with their plain
    counterparts so the model has to tell them apart rather than favour one.
    """
    pieces = []
    for _ in range(rng.randint(2, 5)):
        pair = rng.choice(CONFUSABLE_PAIRS)
        pieces.append(rng.choice(pair))
        pieces.append(rng.choice(string.ascii_lowercase))
    return "".join(pieces)


TALL = "lıitİIfjtlk"
VOWELS = "aeıioöuü"


def _stroke_drill(rng: random.Random) -> str:
    """
    Runs of narrow upright letters — lt, ll, tl, lı, it — placed next to each other.

    These are where the reading still slipped after the first training round: "Boşaltma" came back
    as "Boşatma" at some crops and correctly at others, which is a pair the model has seen too
    rarely rather than one the picture cannot settle. Drilling them directly is cheaper than
    another full round of general text.
    """
    pieces = []
    for _ in range(rng.randint(2, 4)):
        pieces.append(rng.choice(VOWELS))
        pieces.append("".join(rng.choice(TALL) for _ in range(rng.randint(2, 3))))
    return "".join(pieces)


def random_text(rng: random.Random) -> str:
    """A line of text, weighted towards what this application actually reads."""
    kind = rng.random()
    if kind < 0.34:
        words = [rng.choice(PANEL_WORDS) for _ in range(rng.randint(1, 3))]
        text = " ".join(words)
        if rng.random() < 0.3:
            text = text.replace(" ", rng.choice(["/", " ", " - "]), 1)
        return text
    if kind < 0.51:
        return " ".join(rng.choice(TURKISH_WORDS) for _ in range(rng.randint(1, 2)))
    if kind < 0.68:
        return _random_code(rng)
    if kind < 0.80:
        return _confusable_drill(rng)
    if kind < 0.92:
        return _stroke_drill(rng)

    length = rng.randint(3, 14)
    return "".join(rng.choice(CHARSET) for _ in range(length))


def _draw_icon(draw: ImageDraw.ImageDraw, box: tuple[int, int, int, int], ink: int,
               rng: random.Random) -> None:
    """One of the small pictograms a panel prints beside a label."""
    x0, y0, x1, y1 = box
    width = max(1, (x1 - x0) // 12)
    kind = rng.randrange(7)

    if kind == 0:
        draw.rounded_rectangle(box, radius=max(2, (x1 - x0) // 6), outline=ink, width=width)
    elif kind == 1:
        draw.ellipse(box, outline=ink, width=width)
    elif kind == 2:
        draw.polygon([(x0, y1), ((x0 + x1) // 2, y0), (x1, y1)], outline=ink)
    elif kind == 3:
        mid_x, mid_y = (x0 + x1) // 2, (y0 + y1) // 2
        draw.line([(mid_x, y0), (mid_x, y1)], fill=ink, width=width)
        draw.line([(x0, mid_y), (x1, mid_y)], fill=ink, width=width)
    elif kind == 4:
        draw.line([(x0, y0), (x1, y1)], fill=ink, width=width)
        draw.line([(x0, y1), (x1, y0)], fill=ink, width=width)
    elif kind == 5:
        mid_y = (y0 + y1) // 2
        draw.line([(x0, mid_y), (x1, mid_y)], fill=ink, width=width)
        draw.polygon([(x1, mid_y), (x1 - (x1 - x0) // 3, y0), (x1 - (x1 - x0) // 3, y1)], fill=ink)
    else:
        draw.rectangle(box, outline=ink, width=width)
        draw.line([(x0, (y0 + y1) // 2), (x1, (y0 + y1) // 2)], fill=ink, width=width)


def render(text: str, font: ImageFont.FreeTypeFont, rng: random.Random) -> Image.Image:
    """Draws the text dark-on-light or light-on-dark, with room around it for the crop to breathe."""
    bbox = font.getbbox(text)
    width = max(bbox[2] - bbox[0], 8)
    height = max(bbox[3] - bbox[1], 8)

    # From nothing to generous. The detector hands the recogniser a box hugging the ink, and a
    # model trained only on comfortably padded crops loses characters on those tight ones —
    # measured at 5/8 words with the padding it was trained on against 3/8 once trimmed to the text.
    pad_x = rng.choice([0, 0, 1, 2, 4, 7, 11, 16, 22])
    pad_y = rng.choice([0, 0, 1, 2, 4, 7, 11, 16])
    # Panel labels are printed with a pictogram in front of them, and the recogniser has no way to
    # answer "that is not a letter" - asked what a rounded square with a cross says, it answered "Ü",
    # with more confidence than it had in the letters around it. So it is not filtered afterwards,
    # it is trained: the icon is drawn and the label stays the text alone, which is the only thing
    # that teaches the model to pass over it.
    icon = rng.random() < 0.35
    icon_size = int(height * rng.uniform(0.7, 1.15)) if icon else 0
    icon_gap = int(height * rng.uniform(0.25, 0.7)) if icon else 0
    icon_leading = rng.random() < 0.75
    icon_span = icon_size + icon_gap if icon else 0

    canvas = Image.new("L", (width + 2 * pad_x + icon_span, height + 2 * pad_y), 0)

    # Laser marks are as often lighter than the surface as darker; the model should not assume.
    background = rng.randint(120, 235) if rng.random() < 0.7 else rng.randint(20, 90)
    separation = rng.randint(28, 190)
    foreground = max(0, background - separation) if background > 127 else min(255, background + separation)

    canvas.paste(background, (0, 0, canvas.width, canvas.height))
    draw = ImageDraw.Draw(canvas)

    text_x = pad_x + (icon_span if icon and icon_leading else 0)
    draw.text((text_x - bbox[0], pad_y - bbox[1]), text, fill=foreground, font=font)

    if icon:
        icon_x = pad_x if icon_leading else pad_x + icon_span + width + icon_gap - icon_span
        icon_y = pad_y + max(0, (height - icon_size) // 2)
        _draw_icon(draw, (icon_x, icon_y, icon_x + icon_size, icon_y + icon_size), foreground, rng)

    return canvas


def degrade(image: Image.Image, rng: random.Random) -> Image.Image:
    """Blur, noise, uneven lighting and slight rotation, in the ranges the live camera produces."""
    # A detection box is fitted, not exact: it stretches and clips a little. Training through that
    # is what keeps the reading stable between a hand-drawn window and a detected one.
    if rng.random() < 0.5:
        stretch = rng.uniform(0.82, 1.25)
        image = image.resize((max(8, int(image.width * stretch)), image.height), Image.BILINEAR)

    if rng.random() < 0.3:
        bite = rng.randint(1, 3)
        image = image.crop((bite, 0, max(bite + 8, image.width - bite), image.height))

    # A window drawn a little short cuts the tops or bottoms off the letters. Measured on the real
    # line: a window clipping the baseline of "Sıkma/Boşaltma" read it as "BoSaim". Operators draw
    # windows by eye, so the reading has to survive being trimmed into the text.
    if rng.random() < 0.3:
        top = rng.randint(0, 6)
        bottom = rng.randint(0, 6)
        if image.height - top - bottom >= 12:
            image = image.crop((0, top, image.width, image.height - bottom))

    # Whatever else the window caught: the dotted separator printed above a panel label, the edge of
    # a button, a moulding line. Without these the recogniser reads a stray rule as a row of marks.
    if rng.random() < 0.25:
        array = np.asarray(image)
        background = float(array.mean())
        ink = max(0, int(background - rng.randint(40, 120))) if background > 127 \
            else min(255, int(background + rng.randint(40, 120)))

        draw = ImageDraw.Draw(image)
        margin = max(1, image.height // 6)
        y = rng.randint(0, margin) if rng.random() < 0.5 else image.height - 1 - rng.randint(0, margin)

        if rng.random() < 0.45:
            draw.line([(0, y), (image.width, y)], fill=ink, width=rng.randint(1, 2))
        else:
            x = rng.randint(0, 4)
            while x < image.width:
                dash = rng.randint(2, 5)
                draw.line([(x, y), (min(x + dash, image.width), y)], fill=ink, width=1)
                x += dash + rng.randint(3, 7)

    if rng.random() < 0.25:
        image = image.rotate(rng.uniform(-2.5, 2.5), resample=Image.BICUBIC, fillcolor=int(np.asarray(image).mean()))

    blur = rng.choice([0.0, 0.0, 0.4, 0.8, 1.2, 1.8, 2.4])
    if blur:
        image = image.filter(ImageFilter.GaussianBlur(blur))

    array = np.asarray(image).astype(np.float32)

    # A gradient across the part: one end of a code is routinely darker than the other.
    if rng.random() < 0.4:
        height, width = array.shape
        ramp = np.linspace(rng.uniform(-30, 0), rng.uniform(0, 30), width, dtype=np.float32)
        array = array + ramp[None, :]

    noise = rng.choice([0.0, 0.0, 3.0, 6.0, 10.0, 14.0])
    if noise:
        array = array + np.random.normal(0, noise, array.shape)

    return Image.fromarray(np.clip(array, 0, 255).astype(np.uint8))


def to_tensor(image: Image.Image) -> np.ndarray:
    """
    Scaled to the recogniser's fixed height and normalised the way PP-OCR does, so a model trained
    here can be dropped in beside the stock ones without changing the C# preprocessing.
    """
    width = max(8, int(image.width * TARGET_HEIGHT / image.height))
    resized = image.resize((width, TARGET_HEIGHT), Image.BILINEAR)

    array = np.asarray(resized).astype(np.float32) / 255.0
    array = (array - 0.5) / 0.5
    return np.repeat(array[None, :, :], 3, axis=0)


def load_fonts() -> list[str]:
    """Only the fonts that can actually draw the Turkish letters."""
    import os

    usable = []
    probe = "ıİşŞğĞüÜöÖçÇ"
    for path in CANDIDATE_FONTS:
        if not os.path.exists(path):
            continue
        try:
            font = ImageFont.truetype(path, 32)
            if all(font.getbbox(char)[2] > 0 for char in probe):
                usable.append(path)
        except Exception:
            continue

    if not usable:
        raise RuntimeError("Türkçe harfleri çizebilen font bulunamadı.")
    return usable
