import argparse
import json
from pathlib import Path
from urllib.error import HTTPError
from urllib.parse import urlparse
from urllib.request import Request, urlopen
from uuid import UUID, uuid4


class Api:
    def __init__(self, base_url):
        self.base_url = base_url.rstrip("/")

    def request(self, method, path, token=None, payload=None, expected=200):
        headers = {}
        if token is not None:
            headers["Authorization"] = f"Bearer {token}"
        body = None
        if payload is not None:
            headers["Content-Type"] = "application/json"
            body = json.dumps(payload).encode("utf-8")
        request = Request(self.base_url + path, data=body, headers=headers, method=method)
        try:
            with urlopen(request, timeout=30) as response:
                status, raw = response.status, response.read()
        except HTTPError as error:
            status, raw = error.code, error.read()
        if status != expected:
            raise AssertionError(f"{method} {path}: expected {expected}, got {status}: {raw[:500]!r}")
        return json.loads(raw) if raw and expected == 200 else None

    def register(self):
        password = uuid4().hex
        response = self.request("POST", "/user/register", payload={
            "first_name": "Feed", "second_name": "Test", "birthdate": "2000-01-01",
            "gender": "test", "biography": "HW4 integration fixture", "city": "Test",
            "password": password,
        })
        return {"id": response["user_id"], "password": password}

    def login(self, account):
        return self.request("POST", "/login", payload=account)["token"]

    def create_post(self, token, content):
        post_id = self.request("POST", "/post/create", token, {"text": content})
        UUID(post_id)
        return post_id

    def feed(self, token, query=""):
        return self.request("GET", "/post/feed" + query, token)


def require_equal(actual, expected, label):
    if actual != expected:
        raise AssertionError(f"{label}: expected {repr(expected)[:400]}, got {repr(actual)[:400]}")


