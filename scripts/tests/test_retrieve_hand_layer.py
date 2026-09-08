"""Phase 1 Lane C: hand authority, corrections, refresh, and caller gate."""

# yazan: codex · gpt-5.6-sol

from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import sqlite3
import tempfile
import time
import unittest

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler

import duzelt
import retrieve
from test_retrieve import RetrieveHarness


FIXTURE = Path(__file__).parent / "fixtures" / "retrieval_phase1"


class FixtureVaultTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name) / "vault"
        shutil.copytree(FIXTURE / "vault", self.root)
        self.state = self.root / ".claude" / "scripts" / ".state"
        self.db = self.state / retrieve.DB_NAME
        retrieve.build_index(vault_root=self.root, state_dir=self.state)

    def test_episdodic_fixture_recall_is_at_least_seven_of_ten(self) -> None:
        cases = json.loads(
            (FIXTURE / "episodic_questions.json").read_text(encoding="utf-8")
        )["questions"]
        found = 0
        for case in cases:
            hits = retrieve.search(
                case["q"], limit=3, db_path=self.db, vault_root=self.root
            )
            if any(
                hit.source == retrieve.SOURCE_HAND
                and hit.source_file == case["file"]
                and case["answer"] in hit.body
                for hit in hits
            ):
                found += 1
        self.assertGreaterEqual(found, 7, f"episodic recall was {found}/10")

    def test_corrected_speaking_fact_is_first_ahead_of_stale_concept(self) -> None:
        hits = retrieve.search(
            "Speaking için yeni tarih alındı mı ve ücret kaç euro?",
            limit=3,
            db_path=self.db,
            vault_root=self.root,
        )

        self.assertEqual(hits[0].source, retrieve.SOURCE_HAND)
        self.assertIn("18 Eylül 2026", hits[0].body)
        self.assertIn("40 euro", [hit.body for hit in hits if hit.source == "concept"][0])

    def test_hand_injection_has_provenance_and_date_header(self) -> None:
        result = retrieve.hook_result(
            "Speaking yeni tarih ücret bilgisi",
            db_path=self.db,
            vault_root=self.root,
            require_overlap=True,
        )

        self.assertTrue(result["notes"])
        self.assertTrue(
            result["notes"][0]["body"].startswith(
                "[el katmanı · Threads.md › Active › Speaking sınavı tarih ücret · "
                "2026-09-18]"
            )
        )

    def test_audit_machine_prompts_produce_zero_injections(self) -> None:
        prompts = json.loads(
            (FIXTURE / "machine_prompts.json").read_text(encoding="utf-8")
        )["prompts"]
        for index, prompt in enumerate(prompts):
            with self.subTest(prompt=prompt):
                raw = json.dumps({"prompt": prompt, "session_id": f"machine-{index}"})
                self.assertIsNone(
                    retrieve.run_hook_stdin(
                        raw,
                        db_path=self.db,
                        state_dir=self.state,
                        environ={},
                    )
                )


