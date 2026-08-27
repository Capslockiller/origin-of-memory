"""Web ZIP adaptörü: zip-slip reddi, boyut tavanı, filigran davranışı."""

from __future__ import annotations

import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import zipfile

import _helpers
from _helpers import conversation

import ingest_common
import ingest_web


class WebImportTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.root = Path(self._temporary.name)
        self.addCleanup(self._temporary.cleanup)

    def _zip(self, name: str, members: dict[str, str]) -> Path:
        path = self.root / name
        with zipfile.ZipFile(path, "w") as archive:
            for member, text in members.items():
                archive.writestr(member, text)
        return path

    def _payload(self, conversations: list[dict]) -> str:
        return json.dumps(conversations, ensure_ascii=False)

    def _sample(self) -> list[dict]:
        return [
            conversation(
                "uuid-1",
                "Kasa planı",
                "2026-08-20T10:00:00.000000Z",
                "2026-08-20T12:00:00.000000Z",
                [
                    ("human", "merhaba", "2026-08-20T10:00:00.000000Z"),
                    ("assistant", "selam", "2026-08-20T10:01:00.000000Z"),
                    ("human", "peki ya bu", "2026-08-20T11:59:00.000000Z"),
                ],
            )
        ]

    def test_nested_member_found_and_human_mapped(self) -> None:
        path = self._zip(
            "export.zip",
            {"data-2026/conversations.json": self._payload(self._sample())},
        )
        sessions, error = ingest_web.candidates(
            ingest_common.default_state(),
            path,
        )
        self.assertEqual(error, "")
        self.assertEqual(len(sessions), 1)
        session = sessions[0]
        self.assertEqual(session.key, "uuid-1")
        self.assertEqual(session.origin, "Kasa planı")
        self.assertEqual(session.watermark, "2026-08-20T12:00:00.000000Z")
        self.assertEqual([role for role, _ in session.turns], ["user", "assistant", "user"])
        # Tarih SON mesajdan gelir.
        self.assertEqual(
            session.when,
            ingest_common.to_local("2026-08-20T11:59:00.000000Z"),
        )

    def test_zip_slip_refused(self) -> None:
        path = self._zip(
            "slip.zip",
            {
                "../evil.json": "{}",
                "conversations.json": self._payload(self._sample()),
            },
        )
        sessions, error = ingest_web.candidates(
            ingest_common.default_state(),
            path,
        )
        self.assertEqual(sessions, [])
        self.assertTrue(error.startswith("zip-slip:"))

    def test_absolute_member_refused(self) -> None:
        path = self.root / "abs.zip"
        with zipfile.ZipFile(path, "w") as archive:
            info = zipfile.ZipInfo("/etc/passwd")
            archive.writestr(info, "x")
            archive.writestr("conversations.json", self._payload(self._sample()))
        sessions, error = ingest_web.candidates(
            ingest_common.default_state(),
            path,
        )
        self.assertEqual(sessions, [])
        self.assertTrue(error.startswith("zip-slip:"))

    def test_oversized_member_reported_not_raised(self) -> None:
        path = self._zip(
            "big.zip",
            {"conversations.json": self._payload(self._sample())},
        )
        with mock.patch.object(ingest_web, "MAX_UNCOMPRESSED_BYTES", 4):
            sessions, error = ingest_web.candidates(
                ingest_common.default_state(),
                path,
            )
        self.assertEqual(sessions, [])
        self.assertEqual(error, "conversations-too-large")

    def test_missing_member_reported(self) -> None:
        path = self._zip("empty.zip", {"readme.txt": "yok"})
        sessions, error = ingest_web.candidates(
            ingest_common.default_state(),
            path,
        )
        self.assertEqual(sessions, [])
        self.assertEqual(error, "conversations-json-missing")

    def test_watermark_skip_and_resummarize(self) -> None:
        conversations = self._sample()
        path = self._zip(
            "export.zip",
            {"conversations.json": self._payload(conversations)},
        )
        state = ingest_common.default_state()
        ingest_common.record_done(
            state,
            ingest_web.SOURCE,
            "uuid-1",
            "appended",
            daily="2026-08-20.md",
            watermark="2026-08-20T12:00:00.000000Z",
        )
        # Aynı filigran: her iki modda da atlanır.
        self.assertEqual(ingest_web.candidates(state, path)[0], [])
        self.assertEqual(
            ingest_web.candidates(state, path, resummarize=True)[0],
            [],
        )

        # Filigran büyüdü: varsayılan hâlâ atlar, bayrakla yeniden özetlenir.
        conversations[0]["updated_at"] = "2026-08-21T09:00:00.000000Z"
        grown = self._zip(
            "grown.zip",
            {"conversations.json": self._payload(conversations)},
        )
        self.assertEqual(ingest_web.candidates(state, grown)[0], [])
        sessions, error = ingest_web.candidates(state, grown, resummarize=True)
        self.assertEqual(error, "")
        self.assertEqual(len(sessions), 1)
        self.assertEqual(sessions[0].label, ingest_web.UPDATED_LABEL)
        self.assertEqual(
            ingest_common.daily_suffix(sessions[0]),
            " — web (güncellenmiş) · özet: haiku",
        )

    def test_max_conversations_cap(self) -> None:
        conversations = []
        for index in range(5):
            conversations.append(
                conversation(
                    f"uuid-{index}",
                    f"Sohbet {index}",
                    "2026-08-20T10:00:00.000000Z",
                    "2026-08-20T12:00:00.000000Z",
                    [("human", "soru", "2026-08-20T10:00:00.000000Z")],
                )
            )
        path = self._zip(
            "many.zip",
            {"conversations.json": self._payload(conversations)},
        )
        sessions, _ = ingest_web.candidates(
            ingest_common.default_state(),
            path,
            max_conversations=2,
        )
        self.assertEqual(len(sessions), 2)

    def test_newest_zip_selection(self) -> None:
        import os

        first = self._zip("a.zip", {"conversations.json": "[]"})
        second = self._zip("b.zip", {"conversations.json": "[]"})
        os.utime(first, (1000, 1000))
        os.utime(second, (2000, 2000))
        self.assertEqual(ingest_web.newest_zip(self.root), second)


if __name__ == "__main__":
    unittest.main()