def verify(api):
    viewer, first_author, second_author, outsider = (api.register() for _ in range(4))
    viewer_token, first_token, second_token, outsider_token = (
        api.login(account) for account in (viewer, first_author, second_author, outsider)
    )

    api.request("GET", "/post/feed", expected=401)
    for query in ("?offset=-1", "?limit=0", "?limit=1.5"):
        api.request("GET", "/post/feed" + query, viewer_token, expected=400)
    api.request("POST", "/post/create", first_token, {"text": None}, expected=400)
    api.request("PUT", f"/friend/set/{viewer['id']}", viewer_token, expected=400)
    require_equal(api.feed(viewer_token), [], "Empty feed")
    print("PASS auth, invalid parameters and empty feed", flush=True)

    api.request("PUT", f"/friend/set/{first_author['id']}", viewer_token)
    require_equal(api.feed(viewer_token), [], "Subscribed author without posts")
    posts = [api.create_post(first_token, f"Первый автор, запись {number}")
             for number in range(1, 1005)]
    first_author_feed = api.feed(viewer_token)
    require_equal([post["id"] for post in first_author_feed], list(reversed(posts[-10:])),
                  "Create invalidates cached empty feed")
    second_post = api.create_post(second_token, "Второй автор")
    unrelated = api.create_post(outsider_token, "Пост стороннего автора")
    require_equal(api.feed(viewer_token), first_author_feed, "Unrelated authors keep feed contents")
    api.request("PUT", f"/friend/set/{second_author['id']}", viewer_token)
    all_posts = api.feed(viewer_token, "?limit=2000")
    ordered_ids = [second_post, *reversed(posts)]
    require_equal([post["id"] for post in all_posts], ordered_ids[:1000], "Latest 1000")
    require_equal([post["id"] for post in api.feed(viewer_token)], ordered_ids[:10], "Default limit")
    require_equal(api.feed(viewer_token, "?offset=997&limit=10"), all_posts[997:], "Last page")
    require_equal(api.feed(viewer_token, "?offset=1000"), [], "Outside cached range")
    require_equal(api.feed(viewer_token, "?offset=2147483647&limit=2147483647"), [],
                  "Large pagination values")
    require_equal(api.feed(first_token), [], "Friendship direction")
    require_equal(any(post["id"] == unrelated for post in all_posts), False, "Unrelated author")
    require_equal(all_posts[0], {"id": second_post, "text": "Второй автор",
                                "author_user_id": second_author["id"]}, "Response fields")
    require_equal(api.request("GET", f"/post/get/{second_post}"), all_posts[0], "Public post")
    print("PASS latest 1000 from two authors, ordering and pagination", flush=True)

    api.request("PUT", f"/friend/add/{first_author['id']}", outsider_token)
    require_equal(api.feed(outsider_token), first_author_feed, "Second subscriber cache")
    edited = {"id": posts[-1], "text": "Изменённый текст друга"}
    api.request("PUT", "/post/update", outsider_token, edited, expected=400)
    api.request("PUT", f"/post/delete/{posts[-1]}", outsider_token, expected=400)
    require_equal(api.feed(viewer_token), all_posts[:10], "Rejected writes preserve the cached feed")
    api.request("PUT", "/post/update", first_token, edited)
    warm_feed = api.feed(viewer_token)
    require_equal(warm_feed[1], {**edited, "author_user_id": first_author["id"]}, "Edit in warm feed")
    require_equal(api.feed(outsider_token)[0], warm_feed[1], "Edit invalidates both subscribers")
    require_equal([post["id"] for post in warm_feed], ordered_ids[:10], "Edit keeps position")
    api.request("PUT", f"/post/delete/{posts[-1]}", first_token)
    api.request("GET", f"/post/get/{posts[-1]}", expected=404)
    ordered_ids.remove(posts[-1])
    after_delete = api.feed(viewer_token, "?limit=1000")
    require_equal([post["id"] for post in after_delete], ordered_ids[:1000], "Delete fills vacated slot")
    api.request("PUT", f"/friend/delete/{second_author['id']}", viewer_token)
    ordered_ids.remove(second_post)
    require_equal([post["id"] for post in api.feed(viewer_token, "?limit=1000")],
                  ordered_ids[:1000], "Unfriend removes posts and fills vacated slot")
    newest = api.create_post(first_token, "Пост после прогрева кеша")
    final_feed = api.feed(viewer_token, "?limit=1000")
    require_equal([post["id"] for post in final_feed], [newest, *ordered_ids[:999]],
                  "Create invalidates warm feed")
    print("PASS ownership, edit, delete, unfriend, backfill and create after cache warmup", flush=True)
    return {"viewer": viewer, "token": viewer_token, "feed": final_feed}


def main():
    parser = argparse.ArgumentParser(
        description="Run HW4 integration checks against an isolated local API."
    )
    parser.add_argument("base_url")
    parser.add_argument("state", type=Path)
    phase = parser.add_mutually_exclusive_group()
    phase.add_argument("--warm-cache", action="store_true",
                       help="Check the cached feed after PostgreSQL stops, without logging in.")
    phase.add_argument("--rebuild", action="store_true",
                       help="Compare the rebuilt feed to the snapshot after an external API restart.")
    args = parser.parse_args()
    url = urlparse(args.base_url)
    if url.scheme != "http" or url.hostname not in ("127.0.0.1", "::1", "localhost"):
        parser.error("base_url must use http on a loopback address")
    api = Api(args.base_url)
    if args.warm_cache:
        state = json.loads(args.state.read_text(encoding="utf-8"))
        require_equal(api.feed(state["token"], "?limit=1000"), state["feed"], "Warm feed")
        print("PASS all 1000 items match the cached feed without logging in")
        return

    if args.rebuild:
        state = json.loads(args.state.read_text(encoding="utf-8"))
        token = api.login(state["viewer"])
        require_equal(api.feed(token, "?limit=1000"), state["feed"], "Cold feed after restart")
        print("PASS all 1000 items match the pre-restart feed")
        return

    state = verify(api)
    args.state.parent.mkdir(parents=True, exist_ok=True)
    args.state.write_text(json.dumps(state, ensure_ascii=False), encoding="utf-8")
    print(f"Saved feed snapshot to {args.state} for the restart check")


if __name__ == "__main__":
    main()
