"""Измерение HTTP-нагрузки из WSL Ubuntu через Apache JMeter."""
import csv
import math
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
WSL = ["wsl", "-d", "Ubuntu", "--"]
PORT = 5083
RAMP = 5
WARMUP = 3


def run_wsl(*args):
    return subprocess.check_output([*WSL, *args], cwd=ROOT, encoding="utf-8").strip()


def gateway():
    route = run_wsl("ip", "-4", "route", "show", "default")
    return route.split()[2]


def measure(host, directory, phase, concurrency, duration):
    name = f"{phase}-{concurrency}"
    result = directory / f"{name}.jtl"
    selected = directory / f"{name}-measured.jtl"
    relative = directory.resolve().relative_to(ROOT).as_posix()
    command = [
        *WSL, "jmeter", "-n", "-t", "scripts/search-jmeter.jmx",
        "-l", f"{relative}/{result.name}", "-j", f"{relative}/{name}-jmeter.log",
        f"-Jhost={host}", f"-Jport={PORT}", f"-Jthreads={concurrency}",
        f"-Jramp={RAMP}", f"-Jduration={RAMP + WARMUP + duration}",
        "-Jjmeter.save.saveservice.output_format=csv",
        "-Jjmeter.save.saveservice.print_field_names=true",
        "-Jjmeter.save.saveservice.timestamp_format=ms",
        "-Jsampleresult.timestamp.start=true",
        "-Jjmeter.save.saveservice.thread_counts=true",
    ]
    if result.exists():
        raise RuntimeError(f"JMeter results already exist: {result}")
    with (directory / f"{name}-console.log").open("wb") as log:
        subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, check=True)
    with result.open(encoding="utf-8", newline="") as stream:
        reader = csv.DictReader(stream)
        rows = list(reader)
        fields = reader.fieldnames
    if not rows or max(int(row["grpThreads"]) for row in rows) != concurrency:
        raise RuntimeError(f"JMeter did not reach {concurrency} threads: {result}")
    start = min(int(row["timeStamp"]) for row in rows) + (RAMP + WARMUP) * 1000
    end = start + duration * 1000
    measured = [row for row in rows if start <= int(row["timeStamp"]) < end]
    latency = sorted(int(row["elapsed"]) for row in measured
                     if row["success"] == "true" and row["responseCode"] == "200")
    if not latency:
        raise RuntimeError(f"No successful requests in the measurement window: {result}")
    stop = max(end, max(int(row["timeStamp"]) + int(row["elapsed"]) for row in measured))
    seconds = (stop - start) / 1000
    with selected.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows(measured)

    def percentile(rank):
        return latency[math.ceil(rank * len(latency)) - 1]

    return {"concurrency": concurrency, "seconds": seconds, "successes": len(latency),
            "errors": len(measured) - len(latency), "rps": len(latency) / seconds,
            "p50": percentile(.50), "p95": percentile(.95), "p99": percentile(.99)}
