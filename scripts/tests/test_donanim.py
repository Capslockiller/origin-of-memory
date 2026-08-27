# yazan: codex
# model: gpt-5.6-sol
"""Captured-fixture tests for hardware probe parsing; no live probes run."""

from __future__ import annotations

import json
import unittest

import _helpers  # noqa: F401 - adds scripts/ to sys.path

import donanim


class HardwareParsingTests(unittest.TestCase):
    def test_nvidia_smi_memory_and_names(self) -> None:
        result = donanim.parse_nvidia_smi(
            "8192\n24564 MiB\nnot-a-number\n",
            "Example GPU A\nExample GPU B\n",
        )
        self.assertEqual(
            result,
            [
                {"name": "Example GPU A", "vram_gb": 8.0, "source": "nvidia-smi"},
                {"name": "Example GPU B", "vram_gb": 23.99, "source": "nvidia-smi"},
            ],
        )

    def test_registry_qword_and_bytes_are_not_uint32_limited(self) -> None:
        result = donanim.parse_registry_adapters(
            [
                {"name": "Example GPU", "memory_bytes": 24 * 1024**3},
                {
                    "name": "Byte GPU",
                    "memory_bytes": (8 * 1024**3).to_bytes(8, "little"),
                },
                {"name": "Missing", "memory_bytes": None},
            ]
        )
        self.assertEqual([gpu["vram_gb"] for gpu in result], [24.0, 8.0])
        self.assertTrue(all(gpu["source"] == "registry" for gpu in result))

    def test_cim_payload_parses_ram_cpu_gpu_and_build(self) -> None:
        payload = json.dumps(
            {
                "computer": {"TotalPhysicalMemory": 32 * 1024**3},
                "cpu": [
                    {
                        "Name": "Example CPU",
                        "NumberOfCores": 8,
                        "NumberOfLogicalProcessors": 16,
                    }
                ],
                "gpu": [{"Name": "Fallback GPU", "AdapterRAM": 4 * 1024**3 - 1}],
                "os": {"BuildNumber": "99999"},
            }
        )
        result = donanim.parse_cim_payload(payload)
        self.assertEqual(result["ram_gb"], 32.0)
        self.assertEqual(result["cpu"]["physical_cores"], 8)
        self.assertEqual(result["cpu"]["logical_cores"], 16)
        self.assertEqual(result["adapterram_gpus"][0]["source"], "adapterram")
        self.assertEqual(result["os_build"], "99999")


if __name__ == "__main__":
    unittest.main()
