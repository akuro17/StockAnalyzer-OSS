"""Split re-sync of the daily file (WebAiExpansion plan 08, T3). No network: the provider is a scripted fake.

Rules under test (owner decisions D-02=A, U-18=B, D-08-2=A):
- a fetched split (stock_splits > 0) that the stored file has not recorded -> re-fetch the whole history and keep it from the OLDEST stored date on;
- the stored file is replaced only after the written temp file passed validation, and never when the fetch fails,
  starts later than the stored history, or the file is in use (no retry);
- once the split is recorded the next update is an ordinary append.
"""

import datetime
import logging
import os
import sys
from pathlib import Path

import pandas as pd
import polars as pl
import pytest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "StockAnalyzer.Python"))
import data_provider  # noqa: E402
import update_daily  # noqa: E402

ALL = list(data_provider.DAILY_COLUMNS)
ACTIONS = list(data_provider.CORPORATE_ACTION_COLUMNS)
SYMBOL = "AAA"
D = datetime.date


def _frame(rows):
    """rows: (date, close, split). Polars storage frame in contract order and types."""
    return pl.DataFrame({
        "date": [r[0] for r in rows], "open": [r[1] for r in rows], "high": [r[1] for r in rows], "low": [r[1] for r in rows],
        "close": [r[1] for r in rows], "volume": [100] * len(rows), "adj_close": [r[1] for r in rows],
        "dividends": [0.0] * len(rows), "stock_splits": [r[2] for r in rows],
    }).with_columns([pl.col(c).cast(pl.Float32) for c in ("open", "high", "low", "close")]).with_columns(pl.col("volume").cast(pl.Int64))


def _provider_rows(rows):
    """The provider's pandas frame for rows: (date, close, split)."""
    return pd.DataFrame({
        "date": pd.to_datetime([r[0].isoformat() for r in rows]), "open": [float(r[1]) for r in rows], "high": [float(r[1]) for r in rows],
        "low": [float(r[1]) for r in rows], "close": [float(r[1]) for r in rows], "volume": [100.0] * len(rows),
        "adj_close": [float(r[1]) for r in rows], "dividends": [0.0] * len(rows), "stock_splits": [float(r[2]) for r in rows],
    })[ALL]


class ScriptedProvider:
    def __init__(self, *responses):
        self.responses = list(responses)
        self.start_ts = []

    def get_daily_data(self, symbol, start_ts=0, end_ts=None, delay=None, auto_adjust=False):
        self.start_ts.append(start_ts)
        response = self.responses.pop(0)
        if isinstance(response, Exception):
            raise response
        return response


@pytest.fixture
def root(tmp_path, monkeypatch):
    folder = tmp_path / "Daily"
    folder.mkdir()
    monkeypatch.setattr(update_daily, "DATA_ROOT", str(folder))
    return folder


OLD = [(D(2024, 1, 2), 100.0, 0.0), (D(2024, 1, 3), 100.0, 0.0), (D(2024, 1, 4), 100.0, 0.0), (D(2024, 1, 5), 100.0, 0.0)]
SPLIT_DAY = [(D(2024, 1, 8), 11.0, 10.0)]
FULL_NEW_SCALE = [(D(2024, 1, 2), 10.0, 0.0), (D(2024, 1, 3), 10.0, 0.0), (D(2024, 1, 4), 10.0, 0.0), (D(2024, 1, 5), 10.0, 0.0),
                  (D(2024, 1, 8), 11.0, 10.0)]


def _midnight_utc(day):
    return int(datetime.datetime.combine(day, datetime.time.min, tzinfo=datetime.timezone.utc).timestamp())


def _days(*days):
    return pl.Series("date", list(days), dtype=pl.Date)


def _store(root, rows):
    _frame(rows).write_parquet(root / f"{SYMBOL}.parquet")


def _stored(root):
    return pl.read_parquet(root / f"{SYMBOL}.parquet")


# ---- needs_split_resync (pure) -------------------------------------------------------------------------------------

def test_new_split_the_file_has_not_recorded_needs_resync():
    assert update_daily.needs_split_resync(_frame(OLD), _frame(SPLIT_DAY))


