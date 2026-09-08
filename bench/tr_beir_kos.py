#!/usr/bin/env python3
"""SciFact-TR üzerinde bu deponun FTS5 BM25 aramasını ölç.

Varsayılan ``.tmp-beir/`` dizini tek kullanımlık, yeniden üretilebilir benchmark
verisidir; sürüm kontrolünde izlenmemeli ve gerektiğinde elle silinebilir.
Yalnızca Python standart kütüphanesini kullanır.

nDCG@10 hesabında ``DCG@10 = sum((2**rel - 1) / log2(rank + 1))`` ve
``nDCG@10 = DCG@10 / IDCG@10`` kullanılır. Recall@10, ilk 10 sonuçtaki pozitif
ilgili belge sayısının sorgunun bütün pozitif ilgili belgelerine oranıdır.
"""

# yazan: codex · gpt-5.6-sol

from __future__ import annotations

import argparse
import csv
import importlib
import importlib.util
import json
import math
import os
from pathlib import Path
import shutil
import sys
import tempfile
import time
from types import ModuleType
from typing import Any, Iterable, Sequence
import urllib.error
import urllib.parse
import urllib.request


REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPTS_DIR = REPO_ROOT / "scripts"
RETRIEVE_PATH = SCRIPTS_DIR / "retrieve.py"
DEFAULT_DATA_DIR = REPO_ROOT / ".tmp-beir"

DATASET = "AbdulkaderSaoud/scifact-tr"
QRELS_DATASET = "AbdulkaderSaoud/scifact-tr-qrels"
DATASETS_SERVER = "https://datasets-server.huggingface.co"
PAGE_LENGTH = 100
REQUEST_TIMEOUT = 60
REQUEST_RETRY_DELAYS = (5, 15, 30, 60)
PAGE_PAUSE_SECONDS = 1.0

CORPUS_FILE = "corpus.jsonl"
QUERIES_FILE = "queries.jsonl"
QRELS_FILE = "qrels-test.jsonl"
RESULTS_FILE = "results-test.csv"


class BenchmarkError(RuntimeError):
    """Benchmark güvenilir biçimde tamamlanamadığında kullanılır."""


class RowsUnavailable(BenchmarkError):
    """datasets-server satır API'si veri sağlayamadığında kullanılır."""


def _request_json(url: str) -> dict[str, Any]:
    request = urllib.request.Request(
        url,
        headers={"User-Agent": "origin-of-memory-scifact-tr-benchmark/1"},
    )
    last_error: Exception | None = None
    for attempt in range(len(REQUEST_RETRY_DELAYS) + 1):
        try:
            with urllib.request.urlopen(request, timeout=REQUEST_TIMEOUT) as response:
                payload = json.load(response)
            break
        except (
            OSError,
            UnicodeError,
            json.JSONDecodeError,
            urllib.error.HTTPError,
            urllib.error.URLError,
        ) as exc:
            last_error = exc
            retryable = not isinstance(exc, urllib.error.HTTPError) or (
                exc.code == 429 or 500 <= exc.code < 600
            )
            if not retryable or attempt == len(REQUEST_RETRY_DELAYS):
                raise RowsUnavailable(f"HF isteği başarısız: {url}: {exc}") from exc
            delay = REQUEST_RETRY_DELAYS[attempt]
            print(
                f"HF geçici hata ({exc}); {delay} saniye sonra yeniden deneniyor...",
                file=sys.stderr,
                flush=True,
            )
            time.sleep(delay)
    else:
        raise RowsUnavailable(f"HF isteği başarısız: {url}: {last_error}")
    if not isinstance(payload, dict):
        raise RowsUnavailable(f"HF yanıtı nesne değil: {url}")
    return payload


def _endpoint(path: str, **parameters: object) -> str:
    query = urllib.parse.urlencode(parameters)
    return f"{DATASETS_SERVER}/{path}?{query}"


def _discover(dataset: str) -> list[dict[str, str]]:
    payload = _request_json(_endpoint("splits", dataset=dataset))
    splits = payload.get("splits")
    if not isinstance(splits, list) or not splits:
        raise RowsUnavailable(f"{dataset}: /splits kullanılabilir split döndürmedi")
    discovered: list[dict[str, str]] = []
    for item in splits:
        if not isinstance(item, dict):
            continue
        config = item.get("config")
        split = item.get("split")
        if isinstance(config, str) and isinstance(split, str):
            discovered.append({"config": config, "split": split})
    if not discovered:
        raise RowsUnavailable(f"{dataset}: config/split bilgisi çözülemedi")
    return discovered


