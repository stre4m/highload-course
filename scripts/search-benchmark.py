"""Нагрузочный эксперимент ДЗ №2 в локальной базе app_tests_k6."""
import argparse
import csv
import hashlib
import json
import os
from io import StringIO
from pathlib import Path
import platform
import random
import runpy
import socket
import subprocess
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import urlopen
import uuid

ROOT = Path(__file__).resolve().parents[1]
SOURCE = "https://raw.githubusercontent.com/OtusTeam/highload/master/homework/people.v2.csv"
BASE_URL = "http://127.0.0.1:5082"
CASES = [("Алек", "Иван"), ("Дми", "Кузне"), ("Конст", "Оси")]
DATABASE = "app_tests_k6"


def podman(container, *args, input_file=None):
    return subprocess.run(
        ["podman", "exec", "-i", container, *args], stdin=input_file,
        stdout=subprocess.PIPE, check=True,
    ).stdout.decode("utf-8")


def sql(container, query, database=None, input_file=None):
    return podman(
        container, "sh", "-c",
        'exec psql -X -v ON_ERROR_STOP=1 -At -U "$POSTGRES_USER" -d "$1" -c "$2"',
        "psql", database or DATABASE, query, input_file=input_file,
    ).strip()


def prepare(container, directory, resume_empty):
    podman(container, "pg_isready", "-h", "127.0.0.1")
    exists = bool(sql(container, f"SELECT 1 FROM pg_database WHERE datname = '{DATABASE}'", "postgres"))
    if exists and (not resume_empty or sql(container, "SELECT EXISTS (SELECT 1 FROM users)") != "f"):
        raise RuntimeError(f"{DATABASE} already exists; choose a fresh experiment environment.")
    if (metadata := sql(container, "SELECT version() || ' | ' || datcollate FROM pg_database WHERE datname = current_database()", "postgres")).startswith("PostgreSQL 17."):
        print(metadata, flush=True)
    else:
        raise RuntimeError(metadata)
    dataset = directory / "people.v2.csv"
    if not dataset.exists():
        temporary = dataset.with_suffix(".part")
        with urlopen(SOURCE, timeout=60) as response, temporary.open("wb") as output:
            while block := response.read(1024 * 1024):
                output.write(block)
        temporary.replace(dataset)
    with dataset.open("rb") as source:
        digest = hashlib.file_digest(source, "sha256").hexdigest()
    raw = dataset.read_bytes()
    # Опубликованный CSV заканчивается неполной строкой и оборванным символом UTF-8.
    complete = raw[:raw.rindex(b"\n") + 1]
    rows = list(csv.reader(StringIO(complete.decode("utf-8-sig"), newline="")))
    source_count = len(rows)
    if source_count < 990_000:
        raise RuntimeError(f"Source has only {source_count} complete records")
    rng = random.Random(42)
    if source_count < 1_000_000:
        rows.extend(rng.choices(rows, k=1_000_000 - source_count))
    rng.shuffle(rows)
    prepared = directory / "users.csv"
    with prepared.open("w", encoding="utf-8", newline="") as target:
        writer = csv.writer(target, lineterminator="\n")
        for full_name, birthdate, city in rows[:1_000_000]:
            last_name, first_name = full_name.split(maxsplit=1)
            writer.writerow([uuid.UUID(int=rng.getrandbits(128), version=4),
                             first_name, last_name, birthdate, "unknown", "", city, "AA=="])
    if not exists:
        sql(container, f"CREATE DATABASE {DATABASE}", "postgres")
    if sql(container, "SELECT current_database() || ':' || 1") != f"{DATABASE}:1":
        raise RuntimeError("Unexpected database")
    sql(container, (ROOT / "infrastructure/sql/001_create_users.sql").read_text(encoding="utf-8"))
    with prepared.open("rb") as source:
        sql(container, """COPY users (id, first_name, second_name, birthdate, gender, biography, city, password_hash)
            FROM STDIN WITH (FORMAT csv, ENCODING 'UTF8', FORCE_NOT_NULL (biography, city))""", input_file=source)
    sql(container, "VACUUM ANALYZE users")
    (directory / "import.json").write_text(json.dumps([metadata, digest, source_count]), encoding="utf-8")


