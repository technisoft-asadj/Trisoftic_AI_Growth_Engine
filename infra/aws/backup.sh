#!/bin/bash
# Nightly backup of everything that cannot be rebuilt: the database, the Data Protection keys (they decrypt the stored Facebook tokens) and the uploaded files.
# Runs on the EC2 host as root from /etc/cron.d/growth-backup. Uses the instance role for S3, so no keys are stored on the server.
#   BACKUP_BUCKET   (required) S3 bucket; a lifecycle rule on it decides how long files are kept
#   BACKUP_KEEP_LOCAL  how many nightly copies to keep on disk (default 3)
# Restore: see infra/aws/restore.md
set -euo pipefail
: "${BACKUP_BUCKET:?set BACKUP_BUCKET}"
KEEP="${BACKUP_KEEP_LOCAL:-3}"
DIR=/var/backups/growth
STAMP="$(date -u +%Y-%m-%dT%H%M%SZ)"
OUT="$DIR/$STAMP"
mkdir -p "$OUT"; chmod 700 "$DIR" "$OUT"
cd /opt/growth
COMPOSE=(docker compose -f docker-compose.prod.yml -f aws/docker-compose.aws.yml --env-file prod.env -p growth)

fail() { echo "BACKUP FAILED: $1" | logger -t growth-backup; echo "BACKUP FAILED: $1" >&2; rm -rf "$OUT"; exit 1; }

"${COMPOSE[@]}" exec -T postgres pg_dump -U growth -d growthengine -Fc > "$OUT/growth.dump" || fail "pg_dump"
[ -s "$OUT/growth.dump" ] || fail "the dump is empty"
# a dump that cannot be listed cannot be restored either
"${COMPOSE[@]}" exec -T postgres pg_restore --list < "$OUT/growth.dump" > /dev/null || fail "the dump is not readable"
docker run --rm -v growth_keys:/v:ro -v "$OUT":/out alpine tar czf /out/keys.tgz -C /v . || fail "keys"
docker run --rm -v growth_objects:/v:ro -v "$OUT":/out alpine tar czf /out/objects.tgz -C /v . || fail "uploaded files"

aws s3 cp "$OUT" "s3://$BACKUP_BUCKET/$STAMP/" --recursive --only-show-errors --sse AES256 || fail "upload to S3"
# prove it arrived: the object must exist with the same size
LOCAL=$(stat -c %s "$OUT/growth.dump")
REMOTE=$(aws s3api head-object --bucket "$BACKUP_BUCKET" --key "$STAMP/growth.dump" --query ContentLength --output text) || fail "the upload cannot be found"
[ "$LOCAL" = "$REMOTE" ] || fail "the upload has a different size ($REMOTE, expected $LOCAL)"

# keep only the newest few copies on disk
ls -1dt "$DIR"/*/ 2>/dev/null | tail -n +$((KEEP + 1)) | xargs -r rm -rf
echo "backup ok: s3://$BACKUP_BUCKET/$STAMP ($LOCAL bytes of database)" | logger -t growth-backup
echo "backup ok: s3://$BACKUP_BUCKET/$STAMP"
