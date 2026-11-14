#!/usr/bin/env python3
"""Отправляет toServer720.bin на сервер и сохраняет первые 500 КБ ответа."""
import socket
import sys
from pathlib import Path

HOST = "192.168.31.150"
PORT = 9000
TARGET_SIZE = 500 * 1024  # 500 КБ
TIMEOUT = 30  # секунд

BASE_DIR = Path(__file__).resolve().parent
INPUT_FILE = BASE_DIR / "toServer720.bin"
OUTPUT_FILE = BASE_DIR / "fromServer.bin"


def main() -> int:
    payload = INPUT_FILE.read_bytes()

    received = bytearray()
    with socket.create_connection((HOST, PORT), timeout=TIMEOUT) as sock:
        sock.sendall(payload)
        print(f"Отправлено {len(payload)} байт на {HOST}:{PORT}")

        while len(received) < TARGET_SIZE:
            chunk = sock.recv(min(65536, TARGET_SIZE - len(received)))
            if not chunk:
                print("Сервер закрыл соединение раньше времени", file=sys.stderr)
                break
            received.extend(chunk)

    OUTPUT_FILE.write_bytes(received)
    print(f"Получено {len(received)} байт, сохранено в {OUTPUT_FILE}")
    return 0 if len(received) >= TARGET_SIZE else 1


if __name__ == "__main__":
    sys.exit(main())
