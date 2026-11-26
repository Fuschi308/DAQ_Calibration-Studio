"""Windows transport for the installed libusb-win32 driver."""

from __future__ import annotations

import ctypes as c
import logging
import os
from collections.abc import Iterator
from contextlib import contextmanager


VID = 0x5345
PID = 0x1234
OUT_ENDPOINT = 0x01
IN_ENDPOINT = 0x81
CONFIGURATION = 1
BULK_TYPE = 2
LOG = logging.getLogger("peaktech.p1400.usb")


class UsbError(OSError):
    """The installed USB backend could not complete an operation."""


# Packed ABI from lusb0_usb.h. Trailing fields preserve sizes for array indexing.
class _Packed(c.Structure):
    _layout_ = "ms"
    _pack_ = 1


class _Endpoint(_Packed):
    _fields_ = [
        ("bLength", c.c_uint8), ("bDescriptorType", c.c_uint8), ("bEndpointAddress", c.c_uint8),
        ("bmAttributes", c.c_uint8), ("wMaxPacketSize", c.c_uint16), ("bInterval", c.c_uint8),
        ("bRefresh", c.c_uint8), ("bSynchAddress", c.c_uint8),
        ("extra", c.c_void_p), ("extralen", c.c_int),
    ]


class _AltSetting(_Packed):
    _fields_ = [
        ("bLength", c.c_uint8), ("bDescriptorType", c.c_uint8), ("bInterfaceNumber", c.c_uint8),
        ("bAlternateSetting", c.c_uint8), ("bNumEndpoints", c.c_uint8), ("bInterfaceClass", c.c_uint8),
        ("bInterfaceSubClass", c.c_uint8), ("bInterfaceProtocol", c.c_uint8), ("iInterface", c.c_uint8),
        ("endpoint", c.POINTER(_Endpoint)), ("extra", c.c_void_p), ("extralen", c.c_int),
    ]


class _Interface(_Packed):
    _fields_ = [("altsetting", c.POINTER(_AltSetting)), ("num_altsetting", c.c_int)]


class _Config(_Packed):
    _fields_ = [
        ("bLength", c.c_uint8), ("bDescriptorType", c.c_uint8), ("wTotalLength", c.c_uint16),
        ("bNumInterfaces", c.c_uint8), ("bConfigurationValue", c.c_uint8), ("iConfiguration", c.c_uint8),
        ("bmAttributes", c.c_uint8), ("MaxPower", c.c_uint8),
        ("interface", c.POINTER(_Interface)), ("extra", c.c_void_p), ("extralen", c.c_int),
    ]


class _DeviceDescriptor(_Packed):
    _fields_ = [
        ("bLength", c.c_uint8), ("bDescriptorType", c.c_uint8), ("bcdUSB", c.c_uint16),
        ("bDeviceClass", c.c_uint8), ("bDeviceSubClass", c.c_uint8), ("bDeviceProtocol", c.c_uint8),
        ("bMaxPacketSize0", c.c_uint8), ("idVendor", c.c_uint16), ("idProduct", c.c_uint16),
        ("bcdDevice", c.c_uint16), ("iManufacturer", c.c_uint8), ("iProduct", c.c_uint8),
        ("iSerialNumber", c.c_uint8), ("bNumConfigurations", c.c_uint8),
    ]


class _Device(_Packed):
    pass


class _Bus(_Packed):
    pass


_Device._fields_ = [
    ("next", c.POINTER(_Device)), ("prev", c.POINTER(_Device)), ("filename", c.c_char * 512),
    ("bus", c.POINTER(_Bus)), ("descriptor", _DeviceDescriptor), ("config", c.POINTER(_Config)),
    ("dev", c.c_void_p), ("devnum", c.c_uint8), ("num_children", c.c_uint8),
    ("children", c.POINTER(c.POINTER(_Device))),
]
_Bus._fields_ = [
    ("next", c.POINTER(_Bus)), ("prev", c.POINTER(_Bus)), ("dirname", c.c_char * 512),
    ("devices", c.POINTER(_Device)), ("location", c.c_uint32), ("root_dev", c.POINTER(_Device)),
]


