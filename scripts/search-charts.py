"""Графики нагрузочного эксперимента ДЗ №2."""
import argparse
import json
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt


def chart(directory, phase, rows, metric):
    fig, ax = plt.subplots(figsize=(7.5, 4.4), layout="constrained")
    clients = [row["concurrency"] for row in rows]
    if metric == "latency":
        for percentile in ("p50", "p95", "p99"):
            ax.plot(clients, [row[percentile] for row in rows], marker="o", label=percentile)
        ax.legend()
        ax.set_ylabel("Задержка успешного ответа, мс")
    else:
        ax.plot(clients, [row["rps"] for row in rows], marker="o")
        ax.set_ylabel("Успешных ответов в секунду")
    ax.set_xscale("log")
    ax.set_xticks(clients, labels=clients)
    ax.set_ylim(bottom=0)
    ax.set_xlabel("Одновременные HTTP-клиенты")
    ax.grid(alpha=0.25)
    ax.set_title("До создания индекса" if phase == "before" else "После создания индекса")
    fig.savefig(directory / f"{phase}-{metric}.png", dpi=150)
    plt.close(fig)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("metrics", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    measurements = json.loads(args.metrics.read_text(encoding="utf-8"))
    for phase in ("before", "after"):
        rows = measurements[phase]
        if [row["concurrency"] for row in rows] != [1, 10, 100, 1000]:
            raise ValueError(f"Incomplete measurements: {phase}")
    args.output.mkdir(parents=True, exist_ok=True)
    for phase, rows in measurements.items():
        for metric in ("latency", "throughput"):
            chart(args.output, phase, rows, metric)
