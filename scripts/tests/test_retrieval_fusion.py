"""Reciprocal Rank Fusion, recency decay, flag routing and index verification.

Nothing here touches a model or the network; every case runs against a
synthetic vault built in a temporary directory.
"""

# yazan: claude · opus-5

from __future__ import annotations

import datetime as dt
import json
import os
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest import mock

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler

import retrieve


NOW = dt.datetime(2026, 8, 27, 12, 0, tzinfo=dt.timezone.utc)


class FusionHarness(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.concepts = self.root / "knowledge" / "concepts"
        self.state = self.root / ".claude" / "scripts" / ".state"
        self.concepts.mkdir(parents=True)
        self.state.mkdir(parents=True)
        # Tests must not inherit the developer's own retrieval settings.
        cleared = mock.patch.dict(
            os.environ,
            {
                key: ""
                for key in (
                    retrieve.RETRIEVAL_MODE_ENV,
                    retrieve.RRF_K_ENV,
                    retrieve.RECENCY_HALF_LIFE_ENV,
                )
            },
            clear=False,
        )
        cleared.start()
        self.addCleanup(cleared.stop)

    @property
    def db(self) -> Path:
        return self.state / retrieve.DB_NAME

    def write_note(
        self,
        name: str,
        *,
        title: str | None = None,
        aliases: tuple[str, ...] = (),
        tags: tuple[str, ...] = (),
        body: str = "Gövde metni.",
        created: str = "2026-01-01",
        updated: str = "2026-01-01",
    ) -> None:
        (self.concepts / f"{name}.md").write_text(
            "---\n"
            f"title: {title or name}\n"
            f"aliases: {json.dumps(aliases, ensure_ascii=False)}\n"
            f"tags: {json.dumps(tags, ensure_ascii=False)}\n"
            "sources: [sentetik.md]\n"
            f"created: {created}\n"
            f"updated: {updated}\n"
            "---\n\n"
            f"{body}\n",
            encoding="utf-8",
        )

    def build(self) -> dict:
        return retrieve.build_index(vault_root=self.root, state_dir=self.state)


class RrfArithmeticTests(unittest.TestCase):
    """Fixed synthetic lists in, known fused order out — no database involved."""

    def test_fused_scores_match_the_closed_form(self) -> None:
        lists = [["a", "b", "c"], ["c", "a"], ["b"]]

        fused = retrieve.rrf_fuse(lists, k=60)

        self.assertAlmostEqual(fused["a"], 1 / 61 + 1 / 62)
        self.assertAlmostEqual(fused["b"], 1 / 62 + 1 / 61)
        self.assertAlmostEqual(fused["c"], 1 / 63 + 1 / 61)
        self.assertEqual(
            sorted(fused, key=lambda name: (-fused[name], name)),
            ["a", "b", "c"],
        )

    def test_two_mediocre_placements_beat_one_good_one(self) -> None:
        """The whole point of RRF: agreement across signals outranks one spike."""
        fused = retrieve.rrf_fuse([["top", "both"], ["other", "both"]], k=1)

        self.assertEqual(
            sorted(fused, key=lambda name: (-fused[name], name))[0],
            "both",
        )
        self.assertAlmostEqual(fused["both"], 2 / 3)
        self.assertAlmostEqual(fused["top"], 1 / 2)

    def test_absent_note_contributes_nothing_and_k_shifts_the_gap(self) -> None:
        wide = retrieve.rrf_fuse([["a", "b"]], k=1)
        narrow = retrieve.rrf_fuse([["a", "b"]], k=1_000)

        self.assertNotIn("c", wide)
        self.assertGreater(wide["a"] - wide["b"], narrow["a"] - narrow["b"])

    def test_empty_input_is_an_empty_result(self) -> None:
        self.assertEqual(retrieve.rrf_fuse([]), {})
        self.assertEqual(retrieve.rrf_fuse([[], []]), {})

    def test_rank_mapping_and_positional_list_agree_when_nothing_ties(self) -> None:
        positional = retrieve.rrf_fuse([["a", "b", "c"]], k=60)
        mapped = retrieve.rrf_fuse([{"a": 1, "b": 2, "c": 3}], k=60)

        self.assertEqual(positional, mapped)

    def test_tied_names_share_one_rank_and_one_score(self) -> None:
        scores = {"a": -2.0, "b": -2.0, "c": -1.0}

        ranks = retrieve.competition_ranks(["a", "b", "c"], scores.__getitem__)
        fused = retrieve.rrf_fuse([ranks], k=60)

        self.assertEqual(ranks, {"a": 1, "b": 1, "c": 3})
        self.assertEqual(fused["a"], fused["b"])
        self.assertGreater(fused["a"], fused["c"])

    def test_competition_ranks_on_an_empty_list(self) -> None:
        self.assertEqual(retrieve.competition_ranks([], lambda name: name), {})


class RecencyDecayTests(unittest.TestCase):
    def test_half_life_and_bounds(self) -> None:
        self.assertAlmostEqual(retrieve.recency_weight(0.0, 180.0), 1.0)
        self.assertAlmostEqual(retrieve.recency_weight(180.0, 180.0), 0.5)
        self.assertAlmostEqual(retrieve.recency_weight(360.0, 180.0), 0.25)

    def test_floor_holds_for_an_ancient_note(self) -> None:
        for age in (720.0, 3_650.0, 100_000.0):
            with self.subTest(age=age):
                self.assertEqual(
                    retrieve.recency_weight(age, 180.0),
                    retrieve.RECENCY_WEIGHT_FLOOR,
                )

    def test_zero_half_life_disables_decay(self) -> None:
        self.assertEqual(retrieve.recency_weight(10_000.0, 0.0), 1.0)

    def test_future_dates_are_capped_at_one(self) -> None:
        self.assertEqual(retrieve.recency_weight(-500.0, 180.0), 1.0)


class SettingsResolutionTests(unittest.TestCase):
    def test_mode_default_is_bm25_and_junk_falls_back(self) -> None:
        self.assertEqual(retrieve.resolve_mode(None, {}), retrieve.MODE_BM25)
        self.assertEqual(
            retrieve.resolve_mode(None, {retrieve.RETRIEVAL_MODE_ENV: "nonsense"}),
            retrieve.MODE_BM25,
        )
        self.assertEqual(
            retrieve.resolve_mode(None, {retrieve.RETRIEVAL_MODE_ENV: " RRF "}),
            retrieve.MODE_RRF,
        )

    def test_explicit_argument_outranks_the_environment(self) -> None:
        environment = {retrieve.RETRIEVAL_MODE_ENV: "rrf"}
        self.assertEqual(
            retrieve.resolve_mode("bm25", environment), retrieve.MODE_BM25
        )

    def test_rrf_k_and_half_life_fall_back_on_junk(self) -> None:
        self.assertEqual(retrieve.resolve_rrf_k({}), 60)
        self.assertEqual(retrieve.resolve_rrf_k({retrieve.RRF_K_ENV: "12"}), 12)
        self.assertEqual(retrieve.resolve_rrf_k({retrieve.RRF_K_ENV: "0"}), 60)
        self.assertEqual(retrieve.resolve_rrf_k({retrieve.RRF_K_ENV: "abc"}), 60)
        self.assertEqual(retrieve.resolve_half_life_days({}), 180.0)
        self.assertEqual(
            retrieve.resolve_half_life_days({retrieve.RECENCY_HALF_LIFE_ENV: "0"}),
            0.0,
        )
        self.assertEqual(
            retrieve.resolve_half_life_days({retrieve.RECENCY_HALF_LIFE_ENV: "-3"}),
            180.0,
        )


class TagOverlapTests(unittest.TestCase):
    def test_dotted_capital_i_folds_onto_the_dotted_lowercase_form(self) -> None:
        tokens = set(retrieve.expanded_tokens("İSTANBUL"))

        for field in ("title", "aliases", "tags"):
            with self.subTest(field=field):
                fields = {"title": "", "aliases": "", "tags": ""}
                fields[field] = "istanbul belleği"
                self.assertGreater(
                    retrieve.tag_overlap(tokens, **fields), 0
                )
        self.assertGreater(retrieve.tag_overlap(tokens, "", "İstanbul", ""), 0)

    def test_dotless_capital_i_stays_a_different_word(self) -> None:
        """Turkish, not ASCII: ``I`` folds to ``ı``, so ``ISTANBUL`` ≠ ``İstanbul``."""
        dotted = set(retrieve.expanded_tokens("İSTANBUL"))
        dotless = set(retrieve.expanded_tokens("ISTANBUL"))

        self.assertEqual(retrieve.tag_overlap(dotted, "", "", "ISTANBUL"), 0)
        self.assertGreater(retrieve.tag_overlap(dotless, "", "", "ISTANBUL"), 0)
        self.assertEqual(dotted & dotless, set())

    def test_unrelated_note_has_zero_overlap(self) -> None:
        tokens = set(retrieve.expanded_tokens("İstanbul"))

        self.assertEqual(retrieve.tag_overlap(tokens, "Ankara", "başkent", "şehir"), 0)

    def test_more_matching_fields_score_higher(self) -> None:
        tokens = set(retrieve.expanded_tokens("bellek katmanı"))

        both = retrieve.tag_overlap(tokens, "bellek", "", "katmanı")
        one = retrieve.tag_overlap(tokens, "bellek", "", "")

        self.assertGreater(both, one)

    def test_empty_query_never_overlaps(self) -> None:
        self.assertEqual(retrieve.tag_overlap(set(), "bellek", "bellek", "bellek"), 0)
        self.assertEqual(retrieve.indexed_tag_overlap(set(), "bellek"), 0)

    def test_the_indexed_fast_path_agrees_with_the_reference(self) -> None:
        """The hot path reads pre-tokenized columns; it must not drift."""
        cases = [
            ("İstanbul", "istanbul belleği", "İSTANBUL", "şehir"),
            ("bellek katmanı", "Kalıcı bellek", "", "katman"),
            ("zümrüt", "Ankara", "başkent", "şehir"),
            ("ışık gösterisi", "Işık", "ışıklandırma", "gösteri"),
            ("yapay zeka", "", "", ""),
        ]
        for query, title, aliases, tags in cases:
            with self.subTest(query=query, title=title):
                tokens = set(retrieve.expanded_tokens(query))
                self.assertEqual(
                    retrieve.indexed_tag_overlap(
                        tokens,
                        retrieve.token_text(title),
                        retrieve.token_text(aliases),
                        retrieve.token_text(tags),
                    ),
                    retrieve.tag_overlap(tokens, title, aliases, tags),
                )


class ModeRoutingTests(FusionHarness):
    """The default path must stay exactly what it was before fusion existed."""

    def _legacy_rows(self, text: str, limit: int) -> list[tuple[str, float]]:
        connection = sqlite3.connect(self.db)
        connection.row_factory = sqlite3.Row
        try:
            rows = connection.execute(
                """
                SELECT documents.name, bm25(notes, 8.0, 6.0, 3.0, 1.0) AS score
                FROM notes
                JOIN documents ON documents.rowid = notes.rowid
                WHERE notes MATCH ?
                ORDER BY score, documents.name
                """,
                (retrieve._fts_query(text),),
            )
            return [(str(row["name"]), float(row["score"])) for row in rows][:limit]
        finally:
            connection.close()

    def test_default_mode_reproduces_the_raw_bm25_ordering(self) -> None:
        self.write_note("title-hit", title="Zümrüt", body="Sıradan gövde.")
        self.write_note("body-hit", title="Sıradan", body="Zümrüt gövde eşleşmesi.")
        self.write_note("ucuncu", title="Zümrüt yankısı", body="Başka gövde.")
        self.build()

        hits = retrieve.search("zümrüt", limit=3, db_path=self.db)

        self.assertEqual(
            [(hit.name, hit.score) for hit in hits],
            self._legacy_rows("zümrüt", 3),
        )

    def test_explicit_bm25_and_the_default_agree(self) -> None:
        self.write_note("bir", title="Ortak bir")
        self.write_note("iki", title="Ortak iki")
        self.build()

        default = retrieve.search("ortak", limit=2, db_path=self.db)
        explicit = retrieve.search("ortak", limit=2, db_path=self.db, mode="bm25")

        self.assertEqual(default, explicit)

    def test_environment_flag_switches_the_ranking(self) -> None:
        self.write_note("eski", title="Ortak eski", updated="2020-01-01")
        self.write_note("yeni", title="Ortak yeni", updated="2026-08-20")
        self.build()

        with mock.patch.dict(os.environ, {retrieve.RETRIEVAL_MODE_ENV: "rrf"}):
            fused = retrieve.search("ortak", limit=2, db_path=self.db)
        bm25 = retrieve.search("ortak", limit=2, db_path=self.db)

        # bm25 keeps its own scale (lower is better); rrf reports fused scores.
        self.assertTrue(all(hit.score <= 0 for hit in bm25))
        self.assertTrue(all(hit.score > 0 for hit in fused))
        self.assertEqual(fused[0].name, "yeni")

    def test_unparsable_flag_value_falls_back_to_bm25(self) -> None:
        self.write_note("bir", title="Ortak bir")
        self.build()

        with mock.patch.dict(os.environ, {retrieve.RETRIEVAL_MODE_ENV: "vector"}):
            hits = retrieve.search("ortak", limit=1, db_path=self.db)

        self.assertLessEqual(hits[0].score, 0)


class FusedRankingTests(FusionHarness):
    def _fused(
        self,
        text: str,
        *,
        limit: int = 5,
        min_score: float = 0.0,
        k: int = 60,
        half_life_days: float = 180.0,
        now: dt.datetime = NOW,
    ) -> list[retrieve.SearchHit]:
        connection = retrieve._open_readonly(self.db)
        try:
            return retrieve._fused_search(
                text,
                limit=limit,
                connection=connection,
                min_score=min_score,
                k=k,
                half_life_days=half_life_days,
                now=now,
            )
        finally:
            connection.close()

    def test_recency_breaks_a_sibling_note_tie(self) -> None:
        """The named miss class: two equally lexical siblings, one is current."""
        self.write_note("kardes-eski", title="Bellek katmanı", updated="2023-01-01")
        self.write_note("kardes-yeni", title="Bellek katmanı", updated="2026-08-20")
        self.build()

        bm25 = retrieve.search("bellek katmanı", limit=1, db_path=self.db)
        fused = self._fused("bellek katmanı", limit=1)

        self.assertEqual(bm25[0].name, "kardes-eski")  # alphabetical tie-break
        self.assertEqual(fused[0].name, "kardes-yeni")

    def test_tied_lexical_evidence_leaves_recency_to_decide(self) -> None:
        """Competition ranks: an alphabetical tie-break must not win twice."""
        self.write_note("kardes-eski", title="Bellek katmanı", updated="2023-01-01")
        self.write_note("kardes-yeni", title="Bellek katmanı", updated="2026-08-20")
        self.build()

        fused = self._fused("bellek katmanı", half_life_days=0.0)
        weights = {hit.name: hit.score for hit in fused}

        # BM25 and tag overlap tie exactly, so both notes share rank 1 in those
        # two lists; only the recency list separates them.
        self.assertEqual(fused[0].name, "kardes-yeni")
        self.assertGreater(weights["kardes-yeni"], weights["kardes-eski"])

    def test_weight_floor_caps_the_penalty_at_a_quarter(self) -> None:
        """The bound is on the multiplier, and it is exact."""
        self.write_note(
            "tam-eski",
            title="Zümrüt madenciliği",
            tags=("zümrüt",),
            body="Zümrüt madenciliği üzerine.",
            updated="2005-01-01",
        )
        self.build()

        undecayed = self._fused("zümrüt madenciliği", half_life_days=0.0)
        decayed = self._fused("zümrüt madenciliği", half_life_days=180.0)

        self.assertAlmostEqual(
            decayed[0].score,
            undecayed[0].score * retrieve.RECENCY_WEIGHT_FLOOR,
        )

    def test_a_fresh_weak_match_can_still_outrank_an_old_exact_one(self) -> None:
        """Pinned, not endorsed — see docs/retrieval.md, "Known limits".

        RRF compresses every candidate into a band of a few tenths of a percent
        while the recency multiplier spans 4x, so with the default k the decay
        term, not relevance, decides the order.  This is why ``rrf`` is opt-in.
        """
        self.write_note(
            "tam-eski",
            title="Zümrüt madenciliği",
            tags=("zümrüt",),
            body="Zümrüt madenciliği üzerine.",
            updated="2005-01-01",
        )
        self.write_note(
            "yeni-alakasiz",
            title="Zümrüt bir kez geçti",
            body="Konu tamamen başka; zümrüt sadece anıldı." + " dolgu" * 400,
            updated="2026-08-26",
        )
        self.build()

        decayed = self._fused("zümrüt madenciliği", limit=2)
        undecayed = self._fused("zümrüt madenciliği", limit=2, half_life_days=0.0)

        self.assertEqual(undecayed[0].name, "tam-eski")
        self.assertEqual(decayed[0].name, "yeni-alakasiz")

    def test_min_score_gates_the_bm25_component_only(self) -> None:
        self.write_note("etiketli", title="Zümrüt", tags=("zümrüt",))
        self.build()

        wide_open = self._fused("zümrüt")
        floored = self._fused("zümrüt", min_score=1_000_000.0)

        self.assertEqual([hit.name for hit in wide_open], ["etiketli"])
        # The note is gone from the BM25 list but the tag list still holds it,
        # so it survives with a strictly smaller fused score.
        self.assertEqual([hit.name for hit in floored], ["etiketli"])
        self.assertLess(floored[0].score, wide_open[0].score)

    def test_non_matching_notes_are_never_candidates(self) -> None:
        self.write_note("eslesen", title="Zümrüt", updated="2020-01-01")
        self.write_note("alakasiz", title="Bambaşka", updated="2026-08-26")
        self.build()

        fused = self._fused("zümrüt", limit=5)

        self.assertEqual([hit.name for hit in fused], ["eslesen"])

    def test_hook_output_respects_the_mode(self) -> None:
        self.write_note("eski", title="Ortak eski", updated="2020-01-01")
        self.write_note("yeni", title="Ortak yeni", updated="2026-08-20")
        self.build()

        result = retrieve.hook_result(
            "ortak", limit=1, db_path=self.db, mode="rrf"
        )

        self.assertEqual(result["notes"][0]["name"], "yeni")

    def test_benchmark_reports_the_mode_it_ran(self) -> None:
        self.write_note("hafiza", title="Hafıza", body="Kalıcı hafıza notu.")
        self.build()

        report = retrieve.benchmark(self.db, mode="rrf")

        self.assertEqual(report["mode"], "rrf")
        self.assertEqual(len(report["queries"]), len(retrieve.BENCH_QUERIES))
        self.assertGreaterEqual(report["p95_ms"], 0.0)


class SourceDateTests(FusionHarness):
    def test_frontmatter_updated_wins_over_created(self) -> None:
        self.write_note("not", created="2020-01-01", updated="2024-06-05")
        self.build()

        connection = sqlite3.connect(self.db)
        try:
            stored = connection.execute(
                "SELECT source_date FROM documents WHERE name = 'not'"
            ).fetchone()[0]
        finally:
            connection.close()

        self.assertTrue(stored.startswith("2024-06-05"))

    def test_missing_dates_fall_back_to_file_mtime(self) -> None:
        (self.concepts / "tarihsiz.md").write_text(
            "---\ntitle: Tarihsiz\naliases: []\ntags: []\n---\n\nGövde.\n",
            encoding="utf-8",
        )
        self.build()

        connection = sqlite3.connect(self.db)
        try:
            stored = connection.execute(
                "SELECT source_date FROM documents WHERE name = 'tarihsiz'"
            ).fetchone()[0]
        finally:
            connection.close()

        self.assertTrue(retrieve._parse_timestamp(stored) is not None)

    def test_undated_note_keeps_its_fused_score_unweighted(self) -> None:
        """No date is not the same as an ancient date."""
        self.write_note("tarihli", title="Ortak", updated="2005-01-01")
        (self.concepts / "tarihsiz.md").write_text(
            "---\ntitle: Ortak\naliases: []\ntags: []\n---\n\nGövde.\n",
            encoding="utf-8",
        )
        self.build()
        # Blank the stored date so the note reaches the fuser with none at all.
        writable = sqlite3.connect(self.db)
        try:
            writable.execute(
                "UPDATE documents SET source_date = '' WHERE name = 'tarihsiz'"
            )
            writable.commit()
        finally:
            writable.close()

        connection = retrieve._open_readonly(self.db)
        try:
            fused = retrieve._fused_search(
                "ortak",
                limit=5,
                connection=connection,
                min_score=0.0,
                k=60,
                half_life_days=180.0,
                now=NOW,
            )
        finally:
            connection.close()

        scores = {hit.name: hit.score for hit in fused}
        self.assertIn("tarihsiz", scores)
        self.assertGreater(scores["tarihsiz"], scores["tarihli"])


class LegacyIndexTests(FusionHarness):
    """An index built before schema 2 must degrade, not explode."""

    def _drop_source_date(self) -> None:
        connection = sqlite3.connect(self.db)
        try:
            connection.execute("ALTER TABLE documents DROP COLUMN source_date")
            connection.execute("PRAGMA user_version=1")
            connection.commit()
        finally:
            connection.close()

    def test_rrf_still_answers_without_the_recency_column(self) -> None:
        self.write_note("bir", title="Ortak bir", updated="2020-01-01")
        self.write_note("iki", title="Ortak iki", updated="2026-08-20")
        self.build()
        self._drop_source_date()

        hits = retrieve.search("ortak", limit=2, db_path=self.db, mode="rrf")

        self.assertEqual({hit.name for hit in hits}, {"bir", "iki"})
        self.assertTrue(all(hit.score > 0 for hit in hits))

    def test_verify_reports_the_stale_schema_version(self) -> None:
        self.write_note("bir")
        self.build()
        self._drop_source_date()

        report = retrieve.verify_index(vault_root=self.root, state_dir=self.state)

        self.assertTrue(report["ok"])  # contents still agree
        self.assertLess(report["schema_version"], retrieve.SCHEMA_VERSION)


class VerifyIndexTests(FusionHarness):
    def test_healthy_index_reports_ok(self) -> None:
        self.write_note("bir")
        self.write_note("iki")
        self.build()

        report = retrieve.verify_index(vault_root=self.root, state_dir=self.state)

        self.assertTrue(report["ok"])
        self.assertEqual(report["expected_count"], 2)
        self.assertEqual(report["indexed_count"], 2)
        self.assertEqual(report["fts_count"], 2)
        self.assertEqual(report["missing"], [])
        self.assertEqual(report["extra"], [])
        self.assertEqual(report["schema_version"], retrieve.SCHEMA_VERSION)

    def test_note_added_after_the_build_is_reported_missing(self) -> None:
        self.write_note("bir")
        self.build()
        self.write_note("sonradan")

        report = retrieve.verify_index(vault_root=self.root, state_dir=self.state)

        self.assertFalse(report["ok"])
        self.assertEqual(report["missing"], ["sonradan"])
        self.assertEqual(report["extra"], [])

    def test_note_deleted_after_the_build_is_reported_extra(self) -> None:
        self.write_note("bir")
        self.write_note("silinen")
        self.build()
        (self.concepts / "silinen.md").unlink()

        report = retrieve.verify_index(vault_root=self.root, state_dir=self.state)

        self.assertFalse(report["ok"])
        self.assertEqual(report["missing"], [])
        self.assertEqual(report["extra"], ["silinen"])

    def test_missing_database_is_named_not_crashed(self) -> None:
        self.write_note("bir")

        report = retrieve.verify_index(vault_root=self.root, state_dir=self.state)

        self.assertFalse(report["ok"])
        self.assertEqual(report["error"], "index-missing")
        self.assertEqual(report["missing"], ["bir"])

    def test_cli_exit_code_follows_the_diff(self) -> None:
        self.write_note("bir")
        self.build()
        arguments = [
            "verify",
            "--vault-root",
            str(self.root),
            "--state-dir",
            str(self.state),
        ]

        self.assertEqual(retrieve.main(arguments), 0)
        self.write_note("sonradan")
        self.assertEqual(retrieve.main(arguments), 1)


if __name__ == "__main__":
    unittest.main()
