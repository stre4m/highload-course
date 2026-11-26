BEGIN;

ALTER TABLE posts ADD COLUMN IF NOT EXISTS feed_delayed boolean NOT NULL DEFAULT false;
ALTER TABLE friendships ADD COLUMN IF NOT EXISTS added_at timestamptz NOT NULL DEFAULT '-infinity';
ALTER TABLE friendships ALTER COLUMN added_at SET DEFAULT clock_timestamp();

CREATE TABLE IF NOT EXISTS feed_outbox (
    post_id uuid PRIMARY KEY REFERENCES posts (id) ON DELETE CASCADE,
    queued_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX IF NOT EXISTS feed_outbox_queued_idx ON feed_outbox (queued_at, post_id);

CREATE TABLE IF NOT EXISTS feed_targets (
    user_id uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    post_id uuid NOT NULL REFERENCES posts (id) ON DELETE CASCADE,
    PRIMARY KEY (user_id, post_id)
);

CREATE INDEX IF NOT EXISTS feed_targets_post_idx ON feed_targets (post_id);

COMMIT;
