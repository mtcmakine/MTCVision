"""
The alphabet the Turkish recogniser can output.

Kept small on purpose. The stock PP-OCR Latin model carries 502 characters covering a dozen
languages, and spends capacity on scripts this line will never photograph. Restricting the output
to what actually appears on a Turkish part — the alphabet, digits and the punctuation used in
programme names and part codes — leaves a far easier problem, which is what makes training a usable
model on a CPU realistic.

Order matters: it becomes the dictionary file, and the model's class indices are read from it.
"""

# Turkish alphabet, plus the three letters it borrows for foreign words and codes.
UPPERCASE = "ABCÇDEFGĞHIİJKLMNOÖPQRSŞTUÜVWXYZ"
LOWERCASE = "abcçdefgğhıijklmnoöprsştuüvyzqwx"

DIGITS = "0123456789"

# What shows up on programme labels, rating plates and laser-marked codes.
PUNCTUATION = ".,:;/-_()[]%+*&'\"!?#°<>=@"

CHARSET = UPPERCASE + LOWERCASE + DIGITS + PUNCTUATION

# The pairs this model exists to get right. A plain Latin recogniser strips the marks off these,
# which is exactly the failure that made a correct part read as the wrong text.
CONFUSABLE_PAIRS = [
    ("ı", "i"),
    ("İ", "I"),
    ("ş", "s"),
    ("Ş", "S"),
    ("ğ", "g"),
    ("Ğ", "G"),
    ("ü", "u"),
    ("Ü", "U"),
    ("ö", "o"),
    ("Ö", "O"),
    ("ç", "c"),
    ("Ç", "C"),
]


def num_classes() -> int:
    """
    CTC blank at index 0, the alphabet at 1..n, and a trailing space class — the layout PP-OCR
    models use and the one the C# runtime already knows how to decode.
    """
    return len(CHARSET) + 2


def write_dictionary(path) -> None:
    """One character per line, in class order, as the runtime expects."""
    with open(path, "w", encoding="utf-8") as handle:
        for char in CHARSET:
            handle.write(char + "\n")


def encode(text: str) -> list[int]:
    """Text to class indices, skipping anything outside the alphabet."""
    lookup = {char: index + 1 for index, char in enumerate(CHARSET)}
    space = len(CHARSET) + 1
    return [space if char == " " else lookup[char] for char in text if char == " " or char in lookup]
