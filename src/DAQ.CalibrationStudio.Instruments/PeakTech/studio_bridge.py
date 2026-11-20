"""Line-delimited JSON bridge for one explicitly selected P 1400 session."""

from __future__ import annotations

import json
import math
import re
import sys
import time

from libusb0_windows import discover_p1400, open_p1400
from peaktech_p1400 import (
    DEFAULT_TIMEOUT_MS, HEADER_QUERY, TIME_UNITS, ProtocolError, _query, _scale,
    decode_waveform, parse_header,
)


def header(transport):
    payload = _query(transport, HEADER_QUERY, DEFAULT_TIMEOUT_MS)
    try:
        data = json.loads(payload)
        _, model, serial, firmware = data["IDN"].split(",")
        if (model, data["MODEL"]) != ("P1400", "102200103"):
            raise ProtocolError("Das Gerät meldet kein P 1400.")
    except (TypeError, KeyError, UnicodeError, ValueError) as exc:
        raise ProtocolError("Ungültige Modellkennung im P 1400-Header.") from exc
    return payload, data, {"Model": model, "Serial": serial or None, "Firmware": firmware or None}


def settings(data):
    channels = {channel["NAME"]: channel for channel in data["CHANNEL"]}
    trigger = data["Trig"]
    items = trigger["Items"]
    level = re.fullmatch(r"([+-]?\d+(?:\.\d+)?)\s*(m?V)", items.get("Level", ""), re.IGNORECASE)
    if trigger.get("Type", "").upper() != "EDGE":
        raise ValueError("Bitte am P 1400 den Flankentrigger auswählen.")
    if not level or items["Channel"] not in ("CH1", "CH2") or items["Edge"].upper() not in ("RISE", "FALL"):
        raise ProtocolError("Ungültige Flankentriggereinstellungen im Geräteheader.")
    return {
        "TimePerDiv": _scale(data["TIMEBASE"]["SCALE"], TIME_UNITS, "TIMEBASE.SCALE"),
        "VoltsPerDiv1": _scale(channels["CH1"]["SCALE"], {"mv": 0.001, "v": 1}, "CH1.SCALE"),
        "VoltsPerDiv2": _scale(channels["CH2"]["SCALE"], {"mv": 0.001, "v": 1}, "CH2.SCALE"),
        "TriggerChannel": int(items["Channel"][-1]),
        "TriggerLevel": float(level[1]) * (0.001 if level[2].lower() == "mv" else 1),
        "Rising": items["Edge"].upper() == "RISE",
        "Coupling1": channels["CH1"]["COUPLING"].upper(),
        "Coupling2": channels["CH2"]["COUPLING"].upper(),
        "Show1": channels["CH1"]["DISPLAY"].upper() == "ON",
        "Show2": channels["CH2"]["DISPLAY"].upper() == "ON",
    }


def equivalent(a, b):
    return math.isclose(a, b, rel_tol=1e-6, abs_tol=1e-9) if type(a) is float else a == b


def write(transport, command):
    data = command.encode("ascii")
    if transport.bulk_write(data, DEFAULT_TIMEOUT_MS) != len(data):
        raise ProtocolError("Der Oszilloskopbefehl wurde nur teilweise übertragen.")
    # Readback after a 600 ms delay succeeded in the historical physical tests.
    time.sleep(0.6)


def configure(transport, desired):
    _, data, _ = header(transport)
    current = settings(data)
    changed = {key for key in current if not equivalent(current[key], desired[key])}
    if not changed:
        return
    if "Show1" in changed:
        raise ValueError("CH1 am Frontpanel ein- oder ausblenden.")
    if ("Show2" in changed and desired["Show2"] and data["RUNSTATUS"].upper() == "STOP"):
        raise ValueError("Vor dem Einblenden von CH2 die Erfassung starten.")
    supported_times = {0.0001: "100us", 0.0005: "500us", 0.001: "1.0ms"}
    supported_scales = {0.5: "500 mV", 1.0: "1.00 V"}
    supported_levels = {2.5: "+2.5001V", 3.5: "+3.5001V"}
    supported_couplings = {"AC": "AC", "DC": "DC", "GND": "Gnd"}
    trig_ch = desired["TriggerChannel"]
    if trig_ch not in (1, 2):
        raise ValueError("Triggerquelle muss CH1 oder CH2 sein.")
    commands = []
    if "Show2" in changed and desired["Show2"]:
        commands.append(":CH2:DISPlay on")
    try:
        if "TimePerDiv" in changed:
            commands.append(":HORIzontal:Scale " + supported_times[desired["TimePerDiv"]])
        for channel in (1, 2):
            scale = f"VoltsPerDiv{channel}"
            coupling = f"Coupling{channel}"
            if scale in changed:
                commands.append(f":CH{channel}:Scale " + supported_scales[desired[scale]])
            if coupling in changed:
                commands.append(f":CH{channel}:COUPling " + supported_couplings[desired[coupling]])
        if "TriggerChannel" in changed:
            commands.append(f":TRIGger:SINGle:EDGE:SOURce CH{trig_ch}")
        if "Rising" in changed:
            commands.append(":TRIGger:SINGle:EDGE " + ("RISE" if desired["Rising"] else "FALL"))
        if "TriggerLevel" in changed:
            commands.append(":TRIGger:SINGle:EDGe:LEVel " + supported_levels[desired["TriggerLevel"]])
    except KeyError as exc:
        raise ValueError("Einstellbare Werte: 100/500 µs oder 1 ms/div, 0,5 oder 1 V/div, AC/DC/GND sowie Triggerpegel 2,5 oder 3,5 V.") from exc
    if "Show2" in changed and not desired["Show2"]:
        commands.append(":CH2:DISPlay off")
    for command in commands:
        write(transport, command)
    _, data, _ = header(transport)
    actual = settings(data)
    unmatched = [key for key in current if not equivalent(actual[key], desired[key])]
    if unmatched:
        raise ValueError("Rücklesung weicht ab: " + ", ".join(unmatched) + ".")