def test_split_already_recorded_with_the_same_value_does_not():
    stored = _frame(OLD + SPLIT_DAY)
    assert not update_daily.needs_split_resync(stored, _frame(SPLIT_DAY))


def test_split_recorded_with_a_different_value_needs_resync():
    stored = _frame(OLD + [(D(2024, 1, 8), 11.0, 5.0)])
    assert update_daily.needs_split_resync(stored, _frame(SPLIT_DAY))


def test_no_split_in_the_new_rows_does_not():
    assert not update_daily.needs_split_resync(_frame(OLD), _frame([(D(2024, 1, 8), 11.0, 0.0)]))


def test_no_stored_file_does_not():
    assert not update_daily.needs_split_resync(None, _frame(SPLIT_DAY))


def test_empty_stored_file_does_not():
    assert not update_daily.needs_split_resync(_frame(OLD).clear(), _frame(SPLIT_DAY))


def test_stored_file_without_the_column_has_recorded_nothing():
    six = _frame(OLD).select(list(data_provider.BASE_COLUMNS))
    assert update_daily.needs_split_resync(six, _frame(SPLIT_DAY))


def test_stored_row_with_a_null_split_value_has_recorded_nothing():
    stored = _frame(OLD + [(D(2024, 1, 8), 11.0, 0.0)]).with_columns(
        pl.when(pl.col("date") == D(2024, 1, 8)).then(None).otherwise(pl.col("stock_splits")).alias("stock_splits"))
    assert update_daily.needs_split_resync(stored, _frame(SPLIT_DAY))


def test_reverse_split_is_detected():
    assert update_daily.needs_split_resync(_frame(OLD), _frame([(D(2024, 1, 8), 11.0, 0.5)]))


# ---- update_ticker: the re-sync path --------------------------------------------------------------------------------

def test_unrecorded_split_refetches_from_the_oldest_stored_date_and_replaces_the_whole_file(root):
    _store(root, OLD)
    provider = ScriptedProvider(_provider_rows(SPLIT_DAY), _provider_rows(FULL_NEW_SCALE))

    update_daily.update_ticker(provider, SYMBOL)

    assert provider.start_ts == [_midnight_utc(D(2024, 1, 6)), update_daily.WHOLE_HISTORY_START_TS]  # append first, then the whole history
    out = _stored(root)
    assert out.columns == ALL
    assert out["date"].to_list() == [r[0] for r in FULL_NEW_SCALE]
    assert out["close"].to_list() == [10.0, 10.0, 10.0, 10.0, 11.0]  # every row on the new scale
    assert out["stock_splits"].to_list() == [0.0, 0.0, 0.0, 0.0, 10.0]  # the split is recorded now
    assert not (root / f"{SYMBOL}.parquet.tmp").exists()


def test_after_a_resync_the_next_update_is_an_ordinary_append(root):
    _store(root, OLD)
    update_daily.update_ticker(ScriptedProvider(_provider_rows(SPLIT_DAY), _provider_rows(FULL_NEW_SCALE)), SYMBOL)
    provider = ScriptedProvider(_provider_rows([(D(2024, 1, 9), 12.0, 0.0)]))

    update_daily.update_ticker(provider, SYMBOL)

    assert len(provider.start_ts) == 1  # no second fetch
    assert _stored(root)["date"].to_list()[-1] == D(2024, 1, 9)
    assert _stored(root).height == 6


def test_resync_also_covers_a_six_column_file(root):
    _frame(OLD).select(list(data_provider.BASE_COLUMNS)).write_parquet(root / f"{SYMBOL}.parquet")
    provider = ScriptedProvider(_provider_rows(SPLIT_DAY), _provider_rows(FULL_NEW_SCALE))

    update_daily.update_ticker(provider, SYMBOL)

    assert _stored(root).columns == ALL
    assert _stored(root)["close"].to_list() == [10.0, 10.0, 10.0, 10.0, 11.0]


