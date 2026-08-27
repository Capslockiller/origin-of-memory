# yazan: codex
# model: gpt-5.6-sol
"""Synthetic-machine recommendation labels and catalogue contracts."""

from __future__ import annotations

import unittest

import _helpers  # noqa: F401 - adds scripts/ to sys.path

import model_oneri


def probe(ram: float | None, vram: float | None, disk: float = 100.0) -> dict:
    return {
        "ram_gb": ram,
        "gpus": [] if vram is None else [{"vram_gb": vram}],
        "free_disk_gb": disk,
    }


class RecommendationTests(unittest.TestCase):
    def test_synthetic_machine_table(self) -> None:
        cases = [
            ("no GPU", probe(16, None), "cpu-ok"),
            ("8 GB VRAM", probe(16, 8), "fits-gpu"),
            ("24 GB VRAM", probe(32, 24), "fits-gpu"),
            ("16 GB RAM", probe(16, None), "cpu-ok"),
            ("tiny disk does not alter memory fit", probe(16, 8, 0.1), "fits-gpu"),
        ]
        for name, machine, first_label in cases:
            with self.subTest(name=name):
                result = model_oneri.recommend(machine)
                self.assertEqual(result[0]["label"], first_label)
                self.assertNotIn("no-fit", [item["label"] for item in result])

    def test_tight_boundary(self) -> None:
        result = model_oneri.recommend(probe(4, 3.5))
        self.assertEqual(result[0]["tag"], "qwen3:4b")
        self.assertEqual(result[0]["label"], "tight")

    def test_nothing_fits_returns_only_smallest_no_fit(self) -> None:
        result = model_oneri.recommend(probe(2, None))
        self.assertEqual(len(result), 1)
        self.assertEqual(result[0]["tag"], "qwen3:4b")
        self.assertEqual(result[0]["label"], "no-fit")
        self.assertIn("No catalogue candidate fits", result[0]["why"])

    def test_catalogue_is_exactly_the_verified_tags(self) -> None:
        self.assertEqual(
            {item["tag"] for item in model_oneri.CATALOGUE},
            {
                "qwen3:4b",
                "qwen3:8b",
                "qwen3:14b",
                "qwen3:30b",
                "gemma3:4b",
                "gemma3:12b",
                "gemma3:27b",
            },
        )

    def test_pull_preflight_formula(self) -> None:
        self.assertEqual(model_oneri.pull_need_gb(5.2), 7.8)


if __name__ == "__main__":
    unittest.main()