def _select_split(
    discovered: Sequence[dict[str, str]], split: str
) -> tuple[str, str]:
    matches = [item for item in discovered if item["split"] == split]
    if len(matches) != 1:
        detail = ", ".join(
            f"{item['config']}/{item['split']}" for item in discovered
        )
        raise BenchmarkError(
            f"Beklenen split tekil değil: {split!r}; bulunanlar: {detail}"
        )
    return matches[0]["config"], matches[0]["split"]


def _jsonl_count(path: Path) -> int:
    count = 0
    with path.open("r", encoding="utf-8") as handle:
        for line_number, line in enumerate(handle, 1):
            if not line.strip():
                raise BenchmarkError(f"{path}: boş JSONL satırı: {line_number}")
            try:
                row = json.loads(line)
            except json.JSONDecodeError as exc:
                raise BenchmarkError(
                    f"{path}: geçersiz JSON, satır {line_number}: {exc}"
                ) from exc
            if not isinstance(row, dict):
                raise BenchmarkError(
                    f"{path}: JSONL satırı nesne değil: {line_number}"
                )
            count += 1
    return count


def _download_rows(
    *,
    dataset: str,
    config: str,
    split: str,
    target: Path,
) -> tuple[int, bool]:
    if target.exists():
        return _jsonl_count(target), True

    temporary = target.with_name(target.name + ".part")
    offset = 0
    total: int | None = None
    try:
        with temporary.open("w", encoding="utf-8", newline="\n") as handle:
            while total is None or offset < total:
                url = _endpoint(
                    "rows",
                    dataset=dataset,
                    config=config,
                    split=split,
                    offset=offset,
                    length=PAGE_LENGTH,
                )
                payload = _request_json(url)
                rows = payload.get("rows")
                reported_total = payload.get("num_rows_total")
                if not isinstance(rows, list) or not isinstance(reported_total, int):
                    raise RowsUnavailable(
                        f"{dataset} {config}/{split}: /rows biçimi beklenmedik"
                    )
                if total is None:
                    total = reported_total
                elif total != reported_total:
                    raise RowsUnavailable(
                        f"{dataset} {config}/{split}: satır sayısı indirme sırasında değişti"
                    )
                if not rows and offset < total:
                    raise RowsUnavailable(
                        f"{dataset} {config}/{split}: {offset} konumunda boş sayfa"
                    )
                for wrapper in rows:
                    row = wrapper.get("row") if isinstance(wrapper, dict) else None
                    if not isinstance(row, dict):
                        raise RowsUnavailable(
                            f"{dataset} {config}/{split}: geçersiz satır sarmalayıcısı"
                        )
                    handle.write(json.dumps(row, ensure_ascii=False) + "\n")
                offset += len(rows)
                if offset < total:
                    time.sleep(PAGE_PAUSE_SECONDS)
        if total is None or offset != total:
            raise RowsUnavailable(
                f"{dataset} {config}/{split}: {offset}/{total} satır indirildi"
            )
        os.replace(temporary, target)
    except Exception:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass
        raise
    return offset, False


def _download_file(url: str, target: Path) -> None:
    if target.exists():
        return
    temporary = target.with_name(target.name + ".part")
    request = urllib.request.Request(
        url,
        headers={"User-Agent": "origin-of-memory-scifact-tr-benchmark/1"},
    )
    try:
        with urllib.request.urlopen(request, timeout=REQUEST_TIMEOUT) as response:
            with temporary.open("wb") as handle:
                shutil.copyfileobj(response, handle)
        os.replace(temporary, target)
    except Exception:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass
        raise