def test_resync_keeps_the_stored_span_when_the_provider_returns_an_earlier_day(root):
    _store(root, OLD)
    earlier = [(D(2024, 1, 1), 9.0, 0.0)] + FULL_NEW_SCALE  # NY-time start can fetch one day before the stored oldest date
    update_daily.update_ticker(ScriptedProvider(_provider_rows(SPLIT_DAY), _provider_rows(earlier)), SYMBOL)
    assert _stored(root)["date"].to_list()[0] == D(2024, 1, 2)


class WindowsLikeProvider(ScriptedProvider):
    """Like the real provider on Windows: a negative start_ts (a date before 1970) cannot be converted and raises OSError."""

    def get_daily_data(self, symbol, start_ts=0, end_ts=None, delay=None, auto_adjust=False):
        if start_ts < 0:
            raise OSError(22, "Invalid argument")
        return super().get_daily_data(symbol, start_ts, end_ts, delay, auto_adjust)


def test_file_starting_before_1970_is_resynced_with_the_whole_history(root):
    # A stored history that starts before the Unix epoch (AA starts 1962) must not produce a negative start_ts.
    before_epoch = [(D(1962, 1, 2), 100.0, 0.0)]
    old = before_epoch + OLD
    full = [(D(1962, 1, 2), 10.0, 0.0)] + FULL_NEW_SCALE
    _store(root, old)
    provider = WindowsLikeProvider(_provider_rows(SPLIT_DAY), _provider_rows(full))

    update_daily.update_ticker(provider, SYMBOL)

    assert provider.start_ts[-1] == update_daily.WHOLE_HISTORY_START_TS
    out = _stored(root)
    assert out["date"].to_list() == [r[0] for r in full]
    assert out["close"].to_list() == [10.0, 10.0, 10.0, 10.0, 10.0, 11.0]  # repaired to the new scale, not left on the old one
    assert out["stock_splits"].to_list() == [0.0, 0.0, 0.0, 0.0, 0.0, 10.0]


def _assert_unchanged(root, before_bytes):
    assert (root / f"{SYMBOL}.parquet").read_bytes() == before_bytes
    assert not (root / f"{SYMBOL}.parquet.tmp").exists()


def test_provider_history_starting_later_leaves_the_file_unchanged_and_warns(root, caplog):
    _store(root, OLD)
    before = (root / f"{SYMBOL}.parquet").read_bytes()
    later = [(D(2024, 1, 4), 10.0, 0.0), (D(2024, 1, 5), 10.0, 0.0), (D(2024, 1, 8), 11.0, 10.0)]
    with caplog.at_level(logging.WARNING):
        update_daily.update_ticker(ScriptedProvider(_provider_rows(SPLIT_DAY), _provider_rows(later)), SYMBOL)
    assert "Split resync skipped: provider history starts later than stored history" in caplog.text
    _assert_unchanged(root, before)


def test_failed_resync_fetch_leaves_the_file_unchanged_and_warns(root, caplog):
    _store(root, OLD)
    before = (root / f"{SYMBOL}.parquet").read_bytes()
    for failure in (RuntimeError("network down"), None, pd.DataFrame()):
        caplog.clear()
        with caplog.at_level(logging.WARNING):
            update_daily.update_ticker(ScriptedProvider(_provider_rows(SPLIT_DAY), failure), SYMBOL)
        assert "Split resync failed" in caplog.text
        _assert_unchanged(root, before)


def test_written_file_failing_validation_is_not_swapped_in(root, caplog, monkeypatch):
    _store(root, OLD)
    before = (root / f"{SYMBOL}.parquet").read_bytes()
    monkeypatch.setattr(update_daily, "validate_resync_file", lambda path, oldest: ["forced problem"])
    with caplog.at_level(logging.WARNING):
        update_daily.update_ticker(ScriptedProvider(_provider_rows(SPLIT_DAY), _provider_rows(FULL_NEW_SCALE)), SYMBOL)
    assert "failed validation (forced problem)" in caplog.text
    _assert_unchanged(root, before)