def check_api(api):
    def fetch(first, last, status=200):
        try:
            with urlopen(f"{BASE_URL}/user/search?" + urlencode(
                {"first_name": first, "last_name": last}), timeout=10) as response:
                return json.load(response)
        except HTTPError as error:
            if error.code != status:
                raise
            return None

    first, last = CASES[0]
    for _ in range(30):
        if api.poll() is not None:
            raise RuntimeError("API exited during startup; check api.log")
        try:
            results = fetch(first, last)
            break
        except URLError as error:
            if isinstance(error, HTTPError):
                raise
            time.sleep(1)
    else:
        raise RuntimeError("API did not start")
    assert results and all(row["first_name"].startswith(first) and row["second_name"].startswith(last)
                           for row in results)
    ids = [row["id"] for row in results]
    assert ids == sorted(set(ids))
    assert fetch("%", last) == []
    assert fetch("", last, 400) is None
    return len(results)


def measure_k6(executable, directory, phase, concurrency, duration):
    env = os.environ.copy()
    env.update({
        "K6_NO_USAGE_REPORT": "true",
        "SEARCH_URL": BASE_URL,
        "SEARCH_VUS": str(concurrency),
        "SEARCH_CASES": json.dumps(CASES),
    })
    command = [executable, "run", "--quiet", str(ROOT / "scripts/search-k6.js")]
    env["SEARCH_DURATION"] = str(duration)
    with (directory / f"{phase}-{concurrency}-k6.log").open("wb") as log:
        raw = subprocess.check_output(command, env=env, stderr=log)
    summary = json.loads(raw)
    (directory / f"{phase}-{concurrency}-k6.json").write_bytes(raw)
    metrics = summary["metrics"]

    def count(name):
        return metrics.get(name, {}).get("values", {}).get("count", 0)

    successes = count("search_successes")
    errors = count("search_errors")
    if not successes or successes + errors != count("search_attempts"):
        raise RuntimeError(f"Incomplete k6 measurements: {phase}/{concurrency}")
    seconds = max(duration, metrics["search_completion"]["values"]["max"] / 1000)
    latency = metrics["search_latency"]["values"]
    return {"concurrency": concurrency, "seconds": seconds, "successes": successes,
            "errors": errors, "rps": successes / seconds, "p50": latency["med"],
            "p95": latency["p(95)"], "p99": latency["p(99)"]}


