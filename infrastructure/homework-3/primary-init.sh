#!/bin/sh
printf '%s\n' 'host replication appuser all scram-sha-256' >> "$PGDATA/pg_hba.conf"