class HandParserTests(RetrieveHarness):
    def setUp(self) -> None:
        super().setUp()
        self.companion = self.root / "demo-850-Companion"
        self.companion.mkdir()

    def test_heading_split_tags_dates_and_passage_caps(self) -> None:
        long_bullets = "\n".join(f"- madde {index} " + "x" * 90 for index in range(30))
        path = self.companion / "Threads.md"
        path.write_text(
            "## Active\n\n### Uzun Bölüm\n\n"
            "**Durum:** [[Deneme Konusu]] 2026-04-03\n\n"
            + long_bullets,
            encoding="utf-8",
        )

        notes = retrieve.read_hand_file(path)

        self.assertGreater(len(notes), 1)
        self.assertTrue(all(len(note.body) <= retrieve.HAND_PASSAGE_CAP for note in notes))
        self.assertTrue(all(note.heading == "Active › Uzun Bölüm" for note in notes))
        self.assertTrue(all(note.source_date == "2026-04-03" for note in notes))
        self.assertIn("Deneme Konusu", notes[0].tags)
        self.assertIn("Durum", notes[0].tags)

    def test_yenile_replaces_only_hand_rows_and_updates_stamps(self) -> None:
        self.write_note("kalici", title="Kalıcı kavram")
        threads = self.companion / "Threads.md"
        threads.write_text("## Active\n\n### Eski başlık\n\nEski içerik.", encoding="utf-8")
        self.build()
        threads.write_text(
            "## Active\n\n### Yeni başlık\n\nYeni içerik 2026-09-08.", encoding="utf-8"
        )
        future = time.time_ns() + 2_000_000_000
        os.utime(threads, ns=(future, future))

        self.assertTrue(retrieve.hand_refresh_needed(self.root, self.db))
        report = retrieve.refresh_hand_index(
            vault_root=self.root, state_dir=self.state
        )
        hits = retrieve.search("yeni başlık", db_path=self.db, vault_root=self.root)

        self.assertEqual(report["hand_count"], 1)
        self.assertEqual(hits[0].source, retrieve.SOURCE_HAND)
        connection = sqlite3.connect(self.db)
        try:
            self.assertEqual(
                connection.execute(
                    "SELECT count(*) FROM documents WHERE name = 'kalici'"
                ).fetchone()[0],
                1,
            )
        finally:
            connection.close()
        self.assertFalse(retrieve.hand_refresh_needed(self.root, self.db))

    def test_hook_automatically_refreshes_a_newer_hand_file(self) -> None:
        threads = self.companion / "Threads.md"
        threads.write_text(
            "## Active\n\n### Önceki kayıt başlığı\n\nÖnceki kayıt gövdesi.",
            encoding="utf-8",
        )
        self.build()
        threads.write_text(
            "## Active\n\n### Fener deneyi sonucu\n\n"
            "**Sonuç:** Fener deneyi sonucu başarılı olarak kaydedildi. 2026-09-08",
            encoding="utf-8",
        )
        future = time.time_ns() + 2_000_000_000
        os.utime(threads, ns=(future, future))

        raw = json.dumps(
            {"prompt": "Fener deneyi sonuç kaydı", "session_id": "auto-refresh"}
        )
        output = retrieve.run_hook_stdin(
            raw, db_path=self.db, state_dir=self.state, environ={}
        )

        self.assertIsNotNone(output)
        self.assertIn("başarılı olarak kaydedildi", output)

    def test_scoped_authority_ratio_does_not_promote_every_hand_hit(self) -> None:
        hits = [
            retrieve.SearchHit("concept", "Concept", "", -10.0),
            retrieve.SearchHit(
                "strong-hand", "Strong", "", -7.0, source=retrieve.SOURCE_HAND
            ),
            retrieve.SearchHit(
                "weak-hand", "Weak", "", -5.0, source=retrieve.SOURCE_HAND
            ),
        ]

        ordered = retrieve._authority_order(hits, ratio=0.6)

        self.assertEqual(
            [hit.name for hit in ordered], ["strong-hand", "concept", "weak-hand"]
        )

    def test_hand_only_refresh_stays_under_one_second_at_roughly_110_kb(self) -> None:
        threads = self.companion / "Threads.md"
        sections = [
            f"## Kayıt {index}\n\n**Durum:** sentetik içerik " + ("x" * 880)
            for index in range(120)
        ]
        threads.write_text("\n\n".join(sections), encoding="utf-8")
        self.assertGreater(threads.stat().st_size, 100_000)
        self.build()

        started = time.perf_counter()
        retrieve.refresh_hand_index(vault_root=self.root, state_dir=self.state)
        elapsed = time.perf_counter() - started

        self.assertLess(elapsed, 1.0, f"110 KB hand refresh took {elapsed:.3f}s")


