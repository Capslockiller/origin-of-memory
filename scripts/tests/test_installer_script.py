# yazan: codex
# model: gpt-5.6-sol
"""Static compile-preflight checks for the Inno Setup installer."""

from __future__ import annotations

import re
from pathlib import Path


REPO = Path(__file__).resolve().parents[2]
SCRIPT = REPO / "installer" / "origin-of-memory.iss"


def _text() -> str:
    return SCRIPT.read_text(encoding="utf-8")


def _sections(text: str) -> dict[str, list[str]]:
    sections: dict[str, list[str]] = {}
    current: str | None = None
    for raw_line in text.splitlines():
        line = raw_line.strip()
        match = re.fullmatch(r"\[([^]]+)]", line)
        if match:
            current = match.group(1).lower()
            sections.setdefault(current, [])
        elif current is not None:
            sections[current].append(raw_line)
    return sections


def _files_sources(lines: list[str]) -> list[str]:
    sources: list[str] = []
    for line in lines:
        if line.lstrip().startswith(";"):
            continue
        match = re.search(r'\bSource:\s*"([^"]+)"', line, re.IGNORECASE)
        if match:
            sources.append(match.group(1))
    return sources


def test_installer_exists_and_has_ini_like_sections() -> None:
    assert SCRIPT.is_file()
    sections = _sections(_text())
    assert {"setup", "files", "icons", "uninstallrun", "code"} <= sections.keys()
    assert any(line.strip().startswith("AppName=") for line in sections["setup"])


def test_privileges_required_stays_lowest() -> None:
    """This is the tripwire that fails if PrivilegesRequired is raised to admin."""

    setup = "\n".join(_sections(_text())["setup"])
    values = re.findall(
        r"(?im)^\s*PrivilegesRequired\s*=\s*([^;\r\n]+)", setup
    )
    assert values == ["lowest"]


def test_install_authority_and_uninstaller_are_referenced() -> None:
    text = _text()
    assert "kur.ps1" in text
    assert "-Answers" in text
    assert "uninstall.ps1" in text


def test_model_and_backend_are_not_hardcoded_plan_defaults() -> None:
    text = _text()
    assert not re.search(r"\b(?:qwen|gemma)[\w.-]*:\d+\b", text, re.IGNORECASE)
    assert not re.search(
        r"[\"']backend[\"']\s*:\s*[\"']ollama[\"']", text, re.IGNORECASE
    )
    assert not re.search(
        r"\b(?:defaultbackend|backend)\s*:=\s*'ollama'", text, re.IGNORECASE
    )


def test_every_files_source_exists_in_repository() -> None:
    sections = _sections(_text())
    sources = _files_sources(sections["files"])
    assert sources
    for source in sources:
        source_path = (SCRIPT.parent / source).resolve()
        if "*" in source_path.name or "?" in source_path.name:
            matches = list(source_path.parent.glob(source_path.name))
            assert matches, f"[Files] wildcard has no matches: {source}"
        else:
            assert source_path.exists(), f"[Files] source is missing: {source}"


def test_desktop_shortcut_icon_entry_exists() -> None:
    icons = "\n".join(_sections(_text())["icons"])
    assert re.search(
        r'(?im)^\s*Name:\s*"\{autodesktop}\\[^";]+"', icons
    )

def test_installer_version_matches_the_newest_changelog_release() -> None:
    """A stale AppVersion is invisible until the wizard is opened.

    The first working build announced "Origin of Memory 0.1.0" while the
    project was at 0.3.0. Compiling cannot catch that, and neither can any
    check that does not compare the two files.
    """
    declared = re.search(r'#define\s+AppVersion\s+"([^"]+)"', _text())
    assert declared, "AppVersion is not defined"
    changelog = (SCRIPT.parents[1] / "CHANGELOG.md").read_text(encoding="utf-8")
    released = re.findall(r"^## \[(\d+\.\d+\.\d+)\]", changelog, flags=re.MULTILINE)
    assert released, "no released version found in CHANGELOG.md"
    assert declared.group(1) == released[0], (
        f"installer says {declared.group(1)}, newest release is {released[0]}"
    )
