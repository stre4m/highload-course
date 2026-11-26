CREATE TABLE dialog_routes (
    bucket integer NOT NULL CHECK (bucket BETWEEN 0 AND 1023),
    generation integer NOT NULL CHECK (generation > 0),
    shard text NOT NULL CHECK (shard <> ''),
    PRIMARY KEY (bucket, generation)
);

INSERT INTO dialog_routes (bucket, generation, shard)
SELECT bucket, 1, CASE bucket % 2 WHEN 0 THEN 'dialog-a' ELSE 'dialog-b' END
FROM generate_series(0, 1023) AS series(bucket);
