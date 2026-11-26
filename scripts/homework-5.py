import argparse
import hashlib
import json
import subprocess
from pathlib import Path
from urllib.error import HTTPError
from urllib.parse import urlsplit
from urllib.request import Request, urlopen
from uuid import UUID, uuid4


def request(base_url, method, path, *, token=None, payload=None, expected=200):
    headers = {"Content-Type": "application/json"}
    if token is not None:
        headers["Authorization"] = f"Bearer {token}"
    body = json.dumps(payload).encode() if payload is not None else None
    try:
        with urlopen(Request(base_url + path, body, headers, method=method), timeout=20) as response:
            status, raw = response.status, response.read()
    except HTTPError as error:
        status, raw = error.code, error.read()
    check(status, expected, f"{method} {path} status")
    return raw


def check(actual, expected, label):
    if actual != expected:
        raise AssertionError(f"{label}: expected {expected!r}, got {actual!r}")


def register(base_url):
    password = uuid4().hex
    reply = request(base_url, "POST", "/user/register", payload={
        "first_name": "Dialog", "second_name": "Test", "birthdate": "2000-01-01",
        "gender": "test", "biography": "HW5 test account", "city": "Test", "password": password,
    })
    return {"id": str(UUID(json.loads(reply)["user_id"])), "password": password}


def login(base_url, account):
    return json.loads(request(base_url, "POST", "/login", payload=account))["token"]


def bucket(first, second):
    pair = b"".join(sorted([UUID(first).bytes, UUID(second).bytes]))
    return int.from_bytes(hashlib.sha256(pair).digest()[:4], "big") % 1024


def send(base_url, token, other_id, message):
    reply = request(base_url, "POST", f"/dialog/{other_id}/send", token=token,
                    payload={"text": message})
    check(reply, b"", "Send response body")


def dialog(base_url, token, other_id):
    return json.loads(request(base_url, "GET", f"/dialog/{other_id}/list", token=token))


def verify_shards(project, first_id, expected):
    statement = ("SELECT count(*) FROM dialog_messages "
                 f"WHERE sender_id = '{UUID(first_id)}';")
    actual = {}
    for name in expected:
        service = f"dialog-{name}"
        container = subprocess.check_output([
            "podman", "ps", "--filter", f"label=io.podman.compose.project={project}",
            "--filter", f"label=com.docker.compose.service={service}", "--format", "{{.ID}}",
        ], text=True).strip()
        if not container or len(container.splitlines()) != 1:
            raise AssertionError(f"Expected one running {service} container in {project}")
        count = subprocess.check_output([
            "podman", "exec", container, "sh", "-c",
            'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1 -Atc "$1"',
            "sh", statement,
        ], text=True)
        actual[name] = int(count.strip())
    check(actual, expected, "Shard counts for a single sender")
    print("PASS physical placement: " + ", ".join(f"dialog-{key}={count}" for key, count in actual.items()))


def before(base_url, project, state_path):
    first = register(base_url)
    first_token = login(base_url, first)
    contacts = {}
    moving = None
    for _ in range(64):
        contact = register(base_url)
        route = bucket(first["id"], contact["id"])
        contacts.setdefault(route % 2, contact)
        if route % 3 == 2:
            moving = contact
        if len(contacts) == 2 and moving is not None:
            break
    if len(contacts) != 2 or moving is None:
        raise AssertionError("Could not find test contacts for both original shards and a moved bucket")
    contacts[bucket(first["id"], moving["id"]) % 2] = moving
    other = next(account for account in contacts.values() if account != moving)
    moving_token = login(base_url, moving)
    other_token = login(base_url, other)

    path = f"/dialog/{moving['id']}"
    request(base_url, "GET", path + "/list", expected=401)
    request(base_url, "POST", path + "/send", payload={"text": "Unauthorized"}, expected=401)
    for invalid_id in ("invalid-uuid", first["id"], str(uuid4())):
        invalid_path = f"/dialog/{invalid_id}"
        request(base_url, "GET", invalid_path + "/list", token=first_token, expected=400)
        request(base_url, "POST", invalid_path + "/send", token=first_token,
                payload={"text": "Invalid partner"}, expected=400)
    for body in ({}, {"text": None}, {"text": " "}, {"text": "a\u0000b"}, {"text": "a" * 4097}):
        request(base_url, "POST", path + "/send", token=first_token, payload=body, expected=400)
    check(dialog(base_url, first_token, moving["id"]), [], "Empty dialog")
    print("PASS authorization, validation, and empty dialog")

    text = "Первое сообщение: 'кавычки', эмодзи 🙂"
    send(base_url, first_token, moving["id"], text)
    send(base_url, moving_token, first["id"], "Ответ")
    send(base_url, first_token, other["id"], "Другой диалог")
    expected = [
        {"from": first["id"], "to": moving["id"], "text": text},
        {"from": moving["id"], "to": first["id"], "text": "Ответ"},
    ]
    check(dialog(base_url, first_token, moving["id"]), expected, "Sender reads both directions")
    check(dialog(base_url, moving_token, first["id"]), expected, "Recipient reads the same dialog")
    check(dialog(base_url, other_token, moving["id"]), [], "Third account sees its own empty dialog")
    print("PASS bidirectional delivery, exact response, and dialog isolation")
    verify_shards(project, first["id"], {"a": 1, "b": 1, "c": 0})
    state_path.parent.mkdir(parents=True, exist_ok=True)
    state_path.write_text(json.dumps({"first": first, "moving": moving, "expected": expected}),
                          encoding="utf-8")


def after(base_url, project, state_path):
    state = json.loads(state_path.read_text(encoding="utf-8"))
    first, moving, expected = state["first"], state["moving"], state["expected"]
    first_token, moving_token = login(base_url, first), login(base_url, moving)
    check(dialog(base_url, first_token, moving["id"]), expected, "History immediately after activation")
    text = "После переключения"
    send(base_url, first_token, moving["id"], text)
    combined = expected + [{"from": first["id"], "to": moving["id"], "text": text}]
    check(dialog(base_url, first_token, moving["id"]), combined, "Sender sees merged history")
    check(dialog(base_url, moving_token, first["id"]), combined, "Recipient sees merged history")
    print("PASS history and new messages visible from both sides after activation")
    verify_shards(project, first["id"], {"a": 1, "b": 1, "c": 1})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("phase", choices=("before", "after"))
    parser.add_argument("--url", default="http://127.0.0.1:5085")
    parser.add_argument("--project", default="social-network-homework-5")
    parser.add_argument("--state", type=Path, default=Path("TestResults/hw5-integration/state.json"))
    args = parser.parse_args()
    endpoint = urlsplit(args.url)
    if endpoint.scheme != "http" or endpoint.hostname != "127.0.0.1" or endpoint.path not in ("", "/"):
        parser.error("--url must be a local loopback HTTP origin")
    phase = before if args.phase == "before" else after
    phase(args.url.rstrip("/"), args.project, args.state)


if __name__ == "__main__":
    main()
