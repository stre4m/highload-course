import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import math
import os
from pathlib import Path
import subprocess
import threading
import time
from urllib.request import Request, urlopen
from uuid import UUID, uuid4


ROOT = Path(__file__).resolve().parents[1]
DATABASE = "app_tests_replication"
NODES = ("replica1", "replica2")
SEARCH_INDEX = ROOT / "infrastructure/sql/002_users_search_prefix.sql"


class Lab:
    def __init__(self, engine, port, output):
        self.project = f"social-network-hw3-{uuid4().hex[:8]}"
        self.output = output
        self.env = dict(os.environ, HW3_API_PORT=str(port), HW3_POSTGRES_READ="",
                        HW3_POSTGRES_WRITE=f"Host=primary;Database={DATABASE}")
        self.password = uuid4().hex
        self.env["HW3_POSTGRES_PASSWORD"] = self.password
        self.base = f"http://127.0.0.1:{port}"
        self.command = [engine, "compose", "-p", self.project, "-f",
                        "infrastructure/homework-3/compose.yaml"]

    def compose(self, *args, capture=False, timeout=600):
        command = self.command + list(args)
        result = subprocess.run(command, cwd=ROOT, env=self.env, text=True,
                                capture_output=capture, timeout=timeout, check=False,
                                encoding="utf-8", errors="replace")
        if result.returncode:
            raise RuntimeError(f"{args[:3]} -> {result.returncode}: "
                               f"{(result.stderr or '').replace(self.password, '[redacted]')}")
        return result.stdout.strip() if capture else None

    def sql(self, node, sql, *, timeout=20):
        return self.compose("exec", "-T", node, "psql", "-XAt", "-v", "ON_ERROR_STOP=1",
                            "-U", "appuser", "-d", DATABASE, "-c", sql, capture=True, timeout=timeout)

    def ready(self, node):
        self.compose("exec", "-T", node, "pg_isready", "-h", "127.0.0.1", "-U", "appuser", "-d", DATABASE, capture=True)
        return self.sql(node, "SELECT 1") == "1"

    def http(self, path, payload=None):
        request = Request(self.base + path)
        if payload is not None:
            request.data = json.dumps(payload).encode("utf-8")
            request.add_header("Content-Type", "application/json")
        with urlopen(request, timeout=5) as response:
            return json.load(response)

    def save(self, name, data):
        (self.output / name).write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")


def wait_for(description, check, timeout=90):
    deadline = time.monotonic() + timeout
    last = None
    while time.monotonic() < deadline:
        try:
            value = check()
            if value:
                return value
        except (OSError, RuntimeError, ValueError) as exc:
            last = exc
        time.sleep(0.5)
    raise RuntimeError(f"Timed out: {description}: {last}")


def seed(lab, rows):
    lab.sql("primary", f"""
        INSERT INTO users (id, first_name, second_name, birthdate, gender, biography, city, password_hash)
        SELECT md5(n::text)::uuid, 'Name' || lpad((n % 1000)::text, 4, '0'),
            'Surname' || lpad((n / 1000)::text, 5, '0'), DATE '1990-01-01',
            'other', 'Synthetic profile', 'Test city', 'disabled'
        FROM generate_series(1, {rows}) AS n;
        CREATE TABLE hw3_writes (id uuid PRIMARY KEY, written_at timestamptz DEFAULT clock_timestamp());
        """)
    lab.sql("primary", SEARCH_INDEX.read_text(encoding="utf-8"))
    lab.sql("primary", "ANALYZE users")
    return [f"/user/get/{value}" for value in lab.sql(
        "primary", "SELECT md5(n::text)::uuid FROM generate_series(1, 100) AS n"
    ).splitlines()]


