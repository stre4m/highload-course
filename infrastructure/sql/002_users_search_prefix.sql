CREATE INDEX IF NOT EXISTS users_search_prefix_idx
ON users USING btree (second_name varchar_pattern_ops, first_name varchar_pattern_ops);
