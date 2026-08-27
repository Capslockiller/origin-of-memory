# yazan: codex
# model: gpt-5.6-sol
"""Fault-tolerant, standard-library-only hardware probe.

The JSON contract is stable: ``ram_gb``, ``cpu``, ``gpus``,
``free_disk_gb``, ``model_store``, ``commands``, ``os_build``, and ``notes``.
Windows-specific probes are kept behind platform checks so this module remains
importable and useful on other operating systems.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
from typing import Any, Iterable


GIB = 1024**3
COMMANDS = ("ollama", "agy", "claude", "python")


def _round_gb(value: int | float) -> float:
    return round(float(value) / GIB, 2)


def parse_nvidia_smi(memory_text: str, names_text: str = "") -> list[dict[str, Any]]:
    """Parse captured ``nvidia-smi`` memory/name output without running it."""

    memories: list[float] = []
    for line in memory_text.splitlines():
        value = line.strip().split()[0] if line.strip() else ""
        try:
            memories.append(round(float(value) / 1024, 2))
        except (ValueError, IndexError):
            continue
    names = [line.strip() for line in names_text.splitlines() if line.strip()]
    return [
        {
            "name": names[index] if index < len(names) else None,
            "vram_gb": memory,
            "source": "nvidia-smi",
        }
        for index, memory in enumerate(memories)
    ]


def parse_registry_adapters(records: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    """Normalise captured display-class registry records."""

    adapters: list[dict[str, Any]] = []
    for record in records:
        raw_size = record.get("memory_bytes")
        if isinstance(raw_size, bytes):
            raw_size = int.from_bytes(raw_size[:8], "little")
        if not isinstance(raw_size, (int, float)) or raw_size <= 0:
            continue
        adapters.append(
            {
                "name": record.get("name") or None,
                "vram_gb": _round_gb(raw_size),
                "source": "registry",
            }
        )
    return adapters


def parse_cim_payload(payload: str) -> dict[str, Any]:
    """Parse the captured JSON emitted by the PowerShell CIM probe."""

    raw = json.loads(payload)
    cpu_rows = raw.get("cpu") or []
    gpu_rows = raw.get("gpu") or []
    if isinstance(cpu_rows, dict):
        cpu_rows = [cpu_rows]
    if isinstance(gpu_rows, dict):
        gpu_rows = [gpu_rows]

    physical = [row.get("NumberOfCores") for row in cpu_rows]
    logical = [row.get("NumberOfLogicalProcessors") for row in cpu_rows]
    names = [str(row.get("Name", "")).strip() for row in cpu_rows]
    valid_physical = [value for value in physical if isinstance(value, (int, float))]
    valid_logical = [value for value in logical if isinstance(value, (int, float))]
    computer = raw.get("computer") or {}
    operating_system = raw.get("os") or {}
    return {
        "ram_gb": (
            _round_gb(computer["TotalPhysicalMemory"])
            if isinstance(computer.get("TotalPhysicalMemory"), (int, float))
            else None
        ),
        "cpu": {
            "name": " / ".join(name for name in names if name) or None,
            "physical_cores": int(sum(valid_physical)) if valid_physical else None,
            "logical_cores": int(sum(valid_logical)) if valid_logical else None,
        },
        "adapterram_gpus": [
            {
                "name": row.get("Name") or None,
                "vram_gb": _round_gb(row["AdapterRAM"]),
                "source": "adapterram",
            }
            for row in gpu_rows
            if isinstance(row.get("AdapterRAM"), (int, float))
            and row["AdapterRAM"] > 0
        ],
        "os_build": (
            str(operating_system.get("BuildNumber"))
            if operating_system.get("BuildNumber") is not None
            else None
        ),
    }


def _run(command: list[str]) -> str:
    completed = subprocess.run(
        command,
        check=True,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=12,
    )
    return completed.stdout


def _probe_nvidia() -> list[dict[str, Any]]:
    executable = shutil.which("nvidia-smi")
    if not executable:
        return []
    memory = _run(
        [
            executable,
            "--query-gpu=memory.total",
            "--format=csv,noheader,nounits",
        ]
    )
    try:
        names = _run(
            [executable, "--query-gpu=name", "--format=csv,noheader"]
        )
    except Exception:
        names = ""
    return parse_nvidia_smi(memory, names)


def _probe_registry() -> list[dict[str, Any]]:
    if os.name != "nt":
        return []
    import winreg  # type: ignore[import-not-found]

    class_path = (
        "SYSTEM\\CurrentControlSet\\Control\\Class\\"
        "{4d36e968-e325-11ce-bfc1-08002be10318}"
    )
    records: list[dict[str, Any]] = []
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, class_path) as root:
        index = 0
        while True:
            try:
                subkey_name = winreg.EnumKey(root, index)
            except OSError:
                break
            index += 1
            if not (len(subkey_name) == 4 and subkey_name.isdigit()):
                continue
            try:
                with winreg.OpenKey(root, subkey_name) as subkey:
                    size = winreg.QueryValueEx(
                        subkey, "HardwareInformation.qwMemorySize"
                    )[0]
                    name = None
                    for value_name in (
                        "DriverDesc",
                        "HardwareInformation.AdapterString",
                    ):
                        try:
                            name = winreg.QueryValueEx(subkey, value_name)[0]
                            if name:
                                break
                        except OSError:
                            continue
                    records.append({"name": name, "memory_bytes": size})
            except OSError:
                continue
    return parse_registry_adapters(records)


def _probe_cim() -> dict[str, Any]:
    script = """