def control(transport, action):
    _, data, _ = header(transport)
    if action == "Run":
        if data["Trig"].get("Sweep", "").upper() == "SINGLE":
            write(transport, ":TRIGger:SINGle:Sweep Auto")
        write(transport, ":RUNning RUN")
    elif action == "Stop":
        write(transport, ":RUNning STOP")
    elif action == "Single":
        write(transport, ":TRIGger:SINGle:Sweep Single")
        write(transport, ":RUNning RUN")
    else:
        raise ValueError("Unbekannte Oszilloskopaktion.")
    _, after, _ = header(transport)
    stopped = after["RUNSTATUS"].upper() == "STOP"
    if action == "Single":
        if after["Trig"].get("Sweep", "").upper() != "SINGLE":
            raise ValueError("Das Gerät meldet den Einzeltriggermodus nicht zurück.")
    elif stopped != (action == "Stop"):
        raise ValueError("Der rückgelesene Aufnahmestatus weicht von der Auswahl ab.")


def capture(transport):
    payload, data, _ = header(transport)
    active = [channel["NAME"] for channel in data["CHANNEL"] if channel["DISPLAY"].upper() == "ON"]
    if not active or any(name not in ("CH1", "CH2") for name in active) or len(active) != len(set(active)):
        raise ValueError("Mindestens ein gültiger Kanal muss am Oszilloskop sichtbar sein.")
    traces = []
    for name in active:
        parsed = parse_header(payload, channel_name=name)
        command = f":DATA:WAVE:SCREEN:{name}?\r\n".encode("ascii")
        block = _query(transport, command, DEFAULT_TIMEOUT_MS, parsed.payload_bytes)
        trace = decode_waveform(block, parsed)
        seconds, minimum, maximum = zip(*trace.rows)
        traces.append({
            "Channel": int(name[-1]),
            "Seconds": seconds,
            "Volts": [(lo + hi) / 2 for lo, hi in zip(minimum, maximum)],
            "MinimumVolts": minimum,
            "MaximumVolts": maximum,
        })
    return traces


def respond(result=None, error=None):
    print(json.dumps({"result": result, "error": error}, allow_nan=False), flush=True)


def session(transport):
    _, _, identity = header(transport)
    respond(identity)
    for line in sys.stdin:
        try:
            request = json.loads(line)
            operation = request["operation"]
            if operation == "identify":
                _, _, identity = header(transport)
                respond(identity)
            elif operation == "settings":
                _, data, _ = header(transport)
                respond(settings(data))
            elif operation == "configure":
                configure(transport, request["argument"])
                respond()
            elif operation == "control":
                control(transport, request["argument"])
                respond()
            elif operation == "capture":
                respond(capture(transport))
            else:
                raise ValueError("Unbekannte PeakTech-Operation.")
        except (OSError, ProtocolError, TypeError, KeyError) as exc:
            respond(error={"message": str(exc), "fatal": True})
            return
        except ValueError as exc:
            respond(error={"message": str(exc), "fatal": False})


def main():
    try:
        if sys.argv[1:] == ["discover"]:
            respond(discover_p1400())
        elif len(sys.argv) == 3 and sys.argv[1] == "session":
            with open_p1400(sys.argv[2]) as transport:
                session(transport)
        else:
            raise ValueError("Erwartet: discover oder session <USB-Seriennummer>.")
    except (OSError, ValueError) as exc:
        respond(error={"message": str(exc), "fatal": True})
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