def run(container, directory, port, duration, k6, jmeter):
    endpoint = jmeter["gateway"]() if jmeter else None
    urls = f"{BASE_URL};http://{endpoint}:{jmeter['PORT']}" if jmeter else BASE_URL
    executable = ROOT / "src/SocialNetwork/bin/Release/net10.0/SocialNetwork.dll"
    if not executable.is_file():
        raise RuntimeError("Build SocialNetwork.sln in Release before measuring.")
    metadata, digest, source_rows = json.loads((directory / "import.json").read_text(encoding="utf-8"))
    if sql(container, "SELECT current_database() || ':' || 1") != f"{DATABASE}:1":
        raise RuntimeError("Unexpected database")
    if sql(container, "SELECT count(*) FROM pg_index WHERE indrelid = 'users'::regclass AND NOT indisprimary") != "0":
        raise RuntimeError("The baseline requires users with only its primary-key index.")
    stats = sql(container, """SELECT count(*), count(DISTINCT first_name),
        count(DISTINCT second_name) FROM users""").split("|")
    if int(stats[0]) != 1_000_000:
        raise RuntimeError(f"Unexpected row count: {stats}")
    settings = sql(container, """SELECT name || '=' || setting || coalesce(unit, '')
        FROM pg_settings WHERE name IN ('max_connections', 'shared_buffers',
        'max_parallel_workers_per_gather', 'work_mem', 'server_encoding') ORDER BY name""")
    credentials = podman(container, "sh", "-c", 'printf "%s\n%s" "$POSTGRES_USER" "$POSTGRES_PASSWORD"')
    username, password = credentials.split("\n", 1)
    def quote(value):
        return '"' + value.replace('"', '""') + '"'

    env = os.environ.copy()
    env["ConnectionStrings__Postgres"] = (
        f"Host=127.0.0.1;Port={port};Database={DATABASE};Username={quote(username)};"
        f"Password={quote(password)};Maximum Pool Size=64;Timeout=15;Command Timeout=15;Max Auto Prepare=0")
    env["Logging__LogLevel__Default"] = "Warning"
    dataset = {"postgres": metadata, "settings": settings, "sha256": digest,
               "source_rows": source_rows, "rows": list(map(int, stats)),
               "host": platform.platform(), "host_cpus": os.cpu_count(),
               "database": DATABASE,
               "jmeter": jmeter["run_wsl"]("jmeter", "-v") if jmeter else None,
               "k6": subprocess.check_output([k6, "version"], text=True).strip() if not jmeter else None,
               "cases": [], "duration": duration}
    for first, last in CASES:
        a, b, c = sql(container, f"""SELECT count(*) FILTER (WHERE first_name LIKE '{first}%'),
            count(*) FILTER (WHERE second_name LIKE '{last}%'),
            count(*) FILTER (WHERE first_name LIKE '{first}%' AND second_name LIKE '{last}%')
            FROM users""").split("|")
        dataset["cases"].append([first, last, int(a), int(b), int(c)])
    (directory / "dataset.json").write_text(json.dumps(dataset, ensure_ascii=False, indent=2), encoding="utf-8")
    with socket.socket() as listener:
        if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        listener.bind(("127.0.0.1", 5082))
    with (directory / "api.log").open("w", encoding="utf-8") as log:
        api = subprocess.Popen(["dotnet", str(executable),
                                "--urls", urls], env=env, stdout=log, stderr=subprocess.STDOUT)
        try:
            assert check_api(api) == dataset["cases"][0][-1]
            measurements = {}
            for phase in ("before", "after"):
                if phase == "after":
                    index_script, = (ROOT / "infrastructure/sql").glob("002*.sql")
                    sql(container, index_script.read_text(encoding="utf-8-sig"))
                    sql(container, "ANALYZE users")
                    assert check_api(api) == dataset["cases"][0][-1]
                plans = []
                for first, last in CASES:
                    plan = sql(container, f"""EXPLAIN (ANALYZE, BUFFERS)
                        SELECT id, first_name, second_name, birthdate, biography, city
                        FROM users
                        WHERE first_name LIKE '{first}%' ESCAPE '!'
                          AND second_name LIKE '{last}%' ESCAPE '!'
                        ORDER BY id""")
                    plans.append(f"{first} / {last}\n{plan}")
                (directory / f"{phase}-plans.txt").write_text("\n\n".join(plans) + "\n", encoding="utf-8")
                measurements[phase] = []
                for concurrency in (1, 10, 100, 1000):
                    if api.poll() is not None:
                        raise RuntimeError("API exited during measurements; check api.log")
                    result = (jmeter["measure"](endpoint, directory, phase, concurrency, duration)
                              if jmeter else measure_k6(k6, directory, phase, concurrency, duration))
                    measurements[phase].append(result)
                    print(phase, json.dumps(result), flush=True)
                    (directory / "metrics.json").write_text(json.dumps(measurements, indent=2), encoding="utf-8")
            (directory / "sizes.txt").write_text(sql(container, """SELECT indexrelid::regclass,
                pg_size_pretty(pg_relation_size(indexrelid)) FROM pg_index
                WHERE indrelid = 'users'::regclass ORDER BY 1""") + "\n", encoding="utf-8")
        finally:
            api.terminate()
            api.wait(timeout=15)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("container")
    parser.add_argument("directory", type=Path)
    parser.add_argument("--port", type=int, default=15432)
    parser.add_argument("--duration", type=int, default=15)
    parser.add_argument("--phase", choices=("prepare", "measure", "all"), default="all")
    parser.add_argument("--resume-empty", action="store_true")
    parser.add_argument("--k6", default="k6", help="Path to the k6 executable")
    parser.add_argument("--jmeter", action="store_true", help="Use Apache JMeter in WSL Ubuntu")
    args = parser.parse_args()
    jmeter = runpy.run_path(str(ROOT / "scripts/jmeter-runner.py")) if args.jmeter else None
    if args.jmeter:
        DATABASE = "app_tests_jmeter"
    if args.duration <= 0:
        parser.error("--duration must be positive")
    args.directory.mkdir(parents=True, exist_ok=True)
    if args.phase in ("prepare", "all"):
        prepare(args.container, args.directory, args.resume_empty)
    if args.phase in ("measure", "all"):
        run(args.container, args.directory, args.port, args.duration, args.k6, jmeter)