def test_file_in_use_is_not_retried_and_stays_unchanged(root, caplog, monkeypatch):
    _store(root, OLD)
    before = (root / f"{SYMBOL}.parquet").read_bytes()
    calls = []

    def locked(src, dst):
        calls.append(src)
        raise PermissionError("in use")

    monkeypatch.setattr(update_daily.os, "replace", locked)
    with caplog.at_level(logging.WARNING):
        update_daily.update_ticker(ScriptedProvider(_provider_rows(SPLIT_DAY), _provider_rows(FULL_NEW_SCALE)), SYMBOL)
    assert len(calls) == 1  # no retry
    assert "stored file is in use" in caplog.text
    _assert_unchanged(root, before)


def test_ordinary_append_still_raises_when_the_file_is_in_use(root, monkeypatch):
    _store(root, OLD)
    monkeypatch.setattr(update_daily.os, "replace", lambda src, dst: (_ for _ in ()).throw(PermissionError("in use")))
    with pytest.raises(PermissionError):
        update_daily.update_ticker(ScriptedProvider(_provider_rows([(D(2024, 1, 8), 11.0, 0.0)])), SYMBOL)
    assert not (root / f"{SYMBOL}.parquet.tmp").exists()


# ---- validate_resync_file --------------------------------------------------------------------------------------------

def _write(tmp_path, frame):
    path = tmp_path / "check.parquet"
    frame.write_parquet(path)
    return path


def test_validation_accepts_a_contract_file(tmp_path):
    assert update_daily.validate_resync_file(_write(tmp_path, _frame(FULL_NEW_SCALE)), _days(D(2024, 1, 2))) == []


def test_validation_reports_each_kind_of_problem(tmp_path):
    good = _frame(FULL_NEW_SCALE)
    wrong_columns = update_daily.validate_resync_file(_write(tmp_path, good.drop("dividends")), _days(D(2024, 1, 2)))
    assert wrong_columns and "columns" in wrong_columns[0]
    price_type = update_daily.validate_resync_file(_write(tmp_path, good.with_columns(pl.col("close").cast(pl.Float64))), _days(D(2024, 1, 2)))
    assert "price columns are not Float32" in price_type
    action_type = update_daily.validate_resync_file(_write(tmp_path, good.with_columns(pl.col("dividends").cast(pl.Float32))), _days(D(2024, 1, 2)))
    assert "corporate-action columns are not Float64" in action_type
    duplicate = update_daily.validate_resync_file(_write(tmp_path, pl.concat([good, good.head(1)])), _days(D(2024, 1, 2)))
    assert "dates are not strictly increasing" in duplicate
    unordered = update_daily.validate_resync_file(_write(tmp_path, good.reverse()), _days(D(2024, 1, 2)))
    assert "dates are not strictly increasing" in unordered
    newer = update_daily.validate_resync_file(_write(tmp_path, good), _days(D(2023, 12, 1)))
    assert "history starts later than the stored file" in newer
    assert "no rows" in update_daily.validate_resync_file(_write(tmp_path, good.clear()), _days(D(2024, 1, 2)))


# ---- missing-day guard (owner decisions D-08-10..17, 2026-11-15) ----------------------------------------------------------

BASE_DAY = D(2024, 1, 1)


def _day(index):
    return BASE_DAY + datetime.timedelta(days=index)


def _resync_with_missing(root, stored_days, missing_indexes):
    """A stored file of `stored_days` consecutive days (old scale); the provider's whole history lacks `missing_indexes` of them.

    The provider also returns three NEW days after the last stored one, so its row count is never below the stored row count.
    Returns the caplog-relevant stored bytes before the update.
    """
    _store(root, [(_day(i), 100.0, 0.0) for i in range(stored_days)])
    before = (root / f"{SYMBOL}.parquet").read_bytes()
    last = stored_days
    new_days = [(_day(last + k), 11.0, 10.0 if k == 0 else 0.0) for k in range(3)]
    whole = [(_day(i), 10.0, 0.0) for i in range(stored_days) if i not in missing_indexes] + new_days
    update_daily.update_ticker(ScriptedProvider(_provider_rows(new_days[:1]), _provider_rows(whole)), SYMBOL)
    return before