class CorrectionContractTests(RetrieveHarness):
    """The ledger's grammar belongs to lane B; retrieve only reads it.

    Every block below is written the way ``duzelt.render`` writes one, so a
    change to that grammar breaks these tests instead of silently turning the
    exclusion off in production.
    """

    def setUp(self) -> None:
        super().setUp()
        self.companion = self.root / "demo-850-Companion"
        self.companion.mkdir()

    def write_ledger(self, *blocks: str) -> None:
        (self.companion / retrieve.CORRECTION_FILE).write_text(
            "---\ntitle: Düzeltmeler\ntype: duzeltme-defteri\n---\n\n"
            + "\n".join(blocks),
            encoding="utf-8",
        )

    def test_blocks_are_parsed_through_duzelt_not_a_local_regex(self) -> None:
        record = duzelt.Duzeltme(
            kavram="teslim-tarihi",
            iddia="Teslim tarihi 1 Mayıs.",
            dogru="Teslim tarihi 9 Haziran olarak düzeltildi.",
            ts="2026-09-08T10:00:00+03:00",
            kaynak="daily/2026-09-08.md",
        )
        self.write_ledger(duzelt.render(record) + "\n")

        corrections = retrieve.read_corrections(self.root)

        self.assertEqual(set(corrections), {"teslim-tarihi"})
        self.assertTrue(corrections["teslim-tarihi"].pending)
        self.assertEqual(
            corrections["teslim-tarihi"].dogru,
            "Teslim tarihi 9 Haziran olarak düzeltildi.",
        )
        self.assertEqual(corrections["teslim-tarihi"].iddia, "Teslim tarihi 1 Mayıs.")

    def test_correction_replaces_target_and_logs_exclusion(self) -> None:
        self.write_note(
            "teslim-tarihi",
            title="Proje teslim tarihi",
            tags=("proje", "teslim", "tarih"),
            body="Teslim tarihi 1 Mayıs.",
        )
        self.write_ledger(
            "<!-- duzeltme kavram=teslim-tarihi durum=bekliyor "
            "ts=2026-09-08T10:00:00+03:00 kaynak=daily/2026-09-08.md -->\n"
            "iddia: Teslim tarihi 1 Mayıs.\n"
            "dogru: Teslim tarihi 9 Haziran olarak düzeltildi.\n\n"
        )
        self.build()

        result = retrieve.hook_result(
            "Proje teslim tarihi nedir",
            session="duzeltme",
            db_path=self.db,
            state_dir=self.state,
            vault_root=self.root,
            require_overlap=True,
        )

        self.assertEqual(len(result["notes"]), 1)
        self.assertEqual(result["notes"][0]["name"], "correction:teslim-tarihi")
        self.assertEqual(
            result["notes"][0]["body"],
            "[düzeltme · teslim-tarihi]\nTeslim tarihi 9 Haziran olarak düzeltildi.",
        )
        ledger = json.loads(
            (self.state / "retrieve-session-duzeltme.json").read_text(encoding="utf-8")
        )
        self.assertEqual(ledger["decisions"][0]["reason"], retrieve.REASON_EXCLUSION)
        self.assertEqual(ledger["decisions"][0]["notes"], ["teslim-tarihi"])

    def test_applied_target_whose_claim_survived_is_still_excluded(self) -> None:
        # ``durum=uygulandi`` is a claim about the note, not proof: this index
        # still carries the wrong sentence, so the note stays out.
        self.write_note(
            "eski",
            title="Eski kayıt hedefi",
            body="Eski kayıt hedefi 12 Mart tarihinde kapandı.",
        )
        self.write_ledger(
            "<!-- duzeltme kavram=eski durum=uygulandi ts=2026-09-01T09:00:00+03:00 "
            "kaynak=daily/2026-09-01.md uygulandi_ts=2026-09-02T09:00:00+03:00 -->\n"
            "iddia: Eski kayıt hedefi 12 Mart tarihinde kapandı.\n"
            "dogru: Eski kayıt hedefi 19 Nisan tarihinde kapandı.\n\n"
        )
        self.build()

        hits = retrieve.search(
            "eski kayıt hedefi", db_path=self.db, vault_root=self.root
        )

        self.assertEqual([hit.name for hit in hits], ["correction:eski"])
        self.assertEqual(hits[0].body, "Eski kayıt hedefi 19 Nisan tarihinde kapandı.")

    def test_applied_target_that_really_changed_is_injected_again(self) -> None:
        # The claim is gone from the indexed body, so lane B's own
        # accountability test says the note is fixed and may be context again.
        self.write_note(
            "eski",
            title="Eski kayıt hedefi",
            body="Eski kayıt hedefi 19 Nisan tarihinde kapandı.",
        )
        self.write_ledger(
            "<!-- duzeltme kavram=eski durum=uygulandi ts=2026-09-01T09:00:00+03:00 "
            "kaynak=daily/2026-09-01.md uygulandi_ts=2026-09-02T09:00:00+03:00 -->\n"
            "iddia: Eski kayıt hedefi 12 Mart tarihinde kapandı.\n"
            "dogru: Eski kayıt hedefi 19 Nisan tarihinde kapandı.\n\n"
        )
        self.build()

        hits = retrieve.search(
            "eski kayıt hedefi", db_path=self.db, vault_root=self.root
        )

        self.assertEqual([hit.name for hit in hits], ["eski"])

    def test_block_with_no_dogru_line_still_hides_the_note(self) -> None:
        self.write_note("bozuk", title="Bozuk kayıt hedefi", body="Bozuk kayıt.")
        self.write_ledger(
            "<!-- duzeltme kavram=bozuk durum=bekliyor ts=2026-09-08T10:00:00+03:00 "
            "kaynak=- -->\n"
            "iddia: Bozuk kayıt.\n\n"
        )
        self.build()

        self.assertEqual(
            retrieve.search(
                "bozuk kayıt hedefi", db_path=self.db, vault_root=self.root
            ),
            [],
        )

    def test_correction_text_is_capped_at_three_hundred_characters(self) -> None:
        self.write_note("uzun", title="Uzun kayıt hedefi", body="Uzun kayıt.")
        self.write_ledger(
            "<!-- duzeltme kavram=uzun durum=bekliyor ts=2026-09-08T10:00:00+03:00 "
            "kaynak=- -->\n"
            "iddia: Uzun kayıt.\n"
            "dogru: " + "ç" * 400 + "\n\n"
        )
        self.build()

        hits = retrieve.search(
            "uzun kayıt hedefi", db_path=self.db, vault_root=self.root
        )

        self.assertEqual(len(hits[0].body), retrieve.CORRECTION_TEXT_CAP)

    def test_a_still_open_block_outweighs_a_later_closed_one(self) -> None:
        self.write_ledger(
            "<!-- duzeltme kavram=ikili durum=bekliyor ts=2026-09-01T09:00:00+03:00 "
            "kaynak=- -->\n"
            "iddia: Birinci yanlış cümle.\n"
            "dogru: Birinci doğru cümle.\n\n"
            "<!-- duzeltme kavram=ikili durum=uygulandi ts=2026-09-02T09:00:00+03:00 "
            "kaynak=- uygulandi_ts=2026-09-03T09:00:00+03:00 -->\n"
            "iddia: İkinci yanlış cümle.\n"
            "dogru: İkinci doğru cümle.\n\n"
        )

        record = retrieve.read_corrections(self.root)["ikili"]

        self.assertTrue(record.pending)
        self.assertEqual(record.dogru, "İkinci doğru cümle.")
        self.assertTrue(retrieve.correction_hides(record, "hiçbir iddia burada yok"))

    def test_missing_ledger_leaves_every_concept_alone(self) -> None:
        self.write_note("serbest", title="Serbest kayıt hedefi", body="Serbest kayıt.")
        self.build()

        self.assertEqual(retrieve.read_corrections(self.root), {})
        self.assertEqual(
            [
                hit.name
                for hit in retrieve.search(
                    "serbest kayıt hedefi", db_path=self.db, vault_root=self.root
                )
            ],
            ["serbest"],
        )

    def test_superseded_frontmatter_never_injects_without_correction(self) -> None:
        path = self.concepts / "eski.md"
        path.write_text(
            "---\ntitle: Eski karar kaydı\naliases: []\ntags: [eski, karar, kayıt]\n"
            "superseded_by: yeni\n---\n\nEski karar kaydı.",
            encoding="utf-8",
        )
        self.build()

        self.assertEqual(
            retrieve.search(
                "eski karar kaydı", db_path=self.db, vault_root=self.root
            ),
            [],
        )


