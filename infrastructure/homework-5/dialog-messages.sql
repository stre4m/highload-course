CREATE TABLE dialog_messages (
    bucket integer NOT NULL CHECK (bucket BETWEEN 0 AND 1023),
    user_low uuid NOT NULL,
    user_high uuid NOT NULL,
    message_id uuid NOT NULL,
    sender_id uuid NOT NULL,
    sent_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    text text NOT NULL CHECK (char_length(text) BETWEEN 1 AND 4096),
    PRIMARY KEY (bucket, user_low, user_high, message_id),
    CHECK (user_low < user_high),
    CHECK (sender_id IN (user_low, user_high))
);
