# Restoring from a backup

Each night `backup.sh` puts three files in `s3://<backup bucket>/<timestamp>/`:

| File | What it is |
|---|---|
| `growth.dump` | The whole database (Postgres custom format) |
| `keys.tgz` | The Data Protection keys. Without them the stored Facebook token cannot be decrypted and the Page has to be connected again. |
| `objects.tgz` | The uploaded pictures and videos |

Keep the three files of one night together. The bucket is private and encrypted, and a lifecycle rule deletes copies after 30 days.

## Restore on a fresh or broken server

Run on the server in `/opt/growth` (the stack has been deployed once, so images and `prod.env` exist):

    STAMP=2026-11-21T020000Z         # pick one from: aws s3 ls s3://BUCKET/
    mkdir -p /tmp/restore && cd /tmp/restore
    aws s3 cp s3://BUCKET/$STAMP/ . --recursive
    cd /opt/growth
    C="docker compose -f docker-compose.prod.yml -f aws/docker-compose.aws.yml --env-file prod.env -p growth"
    $C stop api worker
    $C exec -T postgres psql -U growth -d postgres -c "DROP DATABASE IF EXISTS growthengine WITH (FORCE)"
    $C exec -T postgres psql -U growth -d postgres -c "CREATE DATABASE growthengine OWNER growth"
    $C exec -T postgres pg_restore -U growth -d growthengine --no-owner < /tmp/restore/growth.dump
    docker run --rm -v growth_keys:/v -v /tmp/restore:/in alpine sh -c "rm -rf /v/* && tar xzf /in/keys.tgz -C /v"
    docker run --rm -v growth_objects:/v -v /tmp/restore:/in alpine sh -c "rm -rf /v/* && tar xzf /in/objects.tgz -C /v"
    $C up -d --no-build api worker

Then sign in and check the Autopilot page. The autopilot continues from the saved state.

## Test it

A backup nobody has restored is only a hope. Once a quarter, restore the newest backup into a scratch database
(`CREATE DATABASE restoretest`, then `pg_restore -d restoretest`) and count the rows in `publishing.published_posts`.
