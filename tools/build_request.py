#!/usr/bin/env python3
"""Собирает 500-байтный запрос к регистратору (см. PROTOCOL.md).

Поля, назначение которых не установлено, оставлены как в исходном toServer720.bin.
Всё после смещения 0x28 в исходном файле — мусор из памяти клиента, устройство его игнорирует,
поэтому здесь там нули (проверено на устройстве).

    python3 tools/build_request.py [--channel N] [--stream S] [-o файл]
"""
import argparse
import struct
import sys

SIZE = 500


def build_request(channel: int = 1, stream: int = 1) -> bytes:
    r = bytearray(SIZE)
    fields = [
        (0x00, 1),            # не влияет на ответ
        (0x04, 3),            # обязательно 3 (вероятно, команда «открыть поток»)
        (0x08, 0x03000000),   # не влияет на поток, эхо в заголовке ответа
        (0x10, 0x68),         # не влияет
        (0x14, 1),            # обязательно 1 (назначение неизвестно)
        (0x18, 0x10),         # не влияет
        (0x1C, channel),      # номер канала/камеры; 0 → отказ
        (0x20, 1),            # не влияет на картинку (назначение неизвестно)
        (0x24, stream),       # 0 → 352x288 (CIF), иначе 704x576 (D1)
    ]
    for off, val in fields:
        r[off:off + 4] = struct.pack(">I", val)
    return bytes(r)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--channel", type=int, default=1)
    ap.add_argument("--stream", type=int, default=1)
    ap.add_argument("-o", "--output", default="-")
    a = ap.parse_args()
    data = build_request(a.channel, a.stream)
    if a.output == "-":
        sys.stdout.buffer.write(data)
    else:
        open(a.output, "wb").write(data)
    return 0


if __name__ == "__main__":
    sys.exit(main())