class GhostAnchorHistoryTests(RetrieveHarness):
    """``compile.py --capa-temizle`` history is bookkeeping, never text."""

    def test_retired_anchor_comment_is_stripped_from_a_body(self) -> None:
        body = (
            "Gerçek gövde satırı.\n"
            "<!-- gecmis-capalar: session:agent-9f2 ts:2026-08-01T10:00:00+03:00 "
            "source:claude; session:agent-4c1 ts:2026-08-02T10:00:00+03:00 "
            "source:codex -->\n"
        )

        cleaned = retrieve.strip_session_anchors(body)

        self.assertEqual(cleaned, "Gerçek gövde satırı.\n")

    def test_retired_anchor_tokens_never_become_searchable(self) -> None:
        self.write_note(
            "capa-gecmisi",
            title="Çapa geçmişi kaydı",
            body=(
                "Çapa geçmişi kaydı gövdesi.\n"
                "<!-- gecmis-capalar: session:agent-benzersizjeton "
                "ts:2026-08-01T10:00:00+03:00 source:claude -->\n"
            ),
        )
        self.build()

        self.assertEqual(
            retrieve.search(
                "benzersizjeton", db_path=self.db, vault_root=self.root
            ),
            [],
        )
        hits = retrieve.search(
            "çapa geçmişi kaydı", db_path=self.db, vault_root=self.root
        )
        self.assertNotIn("gecmis-capalar", hits[0].body)

    def test_history_sharing_a_line_with_prose_leaves_the_prose(self) -> None:
        cleaned = retrieve.strip_session_anchors(
            "önce <!-- gecmis-capalar: session:agent-1 ts:x source:claude --> sonra"
        )

        self.assertEqual(cleaned, "önce  sonra")


