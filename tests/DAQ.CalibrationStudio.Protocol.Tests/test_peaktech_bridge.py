"""Synthetic replay of the bundled P 1400 bridge, with no device or captures required."""

import json
import struct
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src" / "DAQ.CalibrationStudio.Instruments" / "PeakTech"))

import peaktech_p1400 as protocol
import studio_bridge


def make_header():
    return {
        "MODEL": "102200103", "IDN": ",P1400,SYNTHETIC,V0.TEST",
        "RUNSTATUS": "AUTO", "DATATYPE": "SCREEN",
        "SAMPLE": {"DATALEN": 4, "FULLSCREEN": 4, "SLOWMOVE": -1},
        "TIMEBASE": {"SCALE": "500us"},
        "CHANNEL": [
            {"NAME": "CH1", "DISPLAY": "ON", "SCALE": "500mV", "PROBE": "10X",
             "OFFSET": 1, "COUPLING": "DC", "INVERSE": "OFF"},
            {"NAME": "CH2", "DISPLAY": "ON", "SCALE": "1.00 V", "PROBE": "1X",
             "OFFSET": -2, "COUPLING": "AC", "INVERSE": "OFF"},
        ],
        "Trig": {"Type": "EDGE", "Sweep": "AUTO", "Items": {"Channel": "CH2", "Edge": "FALL", "Level": "3.50V"}},
    }


class Replay:
    def __init__(self, header):
        payload = json.dumps(header).encode("ascii").ljust(828, b" ")
        self.packet_sizes = []
        self.responses = {
            protocol.HEADER_QUERY: struct.pack("<I", len(payload)) + payload,
            protocol.CH1_QUERY: struct.pack("<Ihhhh", 8, 24, 8, 40, 32),
            b":DATA:WAVE:SCREEN:CH2?\r\n": struct.pack("<Ihhhh", 8, -8, -16, 0, -8),
        }
        self.writes = []
        self.pending = b""

    def bulk_write(self, request, timeout_ms):
        assert not self.pending
        self.writes.append(request)
        self.pending = self.responses[request]
        return len(request)

    def bulk_read(self, size, timeout_ms):
        self.packet_sizes.append(size)
        if len(self.pending) % 64 == 0 and size > len(self.pending):
            raise TimeoutError("Synthetic exact USB boundary: no short packet")
        chunk, self.pending = self.pending[:size], self.pending[size:]
        return chunk


class PeakTechBridgeTests(unittest.TestCase):
    def test_dual_channel_capture_and_split_packet_length(self):
        transport = Replay(make_header())
        ch1, ch2 = studio_bridge.capture(transport)
        self.assertEqual([64, 768, 1024, 1024], transport.packet_sizes)
        self.assertEqual((1, 2), (ch1["Channel"], ch2["Channel"]))
        self.assertEqual((0.0, 0.3), tuple(round(value, 5) for value in ch1["MinimumVolts"]))
        self.assertEqual((0.02, 0.04), ch2["MaximumVolts"])
        self.assertAlmostEqual(0.0038, ch2["Seconds"][1])
        self.assertFalse(transport.pending)

    def test_reads_settings_from_header(self):
        result = studio_bridge.settings(make_header())
        self.assertEqual(0.0005, result["TimePerDiv"])
        self.assertEqual(1.0, result["VoltsPerDiv2"])
        self.assertEqual("AC", result["Coupling2"])
        self.assertFalse(result["Rising"])

    def test_does_not_decode_unverified_inverted_channel(self):
        header = make_header()
        header["CHANNEL"][1]["INVERSE"] = "ON"
        with self.assertRaisesRegex(protocol.ProtocolError, "invertiertes CH2"):
            studio_bridge.capture(Replay(header))

    def test_refuses_unverified_setting_before_writing(self):
        header = make_header()
        transport = Replay(header)
        desired = studio_bridge.settings(header) | {"TimePerDiv": 0.123}
        with self.assertRaisesRegex(ValueError, "Einstellbare Werte"):
            studio_bridge.configure(transport, desired)
        self.assertEqual([protocol.HEADER_QUERY], transport.writes)


if __name__ == "__main__":
    unittest.main()