@pytest.mark.parametrize("stored_days, missing, accepted", [
    (99, 0, True), (99, 1, False),       # 99 stored days: allowed 0
    (100, 1, True), (100, 2, False),     # 100 stored days: allowed 1
    (199, 1, True), (199, 2, False),     # 199 stored days: allowed 1 (rounded down, not to nearest)
    (200, 2, True), (200, 3, False),     # 200 stored days: allowed 2
])
def test_missing_days_up_to_the_allowed_number_are_replaced_and_more_are_refused(root, caplog, stored_days, missing, accepted):
    caplog.set_level(logging.WARNING)
    before = _resync_with_missing(root, stored_days, set(range(1, 1 + missing)))  # never the oldest day: that has its own guard
    if accepted:
        out = _stored(root)
        assert out["close"].max() == 11.0 and out["close"].min() == 10.0  # every row on the new scale
        assert out.height == stored_days - missing + 3
        assert not [r for r in caplog.records if "missing from the fetched history" in r.getMessage()]
    else:
        _assert_unchanged(root, before)
        assert any("Split resync skipped" in r.getMessage() and "missing from the fetched history" in r.getMessage() for r in caplog.records)


def test_new_days_in_the_fetched_history_do_not_hide_missing_stored_days(root, caplog):
    # 100 stored days, 2 missing, 3 new days: the fetched history has 101 rows (a row-count comparison would pass it).
    caplog.set_level(logging.WARNING)
    before = _resync_with_missing(root, 100, {10, 20})
    _assert_unchanged(root, before)


def test_the_warning_is_one_line_with_the_counts_and_examples_of_the_missing_days(root, caplog):
    caplog.set_level(logging.WARNING)
    _resync_with_missing(root, 100, {10, 20, 30, 40, 50})
    lines = [r.getMessage() for r in caplog.records if "missing from the fetched history" in r.getMessage()]
    assert lines == [
        f"{SYMBOL}: Split resync skipped: written file failed validation (stored days missing from the fetched history: "
        "5 of 100 (allowed 1); missing 2024-01-11..2024-02-20, first 2024-01-11, 2024-01-21, 2024-01-31); stored file left unchanged"
    ]
    assert len(lines[0].splitlines()) == 1


@pytest.mark.parametrize("stored_days, allowed", [(0, 0), (1, 0), (99, 0), (100, 1), (199, 1), (200, 2), (6701, 67)])
def test_allowed_missing_days_is_the_integer_percent_rounded_down(stored_days, allowed):
    result = update_daily.allowed_missing_days(stored_days)
    assert result == allowed and isinstance(result, int)


def test_the_guard_constants_have_the_documented_values():
    assert (update_daily.RESYNC_MAX_MISSING_PERCENT, update_daily.PERCENT_DENOMINATOR, update_daily.RESYNC_LOG_MISSING_EXAMPLES) == (1, 100, 3)


NOON = datetime.datetime(2024, 1, 2, 15, 30)
MIDNIGHT = datetime.datetime(2024, 1, 2, 0, 0)


@pytest.mark.parametrize("fetched", [
    pl.Series("date", [D(2024, 1, 2)], dtype=pl.Date),                                                    # a date
    pl.Series("date", [NOON]),                                                                           # a time of day, no time zone
    pl.Series("date", [MIDNIGHT]).dt.replace_time_zone("Asia/Tokyo"),                                    # Tokyo local midnight (UTC: the day before)
], ids=["date", "time-of-day", "tokyo-midnight"])
def test_the_same_calendar_day_is_not_missing_whatever_its_representation(fetched):
    assert update_daily.missing_stored_days(_days(D(2024, 1, 2)), fetched).to_list() == []
    assert update_daily.missing_stored_days(fetched, _days(D(2024, 1, 2))).to_list() == []


def test_a_different_calendar_day_is_missing():
    tokyo_next_day = pl.Series("date", [datetime.datetime(2024, 1, 3)]).dt.replace_time_zone("Asia/Tokyo")
    assert update_daily.missing_stored_days(_days(D(2024, 1, 2)), tokyo_next_day).to_list() == [D(2024, 1, 2)]