def _parquet_fallback(dataset: str, data_dir: Path) -> list[Path]:
    payload = _request_json(_endpoint("parquet", dataset=dataset))
    files = payload.get("parquet_files")
    if not isinstance(files, list) or not files:
        raise BenchmarkError(f"{dataset}: /parquet de kullanılabilir dosya döndürmedi")
    downloaded: list[Path] = []
    prefix = "qrels" if dataset == QRELS_DATASET else "dataset"
    for index, item in enumerate(files):
        if not isinstance(item, dict) or not isinstance(item.get("url"), str):
            continue
        config = str(item.get("config", "unknown"))
        split = str(item.get("split", "unknown"))
        filename = str(item.get("filename", f"{index:04d}.parquet"))
        target = data_dir / f"{prefix}-{config}-{split}-{Path(filename).name}"
        _download_file(item["url"], target)
        downloaded.append(target)
    if not downloaded:
        raise BenchmarkError(f"{dataset}: parquet URL'leri çözülemedi")
    return downloaded


def _fallback_and_stop(data_dir: Path, reason: Exception) -> None:
    downloaded: list[Path] = []
    fallback_errors: list[str] = []
    for dataset in (DATASET, QRELS_DATASET):
        try:
            downloaded.extend(_parquet_fallback(dataset, data_dir))
        except Exception as exc:
            fallback_errors.append(f"{dataset}: {exc}")
    files = ", ".join(str(path) for path in downloaded) or "yok"
    suffix = (
        " Ek parquet hataları: " + " | ".join(fallback_errors)
        if fallback_errors
        else ""
    )
    raise BenchmarkError(
        "HF datasets-server satır API'si bu veri için kullanılamadı. "
        f"Parquet yedeği indirildi ({files}), fakat sıfır-bağımlılık politikası "
        f"nedeniyle parquet ayrıştırılmadı; benchmark durduruldu. Asıl hata: {reason}."
        f"{suffix}"
    )


def download(data_dir: Path) -> dict[str, int]:
    data_dir.mkdir(parents=True, exist_ok=True)
    try:
        main_splits = _discover(DATASET)
        qrels_splits = _discover(QRELS_DATASET)
        corpus_config, corpus_split = _select_split(main_splits, "corpus")
        queries_config, queries_split = _select_split(main_splits, "queries")
        qrels_config, qrels_split = _select_split(qrels_splits, "test")

        selections = (
            ("corpus", DATASET, corpus_config, corpus_split, CORPUS_FILE),
            ("queries", DATASET, queries_config, queries_split, QUERIES_FILE),
            ("qrels", QRELS_DATASET, qrels_config, qrels_split, QRELS_FILE),
        )
        counts: dict[str, int] = {}
        for label, dataset, config, split, filename in selections:
            count, skipped = _download_rows(
                dataset=dataset,
                config=config,
                split=split,
                target=data_dir / filename,
            )
            counts[label] = count
            action = "mevcut, atlandı" if skipped else "indirildi"
            print(f"{label}: {count} satır ({config}/{split}; {action})")
        return counts
    except RowsUnavailable as exc:
        _fallback_and_stop(data_dir, exc)
    raise AssertionError("ulaşılamaz")


def _load_jsonl(path: Path) -> list[dict[str, Any]]:
    if not path.exists():
        raise BenchmarkError(
            f"Eksik veri dosyası: {path}. Önce 'indir' alt komutunu çalıştırın."
        )
    rows: list[dict[str, Any]] = []
    with path.open("r", encoding="utf-8") as handle:
        for line_number, line in enumerate(handle, 1):
            try:
                row = json.loads(line)
            except json.JSONDecodeError as exc:
                raise BenchmarkError(
                    f"{path}: geçersiz JSON, satır {line_number}: {exc}"
                ) from exc
            if not isinstance(row, dict):
                raise BenchmarkError(f"{path}: nesne olmayan satır: {line_number}")
            rows.append(row)
    return rows


