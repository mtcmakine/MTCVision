"""
Trains the Turkish recogniser and exports it in the form the console loads.

Run from this folder:
    python train.py --minutes 90

Samples are generated as training runs rather than written to disk first: rendering is cheaper than
reading, and it means every batch is fresh, which matters when the whole dataset is synthetic and a
fixed set would be memorised long before it generalised.
"""

import argparse
import random
import time
from pathlib import Path

import numpy as np
import torch
from PIL import ImageFont
from torch import nn
from torch.utils.data import DataLoader, IterableDataset

import charset
import dataset as data
from model import CRNN


# Every sample is scaled and padded to one width. Batching mixed widths wasted nearly half the
# compute on padding — measured average 214 pixels against a 509 maximum — and a fixed width also
# keeps each step's cost predictable, which matters when the budget is a wall-clock deadline.
FIXED_WIDTH = 256


class SyntheticLines(IterableDataset):
    def __init__(self, fonts: list[str], seed: int):
        self.fonts = fonts
        self.seed = seed

    def __iter__(self):
        info = torch.utils.data.get_worker_info()
        rng = random.Random(self.seed + (info.id if info else 0))
        np.random.seed(self.seed + (info.id if info else 0))

        sizes = [22, 26, 30, 34, 40, 48, 56]
        cache: dict[tuple[str, int], ImageFont.FreeTypeFont] = {}

        while True:
            text = data.random_text(rng)
            if not text.strip():
                continue

            key = (rng.choice(self.fonts), rng.choice(sizes))
            font = cache.get(key)
            if font is None:
                font = ImageFont.truetype(key[0], key[1])
                cache[key] = font

            labels = charset.encode(text)
            if not labels:
                continue

            image = data.degrade(data.render(text, font, rng), rng)
            tensor = data.to_tensor(image)

            # Too wide to fit the fixed frame, or too narrow for CTC to emit every character:
            # either way the sample would teach the model something untrue.
            if tensor.shape[2] > FIXED_WIDTH or tensor.shape[2] < 8 * len(labels):
                continue

            padded = np.zeros((3, data.TARGET_HEIGHT, FIXED_WIDTH), dtype=np.float32)
            padded[:, :, : tensor.shape[2]] = tensor
            yield padded, np.array(labels, dtype=np.int64)


def collate(batch):
    """Samples already share a width; CTC still needs the sequence length the network produces."""
    images = np.stack([item[0] for item in batch])
    targets = [item[1] for item in batch]

    sequence_length = FIXED_WIDTH // CRNN.width_divisor()
    return (
        torch.from_numpy(images),
        torch.from_numpy(np.concatenate(targets)),
        torch.full((len(batch),), sequence_length, dtype=torch.long),
        torch.tensor([len(t) for t in targets], dtype=torch.long),
    )


def decode(logits: torch.Tensor) -> list[str]:
    """Greedy CTC: collapse repeats, drop blanks. Enough to watch training progress."""
    space = len(charset.CHARSET) + 1
    outputs = []
    for sequence in logits.argmax(dim=2).cpu().numpy():
        chars = []
        previous = -1
        for index in sequence:
            if index != previous and index != 0:
                chars.append(" " if index == space else charset.CHARSET[index - 1])
            previous = index
        outputs.append("".join(chars))
    return outputs


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--minutes", type=float, default=60.0, help="training budget")
    parser.add_argument("--batch", type=int, default=48)
    parser.add_argument("--lr", type=float, default=3e-4)
    parser.add_argument("--workers", type=int, default=3)
    parser.add_argument("--out", type=Path, default=Path("out"))
    parser.add_argument("--resume", type=Path, default=None)
    args = parser.parse_args()

    args.out.mkdir(parents=True, exist_ok=True)
    torch.set_num_threads(max(1, torch.get_num_threads()))

    fonts = data.load_fonts()
    print(f"  {len(fonts)} font, {len(charset.CHARSET)} karakter, {charset.num_classes()} sınıf")

    loader = DataLoader(
        SyntheticLines(fonts, seed=1234),
        batch_size=args.batch,
        num_workers=args.workers,
        collate_fn=collate,
        persistent_workers=args.workers > 0,
        prefetch_factor=4 if args.workers > 0 else None,
    )

    model = CRNN(charset.num_classes())
    if args.resume and args.resume.exists():
        model.load_state_dict(torch.load(args.resume, map_location="cpu"))
        print(f"  {args.resume} üzerinden devam ediliyor")

    criterion = nn.CTCLoss(blank=0, zero_infinity=True)
    optimiser = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-4)

    # Warm up over a few hundred steps, then hold. A one-cycle schedule needs to know the total
    # step count, and this budget is wall-clock rather than steps — sized against a guess it spent
    # the whole run still warming up, training at a tenth of the intended rate.
    warmup = 300
    scheduler = torch.optim.lr_scheduler.LambdaLR(
        optimiser, lambda step: min(1.0, (step + 1) / warmup))

    deadline = time.time() + args.minutes * 60
    running = 0.0
    step = 0
    model.train()

    for images, targets, input_lengths, target_lengths in loader:
        logits = model(images)
        log_probs = logits.log_softmax(dim=2).permute(1, 0, 2)  # CTC wants [T, N, C]

        loss = criterion(log_probs, targets, input_lengths, target_lengths)
        if not torch.isfinite(loss):
            continue

        optimiser.zero_grad(set_to_none=True)
        loss.backward()
        # Recurrent layers on synthetic data spike early; clipping keeps a bad batch from
        # undoing an hour of progress.
        nn.utils.clip_grad_norm_(model.parameters(), 5.0)
        optimiser.step()
        scheduler.step()

        running += loss.item()
        step += 1

        if step % 100 == 0:
            # A sample decode alongside the loss: CTC loss falls for a long time while the model
            # still emits nothing, and the decoded text is what says whether it is really learning.
            with torch.no_grad():
                sample = decode(logits[:1])[0]
            print(f"  adım {step:6d}  kayıp {running / 100:7.4f}  "
                  f"lr {optimiser.param_groups[0]['lr']:.2e}  "
                  f"örnek {sample[:24]!r}  "
                  f"kalan {max(0, deadline - time.time()) / 60:5.1f} dk", flush=True)
            running = 0.0

        if step % 1000 == 0:
            torch.save(model.state_dict(), args.out / "turkish_rec.pt")

        if time.time() >= deadline:
            break

    torch.save(model.state_dict(), args.out / "turkish_rec.pt")
    print(f"  {step} adım tamamlandı, kaydedildi: {args.out / 'turkish_rec.pt'}")


if __name__ == "__main__":
    main()
