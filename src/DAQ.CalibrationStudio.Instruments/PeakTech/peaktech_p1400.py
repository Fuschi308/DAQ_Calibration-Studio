"""Read and export a PeakTech P 1400 CH1 screen waveform."""

from __future__ import annotations

import argparse
import csv
import json
import logging
import math
import os
import re
import struct
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path
from typing import BinaryIO, Protocol

from libusb0_windows import IN_ENDPOINT, OUT_ENDPOINT, open_p1400


HEADER_QUERY = b":DATA:WAVE:SCREEN:HEAD?\r\n"
CH1_QUERY = b":DATA:WAVE:SCREEN:CH1?\r\n"
USB_READ_SIZE = 1024
MAX_HEADER_BYTES = 64 * 1024
MAX_CHANNEL_BYTES = 1024 * 1024
DEFAULT_TIMEOUT_MS = 3000
TIME_UNITS = {"ns": 1e-9, "us": 1e-6, "µs": 1e-6, "μs": 1e-6, "ms": 1e-3, "s": 1.0}
BIN_MAGIC = b"SPBXDS"
LENGTH = struct.Struct("<I")
WORDS = struct.Struct("<hh")
LOG = logging.getLogger("peaktech.p1400")


class ProtocolError(ValueError):
    """An unexpected response or unsupported screen format."""


class BulkTransport(Protocol):
    def bulk_write(self, data: bytes, timeout_ms: int) -> int: ...
    def bulk_read(self, size: int, timeout_ms: int) -> bytes: ...


@dataclass(frozen=True)
class ScreenHeader:
    idn: str
    run_status: str
    payload_bytes: int
    dt_s: float
    offset: int
    volts_per_count: float


@dataclass(frozen=True)
class Waveform:
    header: ScreenHeader
    rows: tuple[tuple[float, float, float], ...]  # time_s, min_v, max_v


def _scale(value: object, units: dict[str, float], field: str) -> float:
    if not isinstance(value, str):
        raise ProtocolError(f"Ungültige Skalierung für {field} ({value!r}).")
    match = re.fullmatch(r"(\d+(?:\.\d+)?)\s*([a-zµμ]+)", value.strip().lower())
    if not match or match[2] not in units:
        raise ProtocolError(f"Unbekannte Skalierung für {field} ({value!r}).")
    scaled = float(match[1]) * units[match[2]]
    if not math.isfinite(scaled) or scaled <= 0:
        raise ProtocolError(f"Ungültige Skalierung für {field} ({value!r}).")
    return scaled