def _load_retrieve_once() -> ModuleType:
    importlib.invalidate_caches()
    scripts_text = str(SCRIPTS_DIR)
    if scripts_text not in sys.path:
        sys.path.insert(0, scripts_text)
    module_name = "_tr_beir_retrieve"
    sys.modules.pop(module_name, None)
    spec = importlib.util.spec_from_file_location(module_name, RETRIEVE_PATH)
    if spec is None or spec.loader is None:
        raise ImportError(f"Modül tanımı oluşturulamadı: {RETRIEVE_PATH}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[module_name] = module
    try:
        spec.loader.exec_module(module)
    except Exception:
        sys.modules.pop(module_name, None)
        raise
    return module


def _load_retrieve() -> ModuleType:
    try:
        return _load_retrieve_once()
    except Exception as first_error:
        print(
            f"retrieve.py importu başarısız ({first_error}); 60 saniye sonra bir kez "
            "yeniden denenecek.",
            file=sys.stderr,
            flush=True,
        )
        time.sleep(60)
        try:
            return _load_retrieve_once()
        except Exception as second_error:
            raise BenchmarkError(
                "retrieve.py iki denemede de import edilemedi: "
                f"ilk={first_error}; ikinci={second_error}"
            ) from second_error


def _required_text(row: dict[str, Any], field: str, source: Path) -> str:
    value = row.get(field)
    if value is None:
        raise BenchmarkError(f"{source}: eksik alan: {field}")
    return str(value)


def _build_qrels(
    rows: Iterable[dict[str, Any]], source: Path
) -> dict[str, dict[str, float]]:
    qrels: dict[str, dict[str, float]] = {}
    for row in rows:
        query_id = _required_text(row, "query-id", source)
        corpus_id = _required_text(row, "corpus-id", source)
        raw_score = row.get("score", 1)
        try:
            score = float(raw_score)
        except (TypeError, ValueError) as exc:
            raise BenchmarkError(
                f"{source}: qrel skoru sayısal değil: {raw_score!r}"
            ) from exc
        previous = qrels.setdefault(query_id, {}).get(corpus_id)
        if previous is None or score > previous:
            qrels[query_id][corpus_id] = score
    return qrels


def _query_metrics(
    retrieved: Sequence[str], relevances: dict[str, float]
) -> tuple[float, float, int]:
    positive = {name: grade for name, grade in relevances.items() if grade > 0}
    if not positive:
        return 0.0, 0.0, 0
    gains = [positive.get(name, 0.0) for name in retrieved[:10]]
    dcg = sum(
        (2.0**grade - 1.0) / math.log2(rank + 1)
        for rank, grade in enumerate(gains, 1)
        if grade > 0
    )
    ideal = sorted(positive.values(), reverse=True)[:10]
    idcg = sum(
        (2.0**grade - 1.0) / math.log2(rank + 1)
        for rank, grade in enumerate(ideal, 1)
    )
    found = sum(1 for name in retrieved[:10] if name in positive)
    return dcg / idcg if idcg else 0.0, found / len(positive), found


def _display_data_arg(data_dir: Path) -> str:
    try:
        relative = data_dir.resolve().relative_to(REPO_ROOT.resolve())
        text = str(relative)
    except ValueError:
        text = str(data_dir.resolve())
    return f'"{text}"'


def _print_rerun_commands(data_dir: Path) -> None:
    data_arg = _display_data_arg(data_dir)
    print("\nTam yeniden çalıştırma komutları (repo kökünden):")
    print("```powershell")
    print(f"python tools/tr_beir_kos.py indir --data {data_arg}")
    print(f"python tools/tr_beir_kos.py kos --data {data_arg}")
    print("```")


def run(data_dir: Path) -> dict[str, float | int]:
    corpus_path = data_dir / CORPUS_FILE
    queries_path = data_dir / QUERIES_FILE
    qrels_path = data_dir / QRELS_FILE
    corpus_rows = _load_jsonl(corpus_path)
    query_rows = _load_jsonl(queries_path)
    qrel_rows = _load_jsonl(qrels_path)

    corpus_ids = {
        _required_text(row, "_id", corpus_path) for row in corpus_rows
    }
    if len(corpus_ids) != len(corpus_rows):
        raise BenchmarkError("Corpus içinde yinelenen _id bulundu")
    queries = {
        _required_text(row, "_id", queries_path): _required_text(
            row, "text", queries_path
        )
        for row in query_rows
    }
    if len(queries) != len(query_rows):
        raise BenchmarkError("Queries içinde yinelenen _id bulundu")
    qrels = _build_qrels(qrel_rows, qrels_path)
    missing_queries = sorted(set(qrels) - set(queries))
    missing_documents = sorted(
        {
            document_id
            for judgments in qrels.values()
            for document_id in judgments
            if document_id not in corpus_ids
        }
    )
    if missing_queries or missing_documents:
        raise BenchmarkError(
            "Qrels veri bütünlüğü bozuk: "
            f"eksik sorgu={len(missing_queries)}, eksik belge={len(missing_documents)}"
        )

    retrieve = _load_retrieve()
    notes = [
        retrieve.ConceptNote(
            name=_required_text(row, "_id", corpus_path),
            title=str(row.get("title") or ""),
            aliases=(),
            tags=(),
            body=_required_text(row, "text", corpus_path),
        )
        for row in corpus_rows
    ]

    per_query: list[dict[str, object]] = []
    with tempfile.TemporaryDirectory(prefix="scifact-tr-fts-") as temporary:
        db_path = Path(temporary) / "notes.db"
        retrieve._create_database(db_path, notes)
        for query_id in sorted(qrels, key=lambda value: (len(value), value)):
            hits = retrieve.search(
                queries[query_id],
                limit=10,
                db_path=db_path,
                mode="bm25",
            )
            retrieved = [hit.name for hit in hits]
            ndcg, recall, found = _query_metrics(retrieved, qrels[query_id])
            per_query.append(
                {
                    "query_id": query_id,
                    "query": queries[query_id],
                    "relevant_count": sum(
                        1 for grade in qrels[query_id].values() if grade > 0
                    ),
                    "retrieved_count": len(retrieved),
                    "relevant_at_10": found,
                    "ndcg_at_10": f"{ndcg:.12f}",
                    "recall_at_10": f"{recall:.12f}",
                    "retrieved_ids": " ".join(retrieved),
                }
            )

    results_path = data_dir / RESULTS_FILE
    fieldnames = list(per_query[0]) if per_query else []
    if not fieldnames:
        raise BenchmarkError("Test qrels içinde skorlanabilir sorgu yok")
    temporary_results = results_path.with_name(results_path.name + ".part")
    with temporary_results.open("w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(per_query)
    os.replace(temporary_results, results_path)

    n_queries = len(per_query)
    mean_ndcg = sum(float(row["ndcg_at_10"]) for row in per_query) / n_queries
    mean_recall = sum(float(row["recall_at_10"]) for row in per_query) / n_queries
    report: dict[str, float | int] = {
        "ndcg_at_10": mean_ndcg,
        "recall_at_10": mean_recall,
        "n_queries": n_queries,
        "corpus_size": len(corpus_rows),
    }
    print("| metric | value | n_queries | corpus size |")
    print("| --- | ---: | ---: | ---: |")
    print(f"| nDCG@10 | {mean_ndcg:.6f} | {n_queries} | {len(corpus_rows)} |")
    print(f"| Recall@10 | {mean_recall:.6f} | {n_queries} | {len(corpus_rows)} |")
    print(f"\nSorgu başına sonuçlar: {results_path}")
    _print_rerun_commands(data_dir)
    return report


def _parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    for command, help_text in (
        ("indir", "HF rows API'sinden JSONL benchmark verisini indir"),
        ("kos", "geçici FTS5 indeksini kur ve test qrels'i skorla"),
    ):
        child = subparsers.add_parser(command, help=help_text)
        child.add_argument(
            "--data",
            type=Path,
            default=DEFAULT_DATA_DIR,
            help=(
                "tek kullanımlık, sürüm kontrolünde izlenmeyen veri dizini "
                f"(varsayılan: {DEFAULT_DATA_DIR})"
            ),
        )
        child.add_argument(
            "--dataset",
            default=DATASET,
            help=f"BEIR biçimli HF veri seti kimliği (varsayılan: {DATASET})",
        )
        child.add_argument(
            "--qrels",
            default=QRELS_DATASET,
            help=f"qrels HF veri seti kimliği (varsayılan: {QRELS_DATASET})",
        )
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    if os.environ.get("BEYIN_INVOKED_BY"):
        return 0
    args = _parse_args(argv)
    # --dataset/--qrels modül sabitlerini geçersiz kılar (EN/TR ikiz koşum).
    globals()["DATASET"] = args.dataset
    globals()["QRELS_DATASET"] = args.qrels
    data_dir = args.data.resolve()
    try:
        if args.command == "indir":
            download(data_dir)
            print(
                f"Veri dizini: {data_dir} (tek kullanımlık; sürüm kontrolünde izlenmez)"
            )
            _print_rerun_commands(data_dir)
        else:
            run(data_dir)
    except BenchmarkError as exc:
        print(f"HATA: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