class CallerAwareGateTests(RetrieveHarness):
    def test_path_tokens_extensions_drive_and_hex_ids_are_dropped(self) -> None:
        tokens = retrieve.gate_tokens(
            r"E:\repo\sessions scripts/retrieve.py note.md hook.ps1 abcdef123456 konu gerçek"
        )

        self.assertEqual(tokens, ("konu", "gerçek"))

    def test_machine_envelopes_and_json_are_rejected(self) -> None:
        for prompt in (
            "<command-name>pytest</command-name>",
            "<local-command-stdout>ok</local-command-stdout>",
            '{"session_id":"abc", "prompt":"memory topic"}',
        ):
            with self.subTest(prompt=prompt):
                self.assertFalse(retrieve.prompt_hafiza_ister(prompt))

    def test_prompts_under_three_content_words_are_rejected(self) -> None:
        self.assertFalse(retrieve.prompt_hafiza_ister("benzersiz hatıra nedir"))

    def test_overlap_threshold_rises_above_six_content_words(self) -> None:
        self.write_note(
            "alpha-beta",
            title="alpha beta",
            body="alpha beta gamma delta epsilon zeta theta",
        )
        self.build()

        six = retrieve.hook_result(
            "alpha beta gamma delta epsilon zeta",
            db_path=self.db,
            require_overlap=True,
            strict_score=1_000_000,
        )
        seven = retrieve.hook_result(
            "alpha beta gamma delta epsilon zeta theta",
            db_path=self.db,
            require_overlap=True,
            strict_score=1_000_000,
        )

        self.assertTrue(six["notes"])
        self.assertEqual(seven["notes"], [])


if __name__ == "__main__":
    unittest.main()
