BEGIN;

INSERT INTO dialog_routes (bucket, generation, shard)
SELECT bucket, 2, CASE WHEN bucket % 3 = 2 THEN 'dialog-c' ELSE shard END
FROM dialog_routes
WHERE generation = 1
ON CONFLICT (bucket, generation) DO NOTHING;

COMMIT;
