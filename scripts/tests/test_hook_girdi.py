"""Kancaya GİRİLDİĞİNİN izi: hooks/.state/hook-girdi.jsonl (A-borç 2).

54. oturumda 19 saatlik bir transkript daily'ye hiç düşmedi ve teşhis
"SessionEnd hiç ateşlemedi" ile "ateşledi ama Python başlamadı" arasında ayrım
yapamadı — çünkü kancaya girildiğine dair hiçbir kayıt yoktu. `flush-launch.ps1`
artık stdin'i OKUMADAN ÖNCE bir satır düşer.

Betik geçici bir dizine kopyalanarak koşturulur: `$PSScriptRoot` oradan çözülür,
depo ağacı ve canlı kasa kirlenmez. Kardeş `scripts/flush.py` bilerek yoktur —
kanca "flush-script-missing" yazıp 0 ile çıkar, ama giriş izi zaten düşmüştür.

yazan: claude
model: opus-5
"""

from __future__ import annotations

import json
from pathlib import Path
import shutil
import subprocess
import unittest

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler


REPO_ROOT = Path(__file__).resolve().parents[2]
FLUSH_LAUNCH = REPO_ROOT / "hooks" / "flush-launch.ps1"
POWERSHELL = shutil.which("powershell")

STDIN_JSON = json.dumps(
    {
        "session_id": "test-hook-girdi-0001",
        "transcript_path": "C:\\yok\\transkript.jsonl",
        "hook_event_name": "SessionEnd",
    }
)


class KaynakSozlesmesiTests(unittest.TestCase):
    """PowerShell yoksa bile korunan değişmezler."""

    def test_iz_stdin_okunmadan_once_dusuyor(self) -> None:
        metin = FLUSH_LAUNCH.read_text(encoding="ascii")
        cagri = metin.index("Write-BeyinHookGirdi 'flush-launch' $Reason")
        muhafiz = metin.index("if ($env:BEYIN_INVOKED_BY) { exit 0 }")
        okuma = metin.index("[Console]::In.ReadToEnd()")

        self.assertLess(muhafiz, cagri, "muhafız izden önce gelmeli")
        self.assertLess(cagri, okuma, "iz stdin okunmadan önce düşmeli")

    def test_kaynak_ascii_ve_crlf(self) -> None:
        ham = FLUSH_LAUNCH.read_bytes()

        ham.decode("ascii")  # PS 5.1 BOM'suz dosyayı ANSI okur
        self.assertEqual(ham.count(b"\n"), ham.count(b"\r\n"))


@unittest.skipUnless(POWERSHELL, "Windows PowerShell is required")
class HookGirdiIziTests(unittest.TestCase):
    def _kos(self, kok: Path, reason: str = "sessionend") -> subprocess.CompletedProcess:
        (kok / "hooks").mkdir(parents=True, exist_ok=True)
        hedef = kok / "hooks" / "flush-launch.ps1"
        if not hedef.exists():
            shutil.copyfile(FLUSH_LAUNCH, hedef)
        return subprocess.run(
            [
                POWERSHELL,
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                str(hedef),
                "-Reason",
                reason,
            ],
            input=STDIN_JSON,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=120,
        )

    def _satirlar(self, kok: Path) -> list[dict]:
        defter = kok / "hooks" / ".state" / "hook-girdi.jsonl"
        self.assertTrue(defter.exists(), "hook-girdi.jsonl yazılmadı")
        return [
            json.loads(s)
            for s in defter.read_text(encoding="utf-8").splitlines()
            if s.strip()
        ]

    def test_kanca_calisinca_bir_satir_dusuyor(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as gecici:
            kok = Path(gecici)
            sonuc = self._kos(kok)

            self.assertEqual(sonuc.returncode, 0, sonuc.stderr)
            satirlar = self._satirlar(kok)
            self.assertEqual(len(satirlar), 1)
            kayit = satirlar[0]
            self.assertEqual(kayit["hook"], "flush-launch")
            self.assertEqual(kayit["reason"], "sessionend")
            self.assertIsInstance(kayit["pid"], int)
            self.assertTrue(kayit["ts"])

    def test_reason_argumani_kayda_geciyor(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as gecici:
            kok = Path(gecici)
            self._kos(kok, reason="precompact")

            self.assertEqual(self._satirlar(kok)[0]["reason"], "precompact")

    def test_defter_512_kb_ustunde_devriliyor(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as gecici:
            kok = Path(gecici)
            durum = kok / "hooks" / ".state"
            durum.mkdir(parents=True)
            (durum / "hook-girdi.jsonl").write_bytes(b"x" * (512 * 1024 + 1))

            self._kos(kok)

            self.assertTrue((durum / "hook-girdi.jsonl.1").exists())
            self.assertEqual(len(self._satirlar(kok)), 1)

    def test_beyin_invoked_by_izi_de_susturur(self) -> None:
        import os
        import tempfile

        with tempfile.TemporaryDirectory() as gecici:
            kok = Path(gecici)
            (kok / "hooks").mkdir(parents=True)
            shutil.copyfile(FLUSH_LAUNCH, kok / "hooks" / "flush-launch.ps1")
            ortam = dict(os.environ, BEYIN_INVOKED_BY="compile")
            sonuc = subprocess.run(
                [
                    POWERSHELL,
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    str(kok / "hooks" / "flush-launch.ps1"),
                ],
                input=STDIN_JSON,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                env=ortam,
                timeout=120,
            )

            self.assertEqual(sonuc.returncode, 0, sonuc.stderr)
            self.assertFalse((kok / "hooks" / ".state" / "hook-girdi.jsonl").exists())


if __name__ == "__main__":
    unittest.main()