$result = [ordered]@{
  computer = Get-CimInstance Win32_ComputerSystem | Select-Object TotalPhysicalMemory
  cpu = @(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)
  gpu = @(Get-CimInstance Win32_VideoController | Select-Object Name,AdapterRAM)
  os = Get-CimInstance Win32_OperatingSystem | Select-Object BuildNumber
}
$result | ConvertTo-Json -Depth 5 -Compress
"""
    return parse_cim_payload(
        _run(
            [
                "powershell",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                script,
            ]
        )
    )


def _model_store() -> Path:
    configured = os.environ.get("OLLAMA_MODELS")
    if configured:
        return Path(os.path.expandvars(configured)).expanduser()
    return Path.home() / ".ollama" / "models"


def _windows_fallbacks() -> dict[str, Any]:
    """Use Win32 APIs/registry when CIM is unavailable or partially blocked."""

    import ctypes
    import winreg  # type: ignore[import-not-found]

    class MemoryStatus(ctypes.Structure):
        _fields_ = [
            ("length", ctypes.c_ulong),
            ("memory_load", ctypes.c_ulong),
            ("total_physical", ctypes.c_ulonglong),
            ("available_physical", ctypes.c_ulonglong),
            ("total_page_file", ctypes.c_ulonglong),
            ("available_page_file", ctypes.c_ulonglong),
            ("total_virtual", ctypes.c_ulonglong),
            ("available_virtual", ctypes.c_ulonglong),
            ("available_extended_virtual", ctypes.c_ulonglong),
        ]

    status = MemoryStatus()
    status.length = ctypes.sizeof(MemoryStatus)
    ram_gb = None
    if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
        ram_gb = _round_gb(status.total_physical)

    physical_cores = None
    try:
        length = ctypes.c_ulong(0)
        api = ctypes.windll.kernel32.GetLogicalProcessorInformationEx
        api(0, None, ctypes.byref(length))  # first call obtains buffer length
        buffer = ctypes.create_string_buffer(length.value)
        if api(0, buffer, ctypes.byref(length)):
            offset = 0
            count = 0
            while offset + 8 <= length.value:
                relationship = int.from_bytes(buffer.raw[offset : offset + 4], "little")
                record_size = int.from_bytes(buffer.raw[offset + 4 : offset + 8], "little")
                if record_size < 8:
                    break
                if relationship == 0:  # RelationProcessorCore
                    count += 1
                offset += record_size
            physical_cores = count or None
    except (AttributeError, OSError):
        pass

    cpu_name = None
    try:
        with winreg.OpenKey(
            winreg.HKEY_LOCAL_MACHINE,
            r"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
        ) as key:
            cpu_name = str(winreg.QueryValueEx(key, "ProcessorNameString")[0]).strip()
    except OSError:
        pass

    os_build = None
    try:
        with winreg.OpenKey(
            winreg.HKEY_LOCAL_MACHINE,
            r"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
        ) as key:
            os_build = str(winreg.QueryValueEx(key, "CurrentBuildNumber")[0])
    except OSError:
        pass
    return {
        "ram_gb": ram_gb,
        "cpu_name": cpu_name,
        "physical_cores": physical_cores,
        "logical_cores": os.cpu_count(),
        "os_build": os_build,
    }


def _free_disk_gb(store: Path) -> float:
    if os.name != "nt":
        anchor = store
        while not anchor.exists() and anchor != anchor.parent:
            anchor = anchor.parent
        return _round_gb(shutil.disk_usage(anchor).free)

    import ctypes

    expanded = os.path.abspath(str(store))
    drive, _ = os.path.splitdrive(expanded)
    root = drive + "\\" if drive else expanded
    free_for_user = ctypes.c_ulonglong()
    total = ctypes.c_ulonglong()
    total_free = ctypes.c_ulonglong()
    success = ctypes.windll.kernel32.GetDiskFreeSpaceExW(
        ctypes.c_wchar_p(root),
        ctypes.byref(free_for_user),
        ctypes.byref(total),
        ctypes.byref(total_free),
    )
    if not success:
        raise OSError(ctypes.get_last_error(), f"disk probe failed for {root}")
    return _round_gb(free_for_user.value)


def collect_probe() -> dict[str, Any]:
    """Collect each field independently; failures become notes, never errors."""

    notes: list[str] = []
    result: dict[str, Any] = {
        "ram_gb": None,
        "cpu": {
            "name": None,
            "physical_cores": None,
            "logical_cores": os.cpu_count(),
        },
        "gpus": [],
        "free_disk_gb": None,
        "model_store": str(_model_store()),
        "commands": {name: shutil.which(name) is not None for name in COMMANDS},
        "os_build": platform.version() or None,
        "notes": notes,
    }

    cim: dict[str, Any] = {}
    if os.name == "nt":
        try:
            cim = _probe_cim()
            result["ram_gb"] = cim["ram_gb"]
            result["cpu"] = cim["cpu"]
            result["os_build"] = cim["os_build"]
        except Exception as exc:  # probe boundary: intentionally fail-soft
            notes.append(f"CIM probe unavailable: {exc}")
        try:
            fallback = _windows_fallbacks()
            if result["ram_gb"] is None:
                result["ram_gb"] = fallback["ram_gb"]
            if result["cpu"]["name"] is None:
                result["cpu"]["name"] = fallback["cpu_name"]
            if result["cpu"]["physical_cores"] is None:
                result["cpu"]["physical_cores"] = fallback["physical_cores"]
            if result["cpu"]["logical_cores"] is None:
                result["cpu"]["logical_cores"] = fallback["logical_cores"]
            if result["os_build"] is None:
                result["os_build"] = fallback["os_build"]
        except Exception as exc:
            notes.append(f"Win32 fallback probe unavailable: {exc}")
    else:
        notes.append("Windows CIM and registry probes were skipped on this OS.")

    if result["ram_gb"] is None:
        notes.append("Total RAM is unknown.")
    if result["cpu"]["name"] is None:
        result["cpu"]["name"] = platform.processor() or None
    if result["cpu"]["physical_cores"] is None:
        notes.append("CPU physical core count is unknown.")
    if result["cpu"]["logical_cores"] is None:
        notes.append("CPU logical core count is unknown.")
    if result["os_build"] is None:
        notes.append("OS build is unknown.")

    try:
        result["gpus"] = _probe_nvidia()
    except Exception as exc:
        notes.append(f"nvidia-smi probe unavailable: {exc}")
    if not result["gpus"] and os.name == "nt":
        try:
            result["gpus"] = _probe_registry()
        except Exception as exc:
            notes.append(f"Display registry probe unavailable: {exc}")
    if not result["gpus"] and cim.get("adapterram_gpus"):
        result["gpus"] = cim["adapterram_gpus"]
        notes.append("AdapterRAM may be wrong above 4 GB.")
    if not result["gpus"]:
        notes.append("GPU VRAM is unknown or no supported GPU was found.")
    elif any(gpu.get("name") is None for gpu in result["gpus"]):
        notes.append("One or more GPU names are unknown.")

    try:
        result["free_disk_gb"] = _free_disk_gb(_model_store())
    except Exception as exc:
        notes.append(f"Model-store disk space is unknown: {exc}")

    return result


def _format_table(probe: dict[str, Any]) -> str:
    cpu = probe["cpu"]
    physical = cpu["physical_cores"] if cpu["physical_cores"] is not None else "unknown"
    logical = cpu["logical_cores"] if cpu["logical_cores"] is not None else "unknown"
    rows = [
        ("RAM", f"{probe['ram_gb']:.2f} GB" if probe["ram_gb"] is not None else "unknown"),
        ("CPU", cpu["name"] or "unknown"),
        ("CPU cores", f"{physical} physical / {logical} logical"),
        ("OS build", probe["os_build"] or "unknown"),
        ("Model store", probe["model_store"]),
        ("Free disk", f"{probe['free_disk_gb']:.2f} GB" if probe["free_disk_gb"] is not None else "unknown"),
    ]
    for index, gpu in enumerate(probe["gpus"], start=1):
        rows.append(
            (
                f"GPU {index}",
                f"{gpu['name'] or 'unknown'} / {gpu['vram_gb']:.2f} GB ({gpu['source']})",
            )
        )
    rows.append(
        (
            "PATH",
            ", ".join(
                f"{name}={'yes' if found else 'no'}"
                for name, found in probe["commands"].items()
            ),
        )
    )
    width = max(len(label) for label, _ in rows)
    lines = [f"{label:<{width}}  {value}" for label, value in rows]
    if probe["notes"]:
        lines.append("Notes")
        lines.extend(f"  - {note}" for note in probe["notes"])
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Probe local model hardware")
    parser.add_argument("--json", action="store_true", help="emit stable JSON")
    args = parser.parse_args(argv)
    probe = collect_probe()
    if args.json:
        print(json.dumps(probe, ensure_ascii=False, indent=2))
    else:
        print(_format_table(probe))
    return 0


if __name__ == "__main__":
    raise SystemExit(0 if os.environ.get("BEYIN_INVOKED_BY") else main())
