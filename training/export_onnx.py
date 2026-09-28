"""
Exports a trained checkpoint as a language pack the console can load.

The output deliberately imitates the stock PP-OCR recognition models — input named `x` with shape
[N, 3, H, W], output [N, T, classes], opset 14, one dictionary character per line — so the console
loads it through exactly the same path as the models that ship with RapidOcrNet. No new inference
code, no second code path to keep correct.

    python export_onnx.py --checkpoint out/turkish_rec.pt --install
"""

import argparse
import json
import shutil
from pathlib import Path

import torch

import charset
from model import CRNN

BACKEND_MODELS = Path(__file__).resolve().parents[1] / "backend" / "Vision" / "models"

MODEL_FILENAME = "turkish_rec.onnx"
DICTIONARY_FILENAME = "turkish_dict.txt"

MANIFEST_ENTRY = {
    "id": "turkish",
    "name": "Türkçe (bu hat için eğitilmiş)",
    "description": "Türkçe metin için eğitildi — ı, İ, ş, ğ, ü, ö, ç harflerini korur",
    "model": MODEL_FILENAME,
    "dictionary": DICTIONARY_FILENAME,

    # Read straight off the inspection window: it is already a single line, and putting the
    # detector in front of it measurably costs accuracy for a model trained on this framing.
    "lineModel": True,
}


def export(checkpoint: Path, out_dir: Path) -> tuple[Path, Path]:
    out_dir.mkdir(parents=True, exist_ok=True)

    model = CRNN(charset.num_classes())
    model.load_state_dict(torch.load(checkpoint, map_location="cpu"))
    model.eval()

    model_path = out_dir / MODEL_FILENAME
    dictionary_path = out_dir / DICTIONARY_FILENAME

    # Height is fixed at 48 by the architecture; batch and width stay dynamic because the runtime
    # sends one text line at a time, of whatever width the detector cropped.
    torch.onnx.export(
        model,
        torch.randn(1, 3, 48, 256),
        str(model_path),
        input_names=["x"],
        output_names=["fetch_name_0"],
        dynamic_axes={"x": {0: "batch", 3: "width"}, "fetch_name_0": {0: "batch", 1: "steps"}},
        opset_version=14,
        do_constant_folding=True,
        # The tracing exporter, not the new dynamo one: the latter bakes the batch dimension in and
        # emits opset 18, and the runtime expects the same contract the stock models use.
        dynamo=False,
    )

    charset.write_dictionary(dictionary_path)
    return model_path, dictionary_path


def install(model_path: Path, dictionary_path: Path) -> None:
    """Copies the pack next to the others and registers it, leaving existing packs untouched."""
    BACKEND_MODELS.mkdir(parents=True, exist_ok=True)
    shutil.copy2(model_path, BACKEND_MODELS / MODEL_FILENAME)
    shutil.copy2(dictionary_path, BACKEND_MODELS / DICTIONARY_FILENAME)

    manifest_path = BACKEND_MODELS / "languages.json"
    languages = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else []

    languages = [entry for entry in languages if entry.get("id") != MANIFEST_ENTRY["id"]]

    # First in the list and marked default: it is the one trained for the text this line reads.
    for entry in languages:
        entry.pop("isDefault", None)
    languages.insert(0, {**MANIFEST_ENTRY, "isDefault": True})

    manifest_path.write_text(
        json.dumps(languages, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"  kuruldu: {BACKEND_MODELS}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--checkpoint", type=Path, default=Path("out/turkish_rec.pt"))
    parser.add_argument("--out", type=Path, default=Path("out"))
    parser.add_argument("--install", action="store_true", help="copy into the backend and register")
    args = parser.parse_args()

    model_path, dictionary_path = export(args.checkpoint, args.out)
    print(f"  dışa aktarıldı: {model_path} ({model_path.stat().st_size / 1e6:.1f} MB)")
    print(f"  sözlük        : {dictionary_path} ({len(charset.CHARSET)} karakter)")

    if args.install:
        install(model_path, dictionary_path)


if __name__ == "__main__":
    main()