def _load_dll() -> c.CDLL:
    if os.name != "nt":
        raise UsbError("Der USB-Abruf benötigt Windows mit installiertem libusb-win32-Treiber.")
    try:
        # LOAD_LIBRARY_SEARCH_SYSTEM32 selects the Windows system DLL for this process.
        dll = c.CDLL("libusb0.dll", winmode=0x800)
    except OSError as exc:
        raise UsbError(f"Die passende libusb0.dll konnte nicht aus Windows geladen werden ({exc}).") from exc

    handle = c.c_void_p
    signatures = [
        ("usb_init", None, ()),
        ("usb_find_busses", c.c_int, ()),
        ("usb_find_devices", c.c_int, ()),
        ("usb_get_busses", c.POINTER(_Bus), ()),
        ("usb_open", handle, (c.POINTER(_Device),)),
        ("usb_close", c.c_int, (handle,)),
        ("usb_get_string_simple", c.c_int, (handle, c.c_int, c.c_void_p, c.c_size_t)),
        ("usb_claim_interface", c.c_int, (handle, c.c_int)),
        ("usb_release_interface", c.c_int, (handle, c.c_int)),
        ("usb_bulk_write", c.c_int, (handle, c.c_int, c.c_char_p, c.c_int, c.c_int)),
        ("usb_bulk_read", c.c_int, (handle, c.c_int, c.c_void_p, c.c_int, c.c_int)),
        ("usb_strerror", c.c_char_p, ()),
    ]
    for name, result, parameters in signatures:
        try:
            function = getattr(dll, name)
        except AttributeError as exc:
            raise UsbError(f"In der System-DLL libusb0.dll fehlt {name}.") from exc
        function.argtypes = parameters
        function.restype = result
    return dll


def _error(dll: c.CDLL, operation: str, result: int | None = None) -> str:
    detail = (dll.usb_strerror() or b"unbekannter Fehler").decode("ascii", errors="replace").strip()
    code = "" if result is None else f", libusb0 {result}"
    return f"{operation} fehlgeschlagen ({detail}{code})."


def _check(dll: c.CDLL, result: int, operation: str) -> int:
    if result < 0:
        raise UsbError(_error(dll, operation, result))
    return result


def _find_devices(dll: c.CDLL) -> list[c.POINTER(_Device)]:
    dll.usb_init()
    _check(dll, dll.usb_find_busses(), "USB-Bus-Suche")
    _check(dll, dll.usb_find_devices(), "USB-Gerätesuche")
    matches = []
    bus = dll.usb_get_busses()
    while bus:
        device = bus.contents.devices
        while device:
            descriptor = device.contents.descriptor
            if (descriptor.idVendor, descriptor.idProduct) == (VID, PID):
                matches.append(device)
            device = device.contents.next
        bus = bus.contents.next
    return matches


def _find_device(dll: c.CDLL) -> c.POINTER(_Device):
    matches = _find_devices(dll)
    if len(matches) != 1:
        raise UsbError(f"Genau ein Gerät mit {VID:04X}:{PID:04X} erwartet, {len(matches)} gefunden.")
    return matches[0]


def _find_interface(device: _Device) -> int:
    matches = []
    if device.config:
        for config_index in range(device.descriptor.bNumConfigurations):
            config = device.config[config_index]
            if config.bConfigurationValue != CONFIGURATION or not config.interface:
                continue
            for interface_index in range(config.bNumInterfaces):
                interface = config.interface[interface_index]
                if not interface.altsetting:
                    continue
                for alternate_index in range(interface.num_altsetting):
                    alternate = interface.altsetting[alternate_index]
                    if alternate.bAlternateSetting != 0 or not alternate.endpoint:
                        continue
                    endpoints = {
                        endpoint.bEndpointAddress
                        for endpoint in (alternate.endpoint[i] for i in range(alternate.bNumEndpoints))
                        if (endpoint.bmAttributes & 0x03) == BULK_TYPE
                    }
                    if {OUT_ENDPOINT, IN_ENDPOINT} <= endpoints:
                        matches.append(alternate.bInterfaceNumber)
    if len(matches) != 1:
        raise UsbError("Konfiguration 1 muss 0x01 und 0x81 im selben Interface mit Alternate Setting 0 melden.")
    return matches[0]


