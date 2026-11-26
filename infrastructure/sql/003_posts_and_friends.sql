BEGIN;

CREATE TABLE IF NOT EXISTS posts (
    id uuid PRIMARY KEY,
    author_id uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    text text NOT NULL CHECK (char_length(text) BETWEEN 1 AND 4096),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX IF NOT EXISTS posts_author_created_idx
    ON posts (author_id, created_at DESC, id DESC);

CREATE TABLE IF NOT EXISTS friendships (
    user_id uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    friend_id uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    PRIMARY KEY (user_id, friend_id),
    CHECK (user_id <> friend_id)
);

CREATE INDEX IF NOT EXISTS friendships_friend_user_idx
    ON friendships (friend_id, user_id);

COMMIT;
