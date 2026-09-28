"""
A CRNN text recogniser, shaped to drop straight into the running console.

Its input and output match the PP-OCR recognition contract — [N, 3, 48, W] in, [N, T, classes] out,
CTC-decoded against a one-character-per-line dictionary — so the exported model is loaded by the
same C# code that loads the stock ones, with no new inference path to maintain.

Built around depthwise-separable convolutions rather than plain ones. There is no GPU here, and a
straightforward CRNN measured 3.3 seconds per batch on this CPU, which over any realistic training
budget is a few tens of thousands of samples — nowhere near enough to learn an alphabet. Separating
each convolution into a per-channel spatial pass and a 1x1 mixing pass costs a fraction of that for
the same receptive field, which is what makes the run finish.

The job is also narrow: one alphabet, printed flat on industrial labels, not general scene text in
a dozen scripts. The capacity is sized for that.
"""

import torch
from torch import nn


def separable(in_channels: int, out_channels: int, stride=(1, 1)) -> nn.Sequential:
    """Depthwise 3x3 followed by a pointwise mix — roughly an eighth of the work of a dense 3x3."""
    return nn.Sequential(
        nn.Conv2d(in_channels, in_channels, 3, stride=stride, padding=1, groups=in_channels, bias=False),
        nn.BatchNorm2d(in_channels),
        nn.ReLU(inplace=True),
        nn.Conv2d(in_channels, out_channels, 1, bias=False),
        nn.BatchNorm2d(out_channels),
        nn.ReLU(inplace=True),
    )


class CRNN(nn.Module):
    def __init__(self, num_classes: int, channels: int = 3):
        super().__init__()

        # Height collapses 48 -> 1. Width is divided by eight rather than four: a character on these
        # labels is around 25 pixels wide once scaled, so eight-pixel steps still leave three
        # columns per character for CTC to work with, at half the sequence length to recur over.
        self.features = nn.Sequential(
            # The first convolution stays dense — depthwise on three input channels has too little
            # to separate and saves nothing.
            nn.Conv2d(channels, 24, 3, stride=1, padding=1, bias=False),
            nn.BatchNorm2d(24), nn.ReLU(inplace=True),
            nn.MaxPool2d(2, 2),                       # 48 x W    -> 24 x W/2

            separable(24, 48),
            nn.MaxPool2d(2, 2),                       # 24 x W/2  -> 12 x W/4

            separable(48, 96),
            separable(96, 96),
            nn.MaxPool2d((2, 2), (2, 2)),             # 12 x W/4  ->  6 x W/8

            separable(96, 160),
            nn.MaxPool2d((2, 1), (2, 1)),             #  6 x W/8  ->  3 x W/8

            nn.Conv2d(160, 192, (3, 1), padding=(0, 0), bias=False),
            nn.BatchNorm2d(192), nn.ReLU(inplace=True),  # 3 x W/8 ->  1 x W/8
        )

        # One recurrent layer: a diacritic is a local mark, and the neighbouring columns are enough
        # context to tell a dotless i from a dotted one. A second layer doubled the cost for no
        # measurable gain on this alphabet.
        self.recurrent = nn.LSTM(192, 160, num_layers=1, bidirectional=True, batch_first=True)
        self.classifier = nn.Linear(320, num_classes)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        features = self.features(x)           # [N, 192, 1, W/8]
        features = features.squeeze(2)        # [N, 192, W/8]
        sequence = features.permute(0, 2, 1)  # [N, W/8, 192]
        sequence, _ = self.recurrent(sequence)
        return self.classifier(sequence)      # [N, T, classes]

    @staticmethod
    def width_divisor() -> int:
        """How much the network shortens the width; CTC input lengths are derived from it."""
        return 8