class _BulkSession:
    def __init__(self, dll: c.CDLL, handle: int) -> None:
        self.dll = dll
        self.handle: int | None = handle

    def _open_handle(self) -> int:
        if self.handle is None:
            raise UsbError("Die USB-Verbindung ist geschlossen.")
        return self.handle

    def bulk_write(self, data: bytes, timeout_ms: int) -> int:
        result = self.dll.usb_bulk_write(self._open_handle(), OUT_ENDPOINT, data, len(data), timeout_ms)
        return _check(self.dll, result, "Bulk OUT 0x01")

    def bulk_read(self, size: int, timeout_ms: int) -> bytes:
        buffer = c.create_string_buffer(size)
        result = self.dll.usb_bulk_read(self._open_handle(), IN_ENDPOINT, buffer, size, timeout_ms)
        count = _check(self.dll, result, "Bulk IN 0x81")
        if count > size:
            raise UsbError(f"Bulk IN meldet {count} Byte bei einem {size}-Byte-Puffer.")
        return buffer.raw[:count]


def discover_p1400() -> list[str]:
    """Enumerate the descriptor without claiming the scope interface."""
    dll = _load_dll()
    matches = _find_devices(dll)
    if len(matches) > 1:
        raise UsbError("Mehrere P-1400-Geräte erkannt. Für diese Sitzung nur das Zielgerät anschließen.")
    if not matches:
        return []
    device = matches[0]
    _find_interface(device.contents)
    serial_index = device.contents.descriptor.iSerialNumber
    if not serial_index:
        raise UsbError("Im USB-Gerätedeskriptor fehlt der Serienstring-Index.")
    handle = dll.usb_open(device)
    if not handle:
        raise UsbError(_error(dll, "USB-Gerät öffnen"))
    try:
        buffer = c.create_string_buffer(256)
        count = _check(dll, dll.usb_get_string_simple(handle, serial_index, buffer, len(buffer)),
                       "USB-Serienstring lesen")
        return [buffer.raw[:count].decode("ascii")]
    finally:
        dll.usb_close(handle)


@contextmanager
def open_p1400(expected_serial: str) -> Iterator[_BulkSession]:
    dll = _load_dll()
    device = _find_device(dll)
    interface = _find_interface(device.contents)
    serial_index = device.contents.descriptor.iSerialNumber
    if not serial_index:
        raise UsbError("Im USB-Gerätedeskriptor fehlt der Serienstring-Index.")

    handle = dll.usb_open(device)
    if not handle:
        raise UsbError(_error(dll, "USB-Gerät öffnen"))
    try:
        LOG.debug("USB GET_DESCRIPTOR für Stringindex 0 und %d (Serienstring)", serial_index)
        buffer = c.create_string_buffer(256)
        result = dll.usb_get_string_simple(handle, serial_index, buffer, len(buffer))
        count = _check(dll, result, "USB-Serienstring lesen")
        serial = buffer.raw[:count].decode("ascii", errors="replace")
        if serial != expected_serial:
            raise UsbError(f"USB-Serienstring {serial!r}, erwartet wurde {expected_serial!r}.")

        result = dll.usb_claim_interface(handle, interface)
        _check(dll, result, f"USB-Interface {interface} beanspruchen")
        LOG.info("USB %04X:%04X · %s · Interface %d", VID, PID, serial, interface)
        transport = _BulkSession(dll, handle)
        try:
            yield transport
        finally:
            transport.handle = None
            result = dll.usb_release_interface(handle, interface)
            if result < 0:
                LOG.warning("%s", _error(dll, f"USB-Interface {interface} freigeben", result))
            else:
                LOG.debug("USB-Interface %d freigegeben", interface)
    finally:
        result = dll.usb_close(handle)
        if result < 0:
            LOG.warning("%s", _error(dll, "USB-Gerät schließen", result))
        else:
            LOG.debug("USB-Gerätehandle geschlossen")