def measure(lab, paths, workers, seconds):
    barrier = threading.Barrier(workers + 1)
    deadline = [0.0]

    def read(worker):
        samples = {"get": [], "search": []}
        errors = {"get": 0, "search": 0}
        index = worker
        barrier.wait()
        while time.perf_counter() < deadline[0]:
            kind = "get" if index % 2 == 0 else "search"
            path = paths[(index // 2) % len(paths)]
            if kind == "search":
                path = f"/user/search?first_name=Name{(index // 2 + 1) % 1000:04d}&last_name=Surname000"
            started = time.perf_counter()
            try:
                if not lab.http(path):
                    raise ValueError("Empty response")
                samples[kind].append((time.perf_counter() - started) * 1000)
            except (OSError, ValueError):
                errors[kind] += 1
            index += 1
        return samples, errors

    with ThreadPoolExecutor(max_workers=workers) as pool:
        futures = [pool.submit(read, worker) for worker in range(workers)]
        started = time.perf_counter()
        deadline[0] = started + seconds
        barrier.wait()
        results = [future.result() for future in futures]
        elapsed = time.perf_counter() - started

    metrics = {"seconds": elapsed, "workers": workers, "endpoints": {}}
    for kind in ("get", "search"):
        times = sorted(value for samples, _ in results for value in samples[kind])
        failed = sum(errors[kind] for _, errors in results)
        metrics["endpoints"][kind] = {
            "ok": len(times), "errors": failed, "requests_per_second": len(times) / elapsed,
            **{f"p{p}_ms": times[math.ceil(p * len(times) / 100) - 1] if times else None
               for p in (50, 95, 99)},
        }
    return metrics


def scans(lab, nodes):
    return {node: int(lab.sql(node, "SELECT seq_scan + idx_scan FROM pg_stat_user_tables "
                              "WHERE relname = 'users'")) for node in nodes}


def replication(lab, node="primary"):
    rows = lab.sql(node, "SELECT application_name || '|' || state || '|' || sync_state "
                         "FROM pg_stat_replication ORDER BY application_name")
    return rows.splitlines()


def alter(lab, node, setting, value):
    lab.sql(node, f"ALTER SYSTEM SET {setting} = '{value}'")
    lab.sql(node, "SELECT pg_reload_conf()")


def failover(lab):
    alter(lab, "primary", "synchronous_standby_names", "ANY 1 (replica1, replica2)")
    expected = [f"{node}|streaming|quorum" for node in NODES]
    quorum = wait_for("both members in quorum", lambda: (
        rows if (rows := replication(lab)) == expected else None
    ))
    settings = lab.sql("primary", "SELECT current_setting('synchronous_commit'), "
                                   "current_setting('synchronous_standby_names')")
    if settings != "on|ANY 1 (replica1, replica2)":
        raise RuntimeError(f"Unexpected primary settings: {settings}")

    stop = threading.Event()
    enough = threading.Event()
    lock = threading.Lock()
    acknowledged = []
    unconfirmed = []
    started = time.monotonic()
    with (lab.output / "acknowledged.txt").open("w", encoding="utf-8", buffering=1) as journal:
        def writer():
            while not stop.is_set():
                record = str(uuid4())
                try:
                    lab.sql("primary", f"INSERT INTO hw3_writes(id) VALUES ('{record}')", timeout=10)
                except (RuntimeError, subprocess.TimeoutExpired):
                    with lock:
                        unconfirmed.append(record)
                    continue
                with lock:
                    acknowledged.append(record)
                    journal.write(record + "\n")
                    if len(acknowledged) >= 24:
                        enough.set()

        with ThreadPoolExecutor(max_workers=4) as pool:
            futures = [pool.submit(writer) for _ in range(4)]
            try:
                if not enough.wait(40):
                    raise RuntimeError("The client failed to acknowledge 24 transactions")
                with lock:
                    confirmed_before_kill = len(acknowledged)
                lab.compose("kill", "-s", "KILL", "primary", timeout=30)
                failure_at = time.monotonic() - started
            finally:
                stop.set()
            for future in futures:
                future.result()

    def positions():
        result = {node: lab.sql(node, "SELECT pg_last_wal_receive_lsn(), pg_last_wal_replay_lsn()")
                  for node in NODES}
        for value in result.values():
            received, replayed = value.split("|")
            if not received or received != replayed:
                return None
        return {node: value.split("|")[0] for node, value in result.items()}

    lsn = wait_for("WAL replay on replicas", positions)
    promoted = max(NODES, key=lambda node: tuple(int(part, 16) for part in lsn[node].split("/")))
    follower = next(node for node in NODES if node != promoted)
    if lab.sql(promoted, "SELECT pg_promote(true, 60)", timeout=65) != "t":
        raise RuntimeError("Promotion failed")
    alter(lab, follower, "primary_conninfo",
          f"host={promoted} port=5432 user=appuser password={lab.password} application_name={follower}")
    wait_for("streaming from promoted node", lambda: f"{follower}|streaming|async" in replication(lab, promoted))
    alter(lab, promoted, "synchronous_standby_names", f"ANY 1 ({follower})")
    wait_for("quorum restored", lambda: f"{follower}|streaming|quorum" in replication(lab, promoted))
    stored = set(lab.sql(promoted, "SELECT id FROM hw3_writes").splitlines())
    missing = sorted(set(acknowledged) - stored)
    control = str(uuid4())
    lab.sql(promoted, f"INSERT INTO hw3_writes(id) VALUES ('{control}')")
    wait_for("new write visible on follower",
             lambda: lab.sql(follower, f"SELECT id FROM hw3_writes WHERE id = '{control}'") == control)
    lab.save("failover.json", {
        "original_replication": quorum, "original_settings": settings,
        "seconds_until_primary_stopped": failure_at, "attempted": len(acknowledged) + len(unconfirmed),
        "acknowledged_before_kill_request": confirmed_before_kill,
        "acknowledged": len(acknowledged), "unconfirmed_ids": sorted(unconfirmed),
        "stored_before_control": len(stored), "committed_without_ack": sorted(stored - set(acknowledged)),
        "missing_acknowledged_ids": missing, "replayed_lsn": lsn,
        "promoted": promoted, "follower": follower, "restored_replication": replication(lab, promoted),
        "control_id_visible_on_follower": control,
    })
    if missing:
        raise RuntimeError(f"Lost acknowledged transactions: {len(missing)}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--engine", choices=("podman", "docker"), default="podman")
    parser.add_argument("--port", type=int, default=5084)
    parser.add_argument("--rows", type=int, default=100000)
    parser.add_argument("--workers", type=int, default=16)
    parser.add_argument("--seconds", type=float, default=15)
    parser.add_argument("--output", type=Path, default=Path("TestResults/hw3"))
    args = parser.parse_args()
    if args.rows < 1000 or args.workers < 1:
        parser.error("--rows must be at least 1000 and --workers must be positive")
    if not (math.isfinite(args.seconds) and args.seconds > 0 and 1 <= args.port <= 65535):
        parser.error("--seconds must be finite and positive, and --port must be between 1 and 65535")
    args.output.mkdir(parents=True, exist_ok=False)
    lab = Lab(args.engine, args.port, args.output)
    print(f"Compose project: {lab.project}", flush=True)
    lab.save("environment.json", {"project": lab.project, "rows": args.rows,
                                  "workers": args.workers, "seconds": args.seconds,
                                  "api_port": args.port, "engine": args.engine})
    try:
        lab.compose("up", "-d", "primary")
        wait_for("primary", lambda: lab.ready("primary"))
        print("Loading synthetic profiles", flush=True)
        paths = seed(lab, args.rows)
        phases = {}
        for phase, nodes in (("primary", ("primary",)), ("replicas", NODES)):
            print(f"Reading via {phase}", flush=True)
            if phase == "replicas":
                lab.compose("up", "-d", *NODES)
                for node in NODES:
                    wait_for(node, lambda: lab.ready(node))
                    wait_for(f"dataset on {node}", lambda: (
                        int(lab.sql(node, "SELECT count(*) FROM users")) == args.rows
                    ))
                phases["async_replication"] = wait_for("both async replicas streaming", lambda: (
                    rows if (rows := replication(lab)) == [f"{node}|streaming|async" for node in NODES] else None
                ))
                lab.env["HW3_POSTGRES_READ"] = (f"Host=replica1,replica2;Database={DATABASE};"
                                               "Target Session Attributes=standby;Load Balance Hosts=true")
            lab.compose("up", "-d", "--force-recreate", *(["--build"] if phase == "primary" else []), "api")
            wait_for("profile API", lambda: lab.http(paths[0]))
            measure(lab, paths, args.workers, min(args.seconds, 3))
            time.sleep(1.2)
            observed_nodes = ("primary", *NODES) if phase == "replicas" else nodes
            before = scans(lab, observed_nodes)
            phases[phase] = measure(lab, paths, args.workers, args.seconds)
            time.sleep(1.2)
            after = scans(lab, observed_nodes)
            phases[phase]["users_table_scan_delta"] = {node: after[node] - before[node] for node in observed_nodes}
            lab.save("reads.json", phases)
            if not all(phases[phase]["users_table_scan_delta"][node] > 0 for node in nodes):
                raise RuntimeError(f"Read routing not observed for {phase}")
            if phase == "replicas" and phases[phase]["users_table_scan_delta"]["primary"]:
                raise RuntimeError("Benchmark queries reached primary in replica mode")
            if any(endpoint["errors"] or not endpoint["ok"] for endpoint in phases[phase]["endpoints"].values()):
                raise RuntimeError(f"Invalid HTTP results for {phase}")
        registered = lab.http("/user/register", {
            "first_name": "Replica", "second_name": "SmokeTest", "birthdate": "1990-01-01",
            "gender": "other", "biography": "Write routing check", "city": "Test city",
            "password": "Local-lab-demo-2026",
        })["user_id"]
        registered = str(UUID(registered))
        for node in ("primary", *NODES):
            wait_for(f"registered profile on {node}", lambda: (
                lab.sql(node, f"SELECT id FROM users WHERE id = '{registered}'") == registered
            ))
        phases["registered_profile_on_all_nodes"] = registered
        lab.save("reads.json", phases)
        print("Testing quorum and failover", flush=True)
        failover(lab)
    finally:
        lab.compose("stop")


if __name__ == "__main__":
    main()