def parse_header(payload: bytes, *, from_bin: bool = False, channel_name: str = "CH1") -> ScreenHeader:
    try:
        header = json.loads(payload.decode("utf-8"))
        idn = header["IDN"]
        run_status = header["RUNSTATUS"]
        sample = header["SAMPLE"]
        words = sample["DATALEN"]
        channels = header["CHANNEL"]
        timebase = header["TIMEBASE"]["SCALE"]
    except (KeyError, TypeError, UnicodeError, json.JSONDecodeError, RecursionError) as exc:
        raise ProtocolError("Ungültiger oder unvollständiger JSON-Header.") from exc

    if (
        header.get("MODEL") != "102200103"
        or not isinstance(idn, str)
        or idn.split(",", 2)[1:2] != ["P1400"]
    ):
        raise ProtocolError("Der Header weist das Gerät nicht als P 1400 (102200103) aus.")
    if not isinstance(run_status, str) or not run_status:
        raise ProtocolError("Ungültiger Gerätestatus im Header.")
    if (
        header.get("DATATYPE") != "SCREEN"
        or sample.get("SLOWMOVE") != -1
        or sample.get("SCREENOFFSET", 0) != 0
        or sample.get("FULLSCREEN") != words
    ):
        raise ProtocolError("Erwartet wird ein vollständiges SCREEN-Fenster.")
    if type(words) is not int or words % 2 or not 0 < words * 2 <= MAX_CHANNEL_BYTES:
        raise ProtocolError("Ungültige Anzahl der 16-Bit-Kanalwörter.")
    if not isinstance(channels, list) or not all(isinstance(channel, dict) for channel in channels):
        raise ProtocolError("Ungültige Kanalliste im Header.")
    selected = [channel for channel in channels if channel.get("NAME") == channel_name]
    if len(selected) != 1 or selected[0].get("DISPLAY") != "ON":
        raise ProtocolError(f"{channel_name} muss am Oszilloskop eingeschaltet sein.")
    channel = selected[0]
    if from_bin and (
        header.get("IsSkipIfClosed") is not True
        or [item for item in channels if item.get("DISPLAY") == "ON"] != selected
    ):
        raise ProtocolError("Für den BIN-Import wird ein DS_Wave-Einzelexport von CH1 erwartet.")
    if str(channel.get("INVERSE", "OFF")).upper() != "OFF":
        raise ProtocolError(f"USB-Skalierung für invertiertes {channel_name} ist ungeprüft.")
    offset = channel.get("OFFSET")
    if type(offset) is not int or not -(2**31) <= offset < 2**31:
        raise ProtocolError(f"Ungültiger {channel_name}-Offset im Header.")
    seconds_per_div = _scale(timebase, TIME_UNITS, "TIMEBASE.SCALE")
    volts_per_div = _scale(channel.get("SCALE"), {"mv": 1e-3, "v": 1.0}, f"{channel_name}.SCALE")
    probe = _scale(channel.get("PROBE"), {"x": 1.0}, f"{channel_name}.PROBE")
    dt_s = seconds_per_div * 15.2 / (words // 2)
    volts_per_count = volts_per_div * probe / 50
    if not (0 < dt_s < math.inf and 0 < volts_per_count * (abs(offset) + 4096) < math.inf):
        raise ProtocolError("Zeit- oder Spannungsskalierung außerhalb des Wertebereichs.")
    LOG.debug("Wellenform-Header %s", header)
    return ScreenHeader(idn, run_status, words * 2, dt_s, offset, volts_per_count)


def decode_waveform(payload: bytes, header: ScreenHeader) -> Waveform:
    if len(payload) != header.payload_bytes:
        raise ProtocolError(f"CH1-Datenblock mit {len(payload)} statt {header.payload_bytes} Byte.")
    rows = []
    for index, (raw_max, raw_min) in enumerate(WORDS.iter_unpack(payload)):
        if raw_max < raw_min:
            raise ProtocolError(f"Ungültige Max/Min-Reihenfolge bei Paar {index}.")
        minimum = ((raw_min >> 3) - header.offset) * header.volts_per_count
        maximum = ((raw_max >> 3) - header.offset) * header.volts_per_count
        rows.append((index * header.dt_s, minimum, maximum))
    return Waveform(header, tuple(rows))


def _payload_size(prefix: bytes, expected: int | None) -> int:
    if len(prefix) != LENGTH.size:
        raise ProtocolError("Unvollständiges 4-Byte-Längenpräfix.")
    size, = LENGTH.unpack(prefix)
    if expected is not None and size != expected:
        raise ProtocolError(f"Gemeldete CH1-Blocklänge {size} Byte, laut Header {expected} Byte erwartet.")
    if expected is None and not 0 < size <= MAX_HEADER_BYTES:
        raise ProtocolError(f"Ungültige Headerlänge ({size} Byte).")
    return size


def _query(transport: BulkTransport, command: bytes, timeout_ms: int, expected: int | None = None) -> bytes:
    LOG.info("Bulk OUT 0x%02X %r [%s]", OUT_ENDPOINT, command, command.hex(" "))
    deadline = time.monotonic() + timeout_ms / 1000
    written = transport.bulk_write(command, timeout_ms)
    if written != len(command):
        raise ProtocolError(f"Bulk OUT hat {written} von {len(command)} Byte übertragen.")

    response = bytearray()
    end = None
    while end is None or len(response) < end:
        remaining_ms = min(timeout_ms, math.ceil((deadline - time.monotonic()) * 1000))
        if remaining_ms <= 0:
            raise ProtocolError(f"Timeout für {command!r} nach {len(response)} empfangenen Byte.")
        # SCREEN headers can end on a USB packet boundary without a short packet.
        # Read the first packet separately and use the declared length for the final request.
        read_size = 64 if end is None and command == HEADER_QUERY else USB_READ_SIZE
        if end is not None and command == HEADER_QUERY:
            read_size = min(USB_READ_SIZE, end - len(response))
        chunk = transport.bulk_read(read_size, remaining_ms)
        LOG.debug("Bulk IN 0x%02X %d Byte (angefordert %d)", IN_ENDPOINT, len(chunk), read_size)
        if not chunk:
            raise ProtocolError(f"Leere USB-Antwort für {command!r} nach {len(response)} Byte.")
        response.extend(chunk)
        if end is None and len(response) >= LENGTH.size:
            end = LENGTH.size + _payload_size(response[:LENGTH.size], expected)
    if len(response) != end:
        raise ProtocolError(f"USB-Block enthält {len(response) - end} Byte hinter der gemeldeten Länge.")
    LOG.info("Bulk IN 0x%02X %d Byte Nutzdaten", IN_ENDPOINT, end - LENGTH.size)
    return bytes(response[LENGTH.size:])


def capture_ch1(transport: BulkTransport, timeout_ms: int = DEFAULT_TIMEOUT_MS) -> Waveform:
    if type(timeout_ms) is not int or not 1 <= timeout_ms <= 2**31 - 1:
        raise ValueError("Der Timeout muss eine positive 32-Bit-Ganzzahl in Millisekunden sein.")
    header = parse_header(_query(transport, HEADER_QUERY, timeout_ms))
    LOG.info("Gerät %s · RUNSTATUS=%s", header.idn, header.run_status)
    payload = _query(transport, CH1_QUERY, timeout_ms, header.payload_bytes)
    return decode_waveform(payload, header)


def _read_bin_block(source: BinaryIO, expected: int | None = None) -> bytes:
    size = _payload_size(source.read(LENGTH.size), expected)
    data = source.read(size)
    if len(data) != size:
        raise ProtocolError(f"Unvollständiger BIN-Block mit {len(data)} von {size} Byte.")
    return data


def read_bin(path: Path) -> Waveform:
    with path.open("rb") as source:
        if source.read(len(BIN_MAGIC)) != BIN_MAGIC:
            raise ProtocolError("Ungültige SPBXDS-Dateikennung.")
        header = parse_header(_read_bin_block(source), from_bin=True)
        payload = _read_bin_block(source, header.payload_bytes)
        if source.read(1):
            raise ProtocolError("Zusätzliche Daten hinter dem CH1-Block der BIN-Datei.")
    return decode_waveform(payload, header)


def write_csv(output: Path, waveform: Waveform) -> None:
    # Publish only a completed CSV, with exclusive creation of the destination.
    temporary = tempfile.NamedTemporaryFile(
        dir=output.parent, prefix=".p1400-", suffix=".tmp", delete=False, encoding="utf-8", mode="w", newline=""
    )
    staging = Path(temporary.name)
    try:
        with temporary as stream:
            writer = csv.writer(stream, lineterminator="\n")
            writer.writerow(("time_s", "min_v", "max_v"))
            writer.writerows(tuple(format(value, ".15g") for value in row) for row in waveform.rows)
        if os.name == "nt":
            staging.rename(output)
        else:
            os.link(staging, output)
    finally:
        staging.unlink(missing_ok=True)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="CH1-Bildschirmwellenform des PeakTech P 1400 als CSV.")
    parser.add_argument("--verbose", action="store_true", help="JSON-Header und USB-Teillängen protokollieren")
    actions = parser.add_subparsers(dest="action", required=True)

    live = actions.add_parser("capture-ch1", help="CH1 über libusb-win32 abrufen")
    live.add_argument("--serial", required=True, help="erwarteter USB-Serienstring")
    live.add_argument(
        "--timeout-ms", type=int, default=DEFAULT_TIMEOUT_MS,
        help="Millisekunden je Bulk-Abfrage (Standard %(default)s)",
    )
    live.add_argument("--output", type=Path, required=True, help="Pfad für eine neue CSV")

    offline = actions.add_parser("decode-bin", help="DS_Wave-CH1-BIN offline umrechnen")
    offline.add_argument("input", type=Path, help="SPBXDS-Datei mit einem CH1-Datenblock")
    offline.add_argument("--output", type=Path, required=True, help="Pfad für eine neue CSV")
    args = parser.parse_args(argv)
    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)s %(message)s",
    )

    try:
        if os.path.lexists(args.output):
            raise FileExistsError(f"Die Ausgabedatei existiert bereits ({args.output}).")
        if not args.output.parent.is_dir():
            raise FileNotFoundError(f"Das Ausgabeverzeichnis fehlt ({args.output.parent}).")
        if args.action == "capture-ch1":
            if not 1 <= args.timeout_ms <= 2**31 - 1:
                parser.error("--timeout-ms muss zwischen 1 und 2147483647 liegen")
            with open_p1400(args.serial) as transport:
                waveform = capture_ch1(transport, args.timeout_ms)
        else:
            waveform = read_bin(args.input)
        write_csv(args.output, waveform)
    except (OSError, ValueError) as exc:
        LOG.error("%s", exc)
        return 1
    LOG.info("%d CH1-Paare mit %.9g s Abstand nach %s exportiert",
             len(waveform.rows), waveform.header.dt_s, args.output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
