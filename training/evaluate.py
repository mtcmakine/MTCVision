"""
Scores a checkpoint on the words actually photographed off the machine.

Synthetic validation loss says whether the model learned the renderer. This says whether it can
read the part, which is the only question that matters — and it reports the diacritics separately,
because getting those right is the entire reason this model exists.

    python evaluate.py --crops <panel crop folder> [--checkpoint out/turkish_rec.pt]
"""

import argparse
import json
from pathlib import Path

import numpy as np
import torch
from PIL import Image

import charset
import dataset as data
from model import CRNN


def read(model: CRNN, image: Image.Image, width: int = 256) -> str:
    tensor = data.to_tensor(image)
    if tensor.shape[2] > width:
        # Squeeze an over-wide crop rather than cropping it: losing the tail would look like a
        # recognition failure when it is a framing one.
        scaled = image.resize((width, data.TARGET_HEIGHT), Image.BILINEAR)
        tensor = data.to_tensor(scaled)

    padded = np.zeros((1, 3, data.TARGET_HEIGHT, width), dtype=np.float32)
    padded[0, :, :, : tensor.shape[2]] = tensor

    with torch.no_grad():
        logits = model(torch.from_numpy(padded))

    space = len(charset.CHARSET) + 1
    chars = []
    previous = -1
    for index in logits.argmax(dim=2)[0].numpy():
        if index != previous and index != 0:
            chars.append(" " if index == space else charset.CHARSET[index - 1])
        previous = index
    return "".join(chars).strip()


def levenshtein(a: str, b: str) -> int:
    previous = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        current = [i]
        for j, cb in enumerate(b, 1):
            current.append(min(current[-1] + 1, previous[j] + 1, previous[j - 1] + (ca != cb)))
        previous = current
    return previous[-1]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--crops", type=Path, required=True)
    parser.add_argument("--checkpoint", type=Path, default=Path("out/turkish_rec.pt"))
    args = parser.parse_args()

    truth = json.loads((args.crops / "truth.json").read_text(encoding="utf-8"))

    model = CRNN(charset.num_classes())
    model.load_state_dict(torch.load(args.checkpoint, map_location="cpu"))
    model.eval()

    total_similarity = 0.0
    exact = 0
    diacritics_right = 0
    diacritics_total = 0

    marked = set("ıİşŞğĞüÜöÖçÇ")
    for key in sorted(truth):
        path = args.crops / f"{key}.png"
        if not path.exists():
            continue

        expected = truth[key]
        got = read(model, Image.open(path).convert("L"))

        distance = levenshtein(got, expected)
        similarity = max(0.0, 100.0 * (1 - distance / max(len(got), len(expected), 1)))
        total_similarity += similarity
        if got == expected:
            exact += 1

        # Did the accented letters survive, one by one?
        for position, char in enumerate(expected):
            if char in marked:
                diacritics_total += 1
                if position < len(got) and got[position] == char:
                    diacritics_right += 1

        flag = "TAM" if got == expected else "   "
        print(f"  {expected:<18} -> {got:<20} {similarity:5.0f}%  {flag}")

    count = len(truth)
    print()
    print(f"  ortalama benzerlik : %{total_similarity / max(count, 1):.1f}")
    print(f"  tam eşleşme        : {exact}/{count}")
    if diacritics_total:
        print(f"  aksanlı harf       : {diacritics_right}/{diacritics_total} doğru")


if __name__ == "__main__":
    main()
