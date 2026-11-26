#!/bin/sh
set -eu
if [ ! -s "$PGDATA/PG_VERSION" ]; then
    install -d -m 0700 -o postgres -g postgres "$PGDATA"
    gosu postgres pg_basebackup --pgdata="$PGDATA" --write-recovery-conf --wal-method=stream --checkpoint=fast \
        --dbname="host=primary port=5432 user=$POSTGRES_USER application_name=$REPLICA_NAME"
fi
exec docker-entrypoint.sh postgres -c wal_level=replica -c max_wal_senders=10 -c wal_keep_size=128MB
