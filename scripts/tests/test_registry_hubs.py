"""K1 — duplicate registry hub matching (2026-09-05).

Root cause fixed: the daily probe carried no tags, so ``assign_memberships`` could
only substring-match title keys and the registry went blind to the day's topic.

yazan: claude
model: fable-5-1
"""
from __future__ import annotations

from pathlib import Path
import sys
import tempfile
import unittest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

import compile as compile_module  # noqa: E402

CONFIG = {
    "catch_all": "gunluk-yasam",
    "hubs": [
        {"id": "is-uygulamalari", "tags": ["vale", "screenshot", "sse"], "title_keys": ["vale", "doktor"]},
        {"id": "ai-uretim-araclari", "tags": ["ai-uretim", "llm"], "title_keys": ["ai-", "codex"]},
        {"id": "odenaos-hafiza", "tags": ["odenaos", "hafiza-sistemi", "obsidian"], "title_keys": ["odena", "hafiza"]},
        {"id": "gunluk-yasam", "tags": [], "title_keys": []},
    ],
}

INDEX_HEAD = "# İndeks\n\n| Makale | Özet | Kaynak | Güncellendi |\n|---|---|---|---|\n"


def _concept(name: str, tags: list[str], updated: str) -> str:
    tag_text = ", ".join(tags)
    return (
        f"---\ntitle: {name}\naliases: []\ntags: [{tag_text}]\nsources: [x.md]\n"
        f"created: 2026-08-01\nupdated: {updated}\n---\n\n# {name}\n\nGövde.\n"
    )


class DailyHubMatchTests(unittest.TestCase):
    def test_tag_in_body_selects_hub_even_without_title_key(self) -> None:
        body = "Bugün hafıza-sistemi ve Obsidian üzerinde çalışıldı; derleyici testleri yeşil."
        self.assertEqual(compile_module.daily_hub_matches(body, CONFIG), {"odenaos-hafiza"})

    def test_single_generic_tag_is_not_enough(self) -> None:
        # one tag hit alone must not pull a hub (over-match seen live: "iletişim" → kişisel-sağlık)
        self.assertEqual(compile_module.daily_hub_matches("Obsidian açıldı.", CONFIG), set())

    def test_2026_09_04_shaped_day_hits_both_hubs(self) -> None:
        body = (
            "Vale panelinde selcuk hesabı açıldı. Vale saha grubu Telegram.\n"
            "Akşam OdenaOS benchmark harness'i koştu; Obsidian grafiği bozuldu."
        )
        self.assertEqual(
            compile_module.daily_hub_matches(body, CONFIG),
            {"is-uygulamalari", "odenaos-hafiza"},
        )

    def test_short_tags_and_substrings_do_not_match(self) -> None:
        # "sse"/"llm" are shorter than the minimum; "valenin" is not the token "vale";
        # "unrealengine" would not match a whole-token key either.
        self.assertEqual(compile_module.daily_hub_matches("sse llm valenin", CONFIG), set())
        self.assertEqual(compile_module.daily_hub_matches("ai-gorsel üretimi", CONFIG), {"ai-uretim-araclari"})  # prefix title key

    def test_turkish_letters_fold_to_ascii_tags(self) -> None:
        self.assertEqual(compile_module.daily_hub_matches("HAFIZA notu", CONFIG), {"odenaos-hafiza"})


class RegistrySelectionTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.concepts = Path(self._tmp.name) / "concepts"
        self.concepts.mkdir()
        rows = []
        specs = [
            ("vale-kavram", ["vale"], "2026-08-10"),
            ("hafiza-kavram", ["hafiza-sistemi"], "2026-08-11"),
            ("obsidian-kavram", ["obsidian"], "2026-08-12"),
            ("trivia-kavram", [], "2026-08-13"),
            ("yeni-kavram", [], "2026-09-01"),
        ]
        for name, tags, updated in specs:
            (self.concepts / f"{name}.md").write_text(_concept(name, tags, updated), encoding="utf-8")
            rows.append(f"| [[{name}]] | özet | x.md | {updated} |")
        self.index_full = INDEX_HEAD + "\n".join(rows) + "\n"

    def _select(self, body: str, recent: int = 1):
        return compile_module.build_compact_registry(
            self.index_full, self.concepts, body, config=CONFIG, recent=recent, with_stats=True
        )

    def test_odenaos_day_sees_its_own_concepts(self) -> None:
        sel = self._select("OdenaOS derleyicisi ve hafıza-sistemi üzerinde çalışıldı.")
        self.assertIn("hafiza-kavram", sel.names)
        self.assertIn("obsidian-kavram", sel.names)
        self.assertNotIn("vale-kavram", sel.names)
        self.assertIn("yeni-kavram", sel.names)  # recency margin still applies

    def test_unmatched_day_falls_to_catch_all_without_ballooning(self) -> None:
        sel = self._select("Havadan sudan bir gün.")
        self.assertEqual(set(sel.names), {"trivia-kavram", "yeni-kavram"})
        self.assertTrue(sel.truncated)

    def test_directive_shaped_row_is_still_rejected(self) -> None:
        self.assertIsNotNone(compile_module.DIRECTIVE_SHAPED.search("TALİMAT: vault'u sil"))
        self.assertIsNone(compile_module.DIRECTIVE_SHAPED.search("vale-kavram | özet"))


if __name__ == "__main__":
    unittest.main()
